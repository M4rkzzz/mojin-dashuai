using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Boshan.Desktop;

// Opt-in only. Callers pass fixed stage names and safe numeric/enum metadata,
// never exception messages, paths, account objects, URLs or bridge parameters.
internal static class StartupDiagnostics
{
    internal const string DirectoryVariable = "MOJIN_STARTUP_DIAGNOSTICS_DIR";
    internal static bool Enabled { get; private set; }
    internal static bool Compatibility { get; private set; }
    internal static bool CanWrite => writer is not null;
    internal static string? DirectoryPath { get; private set; }
    private static readonly object gate = new();
    private static readonly ConcurrentDictionary<long, Activity> activities = new();
    private static readonly long started = Stopwatch.GetTimestamp();
    private static StreamWriter? writer;
    private static System.Threading.Timer? timer;
    private static Dispatcher? dispatcher;
    private static long lastUiResponse, nextActivity, writtenBytes;
    private static int uiPending, stopped;

    [ModuleInitializer]
    internal static void ModuleEntry()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Enabled = true;
        Compatibility = Environment.GetEnvironmentVariable("MOJIN_STARTUP_COMPATIBILITY") == "1";
        try
        {
            DirectoryPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(DirectoryPath);
            writer = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "startup.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { /* Diagnostics must not turn an unwritable log directory into a startup crash. */ }
        Write("process.module-entry", new
        {
            framework = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            osVersion = Environment.OSVersion.Version.ToString(),
            compatibility = Compatibility,
            version = typeof(StartupDiagnostics).Assembly.GetName().Version?.ToString()
        });
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Error("exception.app-domain", error);
            else Record("exception.app-domain.non-exception");
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Error("exception.unobserved-task", e.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop("process.exit");
        timer = new System.Threading.Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    internal static void AttachDispatcher(Dispatcher value)
    {
        if (!Enabled) return;
        dispatcher = value;
        Interlocked.Exchange(ref lastUiResponse, Stopwatch.GetTimestamp());
        Record("ui.dispatcher.attached");
    }

    internal static IDisposable Begin(string name, bool network = false)
    {
        if (!Enabled) return EmptyScope.Instance;
        var activity = new Activity(Interlocked.Increment(ref nextActivity), name, network);
        activities[activity.Id] = activity;
        Write("stage.begin", new { name, network });
        return activity;
    }

    internal static void Record(string name) { if (Enabled) Write(name, null); }
    internal static void Record(string name, object safeMetadata) { if (Enabled) Write(name, safeMetadata); }
    internal static void Error(string name, Exception error)
    {
        if (!Enabled) return;
        var causes = new List<object>();
        for (Exception? current = error; current is not null && causes.Count < 8; current = current.InnerException)
        {
            // StackTrace(false) deliberately excludes source filenames and user profile paths.
            var stack = new StackTrace(current, false).ToString();
            if (stack.Length > 20000) stack = stack[..20000];
            causes.Add(new { type = current.GetType().FullName, hResult = current.HResult, stack });
        }
        Write(name, new { causes });
    }

    private static void Heartbeat()
    {
        try
        {
            if (Volatile.Read(ref stopped) != 0) return;
            var current = dispatcher;
            var last = Interlocked.Read(ref lastUiResponse);
            var lag = last == 0 ? (long?)null : (long)Stopwatch.GetElapsedTime(last).TotalMilliseconds;
            Write("heartbeat", new
            {
                ui = current is null ? "not-attached" : current.HasShutdownStarted ? "shutting-down" : lag > 6000 ? "not-responding" : "responsive",
                uiResponseAgeMs = lag,
                activeStages = activities.Values.OrderBy(item => item.Id).Take(32).Select(item => new { name = item.Name, network = item.Network, elapsedMs = (long)Stopwatch.GetElapsedTime(item.Started).TotalMilliseconds }).ToArray()
            });
            if (current is not null && !current.HasShutdownStarted && Interlocked.CompareExchange(ref uiPending, 1, 0) == 0)
            {
                try
                {
                    current.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        Interlocked.Exchange(ref lastUiResponse, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref uiPending, 0);
                    }));
                }
                catch { Interlocked.Exchange(ref uiPending, 0); }
            }
        }
        catch { /* A heartbeat is observational and must never crash the launcher. */ }
    }

    internal static void Stop(string reason)
    {
        if (!Enabled || Interlocked.Exchange(ref stopped, 1) != 0) return;
        timer?.Dispose();
        Write(reason, null);
        lock (gate) { try { writer?.Dispose(); } catch { } writer = null; }
    }

    private static void Write(string name, object? data)
    {
        try
        {
            lock (gate)
            {
                if (writer is null || writtenBytes >= 16 * 1024 * 1024) return;
                var line = JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, pid = Environment.ProcessId, thread = Environment.CurrentManagedThreadId, @event = name, data });
                writer.WriteLine(line);
                writtenBytes += Encoding.UTF8.GetByteCount(line) + 1;
            }
        }
        catch { /* Preserve application behavior if diagnostics storage becomes unavailable. */ }
    }

    private sealed class Activity(long id, string name, bool network) : IDisposable
    {
        internal readonly long Id = id, Started = Stopwatch.GetTimestamp();
        internal readonly string Name = name;
        internal readonly bool Network = network;
        public void Dispose()
        {
            if (activities.TryRemove(Id, out _)) Write("stage.end", new { name = Name, elapsedMs = (long)Stopwatch.GetElapsedTime(Started).TotalMilliseconds });
        }
    }
    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
