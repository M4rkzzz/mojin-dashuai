using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Boshan.Launcher;
using Microsoft.Win32.SafeHandles;

namespace Boshan.Desktop;

/// <summary>A pipe belongs to one game process and its original account for its entire lifetime.</summary>
public sealed class GameJoinSession : IDisposable
{
    private readonly string pipeName="mojin-join-"+Guid.NewGuid().ToString("N");
    private readonly string instance;
    private readonly Func<CancellationToken,Task<JoinTicket>> ticket;
    private readonly CancellationTokenSource stop=new();
    private readonly TaskCompletionSource<Process> attached=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private NamedPipeServerStream? pipe;
    public GameJoinOptions Options{get;}
    public GameJoinSession(string instancePath,string instance,Func<CancellationToken,Task<JoinTicket>> ticket)
    {
        this.instance=instance;this.ticket=ticket;
        using var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mojin.JoinAgent")??throw new FileNotFoundException("入服认证组件缺失，请更新统一客户端。");
        using var buffer=new MemoryStream();source.CopyTo(buffer);var data=buffer.ToArray();var sha=Convert.ToHexStringLower(SHA256.HashData(data));
        var relative=".hub/join/agent-"+sha+".jar";var path=Path.Combine(instancePath,relative.Replace('/',Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if(!File.Exists(path)||!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(data)))
        {var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllBytes(temporary,data);File.Move(temporary,path,true);}
        Options=new(relative,pipeName,instance);
        worker=Task.Run(Serve);
    }
    public void Attach(Process process)=>attached.TrySetResult(process);
    private async Task Serve()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                using var current=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,4096,4096);
                pipe=current;
                await current.WaitForConnectionAsync(stop.Token);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    var process=await attached.Task.WaitAsync(timeout.Token);
                    if(process.HasExited||!GetNamedPipeClientProcessId(current.SafePipeHandle,out var pid)||pid!=(uint)process.Id)continue;
                    using var reader=new StreamReader(current,new UTF8Encoding(false,true),false,1024,true);
                    using var writer=new StreamWriter(current,new UTF8Encoding(false),1024,true){AutoFlush=true,NewLine="\n"};
                    var text=new StringBuilder();var character=new char[1];
                    while(true)
                    {
                        if(await reader.ReadAsync(character.AsMemory(),timeout.Token)==0)throw new IOException("Pipe closed");
                        if(character[0]=='\n')break;
                        if(text.Length>=512)throw new InvalidDataException("Invalid pipe request");
                        text.Append(character[0]);
                    }
                    using var request=JsonDocument.Parse(text.ToString());
                    if(!request.RootElement.TryGetProperty("instance",out var target)||target.GetString()!=instance)throw new InvalidDataException("Invalid instance");
                    object result;
                    // Leave time to deliver a classified service timeout before Java's
                    // 20-second pipe deadline. A stalled provider cannot leave a valid
                    // local connection looking like a missing launcher.
                    using var ticketTimeout=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    ticketTimeout.CancelAfter(TimeSpan.FromSeconds(18));
                    try{result=await ticket(ticketTimeout.Token).WaitAsync(ticketTimeout.Token);}
                    catch(OperationCanceledException) when(stop.IsCancellationRequested){throw;}
                    catch(Exception ex){result=JoinAuthenticationErrors.From(ex);}
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result,new JsonSerializerOptions(Json.Options){WriteIndented=false}).AsMemory(),timeout.Token);
                }
                catch(Exception ex) when(ex is IOException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException){ }
                finally{pipe=null;}
            }
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){ }
    }
    public void Dispose(){if(stop.IsCancellationRequested)return;stop.Cancel();pipe?.Dispose();}
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint processId);
}
