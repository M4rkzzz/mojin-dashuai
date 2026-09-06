using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Boshan.Desktop;
using Boshan.Launcher;

var root=Path.GetFullPath(args[0]);
var work=Path.Combine(root,".local","join-errors-check");Directory.CreateDirectory(work);
var classifier=typeof(GameJoinSession).Assembly.GetType("Boshan.Desktop.JoinAuthenticationErrors")!.GetMethod("From",BindingFlags.NonPublic|BindingFlags.Static)!;
foreach(var (message,code) in new[]{("微软登录凭据已失效，请重新登录。","login_expired"),("此微软账号未拥有 Minecraft Java 版。","ownership_required"),("Minecraft 所有权验证暂时不可用，请稍后重试。","service_unavailable")})
{
    var classified=classifier.Invoke(null,[new InvalidDataException(message)])!;
    if((string?)classified.GetType().GetProperty("ErrorCode")!.GetValue(classified)!=code)throw new InvalidOperationException("Silent Microsoft error classification differs");
}
Console.WriteLine("PASS Microsoft silent-restore error classification");
if(args.Length==2&&args[1]=="--mapping-only")return;
var classes=Path.Combine(work,"classes");Directory.CreateDirectory(classes);
var prepared=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,".local/loading-live-20260906/prepared.json"))).RootElement;
var javac=Directory.GetFiles(Path.Combine(root,"..",".tools","temurin25"),"javac.exe",SearchOption.AllDirectories).Single();
var sourceRoot=Path.Combine(root,"tests/game-integration/join/errors");
using(var compile=new Process{StartInfo=new(javac){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}})
{
    foreach(var arg in new[]{"--release","8","-encoding","UTF-8","-d",classes,Path.Combine(sourceRoot,"C00Handshake.java"),Path.Combine(sourceRoot,"JoinErrorCheck.java")})compile.StartInfo.ArgumentList.Add(arg);
    compile.Start();var output=compile.StandardOutput.ReadToEndAsync();var error=compile.StandardError.ReadToEndAsync();await compile.WaitForExitAsync();await output;await error;
    if(compile.ExitCode!=0)throw new InvalidOperationException("Java error fixture did not compile");
}
const string secret="DO_NOT_EXPOSE password=SECRET https://internal.example/private TOKEN_PRIVATE";
async Task<Exception> ApiFailure(HttpStatusCode status,string? message=null)
{
    using var response=new HttpResponseMessage(status){Content=new StringContent(JsonSerializer.Serialize(new{error=message??secret}),System.Text.Encoding.UTF8,"application/json")};
    var method=typeof(Accounts).GetMethod("Check",BindingFlags.NonPublic|BindingFlags.Static)!;
    try{await (Task)method.Invoke(null,[response])!;}catch(Exception error){return error;}
    throw new InvalidOperationException("Fixture did not reject response");
}
var conflict=await ApiFailure(HttpStatusCode.Conflict);
var login=await ApiFailure(HttpStatusCode.Unauthorized);
var unavailable=await ApiFailure(HttpStatusCode.ServiceUnavailable);
var limited=await ApiFailure(HttpStatusCode.TooManyRequests);
var ownership=await ApiFailure(HttpStatusCode.Unauthorized,"微软账号资料或 Minecraft Java 版所有权验证失败。");
var reports=new List<object>();
async Task Check(string runtime,string name,Exception? failure,string expected,bool missingPipe=false,bool stalledProvider=false)
{
    var java=prepared.GetProperty(runtime).GetProperty("java").GetString()!;
    var requests=0;
    using var session=new GameJoinSession(work,"vw",_=>
    {
        Interlocked.Increment(ref requests);
        if(stalledProvider)return new TaskCompletionSource<JoinTicket>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        return failure is null?Task.FromResult(new JoinTicket(new string('A',43),DateTimeOffset.UtcNow.AddMinutes(2),"JoinAudit","unused")):Task.FromException<JoinTicket>(failure);
    });
    var jar=Path.Combine(work,session.Options.AgentFile.Replace('/',Path.DirectorySeparatorChar));
    using var process=new Process{StartInfo=new(java){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=work}};
    foreach(var arg in new[]{"-javaagent:"+jar,"-Dmojin.join.instance=vw","-Dmojin.join.pipe="+(missingPipe?"mojin-join-"+Guid.NewGuid().ToString("N"):session.Options.PipeName),"-cp",jar+Path.PathSeparator+classes,"JoinErrorCheck",expected})process.StartInfo.ArgumentList.Add(arg);
    var watch=Stopwatch.StartNew();process.Start();session.Attach(process);
    var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(28));}
    catch{if(!process.HasExited)process.Kill();throw;}
    var output=await stdout;var errorOutput=await stderr;
    if(process.ExitCode!=0||!output.Contains("PLAYER_MESSAGE_PASS")||requests!=(missingPipe?0:1))throw new InvalidOperationException("Join message scenario failed: "+runtime+"/"+name);
    if(new[]{"DO_NOT_EXPOSE","SECRET","internal.example","TOKEN_PRIVATE","Join IPC unavailable"}.Any(value=>output.Contains(value)||errorOutput.Contains(value)))throw new InvalidOperationException("Unsafe detail exposed");
    reports.Add(new{runtime,scenario=name,passed=true,requests,elapsedMs=watch.ElapsedMilliseconds});
    Console.WriteLine("PASS "+runtime+" "+name);
}
foreach(var runtime in new[]{"vw","dc2","mb"})
{
    await Check(runtime,"role_conflict",conflict,"此角色的归属或名称存在冲突，请联系管理员核实关联后再连接。");
    await Check(runtime,"login_expired",login,"登录已失效，请回统一客户端重新登录，再重新启动游戏。");
    await Check(runtime,"network",new HttpRequestException(secret),"入服认证服务暂时无法连接，请检查网络后重试；仍失败请联系管理员。");
    await Check(runtime,"valid",null,"SUCCESS");
}
await Check("vw","service_unavailable",unavailable,"入服认证服务暂时无法连接，请检查网络后重试；仍失败请联系管理员。");
await Check("vw","rate_limited",limited,"入服请求过于频繁，请稍后重新连接。");
await Check("vw","ownership",ownership,"登录或 Minecraft Java 版所有权验证未通过，请使用拥有 Java 版的微软账号重新登录。");
await Check("vw","account_changed",new InvalidDataException("启动账号已切换，请从统一客户端重新启动游戏。"),"启动账号已切换，请从统一客户端重新启动游戏。");
await Check("vw","unknown_error_redacted",new InvalidDataException(secret),"入服认证未通过，请回统一客户端检查登录状态；仍失败请联系管理员。");
await Check("vw","stalled_service",null,"入服认证请求超时，请稍后重新连接；仍失败请检查网络。",stalledProvider:true);
await Check("vw","missing_launcher",null,"无法与统一客户端通信，请保持统一客户端运行，并从统一客户端重新启动游戏。",missingPipe:true);
Json.Write(Path.Combine(work,"result.json"),new{passed=true,scope="Actual production GameJoinSession PID-bound pipe plus transformed client handshake in Java 8/17/25; no account credentials or real game launched",checks=reports});
