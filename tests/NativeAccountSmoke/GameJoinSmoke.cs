using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

internal static class GameJoinSmoke
{
    public static async Task Run(string root)
    {
        root=Path.GetFullPath(root);
        var work=Path.Combine(root,".local","join-desktop-check");Directory.CreateDirectory(work);
        var prepared=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,".local","loading-live-20260906","prepared.json"))).RootElement;
        var reports=new List<object>();
        foreach(var id in new[]{"vw","dc2","mb"})
        {
            var java=prepared.GetProperty(id).GetProperty("java").GetString()!;
            var requests=0;
            using var session=new GameJoinSession(work,"vw",_=>{Interlocked.Increment(ref requests);return Task.FromResult(new JoinTicket(new string('A',43),DateTimeOffset.UtcNow.AddMinutes(2),"JoinAudit","unused"));});
            using var process=new Process{StartInfo=new(java){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=work}};
            var sourceJar=Path.Combine(root,"src","GameIntegration","join","mojin-join-agent.jar");
            foreach(var arg in new[]{"-javaagent:"+sourceJar,"-Dmojin.join.pipe="+session.Options.PipeName,"-Dmojin.join.instance=vw","-cp",sourceJar+Path.PathSeparator+Path.Combine(root,".local","join-agent","check","classes"),"JoinPipeCheck"})process.StartInfo.ArgumentList.Add(arg);
            process.Start();session.Attach(process);
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
            catch{if(!process.HasExited)process.Kill();throw;}
            var output=await stdout;await stderr;
            if(process.ExitCode!=0||requests!=1||!output.Contains("PIPE_PASS"))throw new InvalidOperationException("Java / launcher pipe check failed for "+id);
            reports.Add(new{runtime=id,passed=true,processBoundPipe=true,oneLineResponse=true,requests});
        }
        Json.Write(Path.Combine(work,"report.json"),new{passed=true,checks=reports});
        Console.WriteLine("PASS Java 8/17/25 through actual launcher process-bound pipe; no account credentials sent to Java");
    }
}
