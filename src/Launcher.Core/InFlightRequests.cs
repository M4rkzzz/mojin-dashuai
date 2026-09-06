namespace Boshan.Launcher;

/// <summary>Share only active work; cancellation belongs to each caller.</summary>
internal sealed class InFlightRequests<T>
{
    private sealed class Flight
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<T> Task = null!;
        public int Waiters;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Flight> flights = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> Run(string key, Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Flight flight;
        lock (gate)
        {
            if (!flights.TryGetValue(key, out flight!) || flight.Task.IsCompleted)
            {
                flight = new Flight();
                // Run outside the caller's UI context. No completed result is cached here.
                flight.Task = Task.Run(() => action(flight.Cancellation.Token));
                flights[key] = flight;
                _ = flight.Task.ContinueWith(task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            flight.Waiters++;
        }
        try { return await flight.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            bool last;
            lock (gate)
            {
                last = --flight.Waiters == 0;
                if (last && flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) flights.Remove(key);
            }
            if (last)
            {
                // Do not cancel another caller's probe; stop the underlying work once nobody needs it.
                if (!flight.Task.IsCompleted) flight.Cancellation.Cancel();
                _ = flight.Task.ContinueWith(_ => flight.Cancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
