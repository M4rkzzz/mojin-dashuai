using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class InstanceStateSmoke
{
    public static void Run()
    {
        Exception? error=null;var checks=new List<string>();
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"mojin-instance-state-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
            object? Call(MainWindow window,string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,args);
            T Field<T>(MainWindow window,string name)=>(T)typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            MainWindow Window()
            {
                // No Show/Loaded/Initialize call: no native window, WebView process or network.
                var window=new MainWindow();
                typeof(MainWindow).GetField("settings",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,new LauncherSettings{Root=root,ContentDirectoryConfigured=true});
                return window;
            }
            try
            {
                var first=Window();var release=new ReleaseRef("fixture",1,"https://example.invalid/manifest",new string('a',64),"fixture");
                Field<HashSet<string>>(first,"launchAfterDownload").Add("dc2");
                Call(first,"TrackDownload","dc2",release);Call(first,"TrackDownload","mb",release);
                Call(first,"SaveDownloadProgress",new TransferProgress("dc2","正在下载世界内容",636,1500,3),true);
                Call(first,"SaveDownloadProgress",new TransferProgress("mb","正在下载世界内容",205,900,2),true);
                var restored=Window();Call(restored,"RestoreDownloads");
                var tasks=Field<Dictionary<string,ResumeDownload>>(restored,"savedDownloads");
                var progress=Field<Dictionary<string,TransferProgress>>(restored,"transferProgress");
                Check(tasks.Count==2,"Two independent tasks survive launcher recreation");
                Check(progress["dc2"].Completed==636&&progress["mb"].Completed==205,"Each saved byte count belongs to its own instance");
                Check(progress.Values.All(p=>p.Paused&&p.BytesPerSecond==0),"Restored tasks are paused with no stale speed");
                Check(Field<HashSet<string>>(restored,"launchAfterDownload").SetEquals(["dc2"]),"Download-only and launch-after choices survive independently");
                Check(Field<Dictionary<string,System.Threading.CancellationTokenSource>>(restored,"transfers").Count==0,"Restore starts no transfers or implicit game launch");
                ((Task<object?>)Call(restored,"Dispatch","download.cancel",JsonSerializer.SerializeToElement(new{instance="mb"},Json.Options))!).GetAwaiter().GetResult();
                Check(tasks.ContainsKey("dc2")&&!tasks.ContainsKey("mb"),"Cancelling one durable task preserves the other");
                Check(File.Exists(Path.Combine(root,"instances","dc2",".hub","download.json"))&&!File.Exists(Path.Combine(root,"instances","mb",".hub","download.json")),"Only the cancelled instance checkpoint is removed");
                var held1=new TaskCompletionSource();var held2=new TaskCompletionSource();
                var operation1=(Task)Call(restored,"InstanceOperation","m3e",(Func<Task>)(()=>held1.Task))!;
                var operation2=(Task)Call(restored,"InstanceOperation","mb",(Func<Task>)(()=>held2.Task))!;
                Check(!operation1.IsCompleted&&!operation2.IsCompleted,"Two different instance operations run concurrently");
                var duplicate=(Task)Call(restored,"InstanceOperation","m3e",(Func<Task>)(()=>Task.CompletedTask))!;
                Check(duplicate.IsFaulted,"A duplicate operation for the same instance is rejected");
                Check((string)Call(restored,"InstanceState","m3e")! =="preparing","Active instance exposes preparation state");
                held1.SetResult();held2.SetResult();
                Check(operation1.IsCompletedSuccessfully&&operation2.IsCompletedSuccessfully,"Independent operation guards are released on completion");
                using var process=Process.GetCurrentProcess();
                Json.Write(Path.Combine(root,"instances","m3e",".hub","active-game.json"),new ActiveGame(process.Id,process.StartTime.ToUniversalTime()));
                Check((string)Call(restored,"InstanceState","m3e")! =="running","A live process marker is detected after launcher recreation");
                Json.Write(Path.Combine(root,"instances","m3e",".hub","active-game.json"),new ActiveGame(process.Id,process.StartTime.ToUniversalTime().AddMinutes(-1)));
                Check((string)Call(restored,"InstanceState","m3e")! !="running","A reused process id does not show a stale running state");
                first.Close();restored.Close();
            }
            catch(Exception ex){error=ex;}
            finally
            {
                var resolved=Path.GetFullPath(root);
                if(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("mojin-instance-state-",StringComparison.Ordinal))Directory.Delete(resolved,true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)throw new InvalidOperationException("Instance state smoke failed",error);
        Console.WriteLine(JsonSerializer.Serialize(new{passed=checks.Count,checks}));
    }
}
