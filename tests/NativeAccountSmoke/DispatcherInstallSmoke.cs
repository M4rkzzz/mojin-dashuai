using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class DispatcherInstallSmoke
{
    private sealed class Handler(byte[] archive):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(archive)});
    }
    public static void Run()
    {
        Exception? error=null;object? result=null;
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"mojin-dispatcher-install-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame=new DispatcherFrame();
            dispatcher.BeginInvoke(new Action(async()=>
            {
                try
                {
                    var files=new List<ContentFile>();using var output=new MemoryStream();
                    using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))for(var index=0;index<1200;index++)
                    {
                        var bytes=Encoding.UTF8.GetBytes("small configuration "+index);var path=$"config/fixture/{index}.cfg";
                        files.Add(new(path,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),["https://fixture.invalid/"+path],FilePolicy.Seed,"local fixture"));
                        using var entry=zip.CreateEntry("overrides/"+path).Open();entry.Write(bytes);
                    }
                    var archive=output.ToArray();var bundleFile=new ContentFile("fixture.zip",archive.Length,Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),["https://fixture.invalid/pack"],FilePolicy.Managed,"local fixture");
                    var runtime=new RuntimeSpec("java17",17,"17","windows-x64",bundleFile,"bin/java.exe",1);
                    var manifest=new PackManifest("dc2","fixture",1,"1.20.1","forge","47","fixture",runtime,8192,"fixture",files.ToArray(),["local smoke"],[new(bundleFile,"overrides/")]);
                    using var downloader=new Downloader(Path.Combine(root,"cache"),new LauncherSettings{Root=root},new Handler(archive));
                    using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var callbacks=0;var heartbeats=0;var extractionTicks=0;var phase="";var maximumGap=0L;var clock=Stopwatch.StartNew();var lastTick=0L;
                    var timer=new DispatcherTimer(DispatcherPriority.Input,dispatcher){Interval=TimeSpan.FromMilliseconds(10)};
                    timer.Tick+=(_,_)=>
                    {
                        var now=clock.ElapsedMilliseconds;maximumGap=Math.Max(maximumGap,now-lastTick);lastTick=now;heartbeats++;
                        if(Volatile.Read(ref phase)=="解压世界配置"&&++extractionTicks>=3)cancel.Cancel();
                    };
                    using var progress=new DispatcherTransferProgress(dispatcher,p=>callbacks++,p=>Volatile.Write(ref phase,p.Phase));
                    timer.Start();var cancelled=false;
                    try{await BackgroundInstallation.Run(()=>new TransactionalInstaller(root).Install(manifest,downloader,4,progress,cancel.Token),cancel.Token);}
                    catch(OperationCanceledException)when(cancel.IsCancellationRequested){cancelled=true;}
                    timer.Stop();
                    if(!cancelled||extractionTicks<3||heartbeats<3)throw new InvalidOperationException("Dispatcher did not process cancellation during extraction.");
                    if(maximumGap>1500)throw new InvalidOperationException("Dispatcher heartbeat was blocked for over 1.5 seconds.");
                    var filesExtracted=Directory.EnumerateFiles(Path.Combine(root,"cache")).Count();
                    // Flooding native progress must not create one Dispatcher work item per report.
                    var floodCallbacks=0;
                    using(var flood=new DispatcherTransferProgress(dispatcher,_=>floodCallbacks++))
                    {
                        await BackgroundInstallation.Run(()=>
                        {
                            for(var i=0;i<100000;i++)flood.Report(new("dc2","下载世界内容",i,100000,1));
                            return Task.CompletedTask;
                        });
                        await Task.Delay(250);
                    }
                    if(floodCallbacks>5)throw new InvalidOperationException("Chunk progress flooded the Dispatcher queue.");
                    result=new{passed=true,fixtureFiles=files.Count,filesExtracted,heartbeats,extractionTicks,maximumGapMs=maximumGap,progressCallbacks=callbacks,floodReports=100000,floodCallbacks,cancelledDuringExtraction=cancelled};
                }
                catch(Exception ex){error=ex;}
                finally{frame.Continue=false;}
            }));
            Dispatcher.PushFrame(frame);
            var resolved=Path.GetFullPath(root);
            if(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("mojin-dispatcher-install-",StringComparison.Ordinal))Directory.Delete(resolved,true);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)throw new InvalidOperationException("Dispatcher installation smoke failed",error);
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
