using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DnsClient;
using System.Buffers.Binary;

namespace Boshan.Launcher;

public sealed record RouteEndpoint(string Domain,string Host,int Port,int Latency);
public static class Routes
{
    public static readonly IReadOnlyDictionary<string,string[]> Domains = new Dictionary<string,string[]> {
        ["m3e"]=["mc.m3e.boshan.uk","mc.m3e.bk.boshan.uk"], ["dc2"]=["dc2.mc.boshan.uk","dc2.bk.mc.boshan.uk"], ["mb"]=["mb.mc.boshan.uk","mb.bk.mc.boshan.uk"]
    };
    public static async Task<RouteEndpoint> Resolve(string domain, CancellationToken token = default)
    {
        string host=domain; int port=25565;
        try
        {
            var dns=new LookupClient(new LookupClientOptions {Timeout=TimeSpan.FromSeconds(3),Retries=1});
            var query=await dns.QueryAsync("_minecraft._tcp."+domain,QueryType.SRV,cancellationToken:token);
            var records=query.Answers.SrvRecords().ToArray();
            var srv=records.OrderBy(x=>x.Priority).ThenBy(_=>Random.Shared.Next()).FirstOrDefault();
            if(srv is not null){host=srv.Target.Value.TrimEnd('.');port=srv.Port;}
            else (host,port)=await Doh(domain,token);
        }
        catch(Exception e) when(e is DnsResponseException or TimeoutException){(host,port)=await Doh(domain,token);}
        if (host is "" or "." || port==0) throw new IOException("该线路暂不提供游戏服务。");
        return new(domain,host,port,-1);
    }
    private static async Task<(string,int)> Doh(string domain,CancellationToken token)
    {
        using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(4)};
        var json=await http.GetFromJsonAsync<JsonElement>("https://dns.google/resolve?name="+Uri.EscapeDataString("_minecraft._tcp."+domain)+"&type=SRV",token);
        if(json.TryGetProperty("Answer",out var answers)) foreach(var answer in answers.EnumerateArray().Where(x=>x.GetProperty("type").GetInt32()==33))
        {var fields=answer.GetProperty("data").GetString()!.Split(' ',StringSplitOptions.RemoveEmptyEntries);return (fields[3].TrimEnd('.'),int.Parse(fields[2]));}
        return (domain,25565);
    }
    public static async Task<RouteEndpoint> Probe(string domain,CancellationToken token=default)
    {
        var resolved=await Resolve(domain,token);
        return await Probe(resolved,token);
    }
    public static async Task<RouteEndpoint> Probe(RouteEndpoint resolved,CancellationToken token=default)
    {
        resolved=resolved with{Latency=-1};
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(resolved.Host,resolved.Port,timeout.Token);
            // Verify a Minecraft status response; an FRP listener alone does not imply a working game server.
            var stream=tcp.GetStream();using var handshake=new MemoryStream();
            WriteVarInt(handshake,0);WriteVarInt(handshake,47);var name=Encoding.UTF8.GetBytes(resolved.Domain);WriteVarInt(handshake,name.Length);handshake.Write(name);
            handshake.WriteByte((byte)(resolved.Port>>8));handshake.WriteByte((byte)resolved.Port);WriteVarInt(handshake,1);
            using var packet=new MemoryStream();WriteVarInt(packet,(int)handshake.Length);handshake.Position=0;handshake.CopyTo(packet);packet.Write([1,0]);
            await stream.WriteAsync(packet.ToArray(),timeout.Token);
            var length=await ReadVarInt(stream,timeout.Token);if(length is < 3 or > 1024*1024)throw new IOException("状态包无效。");
            // Forge may append extension data after the status JSON. Consume the
            // entire frame so that extension bytes cannot be mistaken for a pong.
            var statusPacket=new byte[length];await stream.ReadExactlyAsync(statusPacket,timeout.Token);
            using var statusFrame=new MemoryStream(statusPacket);
            if(await ReadVarInt(statusFrame,timeout.Token)!=0)throw new IOException("游戏状态不可用。");
            var jsonLength=await ReadVarInt(statusFrame,timeout.Token);if(jsonLength<=0||jsonLength>statusFrame.Length-statusFrame.Position)throw new IOException("状态包无效。");
            var data=new byte[jsonLength];await statusFrame.ReadExactlyAsync(data,timeout.Token);using var status=JsonDocument.Parse(data);
            if(!status.RootElement.TryGetProperty("version",out _))throw new IOException("游戏状态不可用。");
            // Match Minecraft's server-list RTT: DNS, TCP setup and the status
            // JSON/icon response are complete before timing the ping/pong pair.
            var ping=new byte[10];ping[0]=9;ping[1]=1;
            var payload=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(2),payload);
            var timer=Stopwatch.StartNew();await stream.WriteAsync(ping,timeout.Token);
            if(await ReadVarInt(stream,timeout.Token)!=9||await ReadVarInt(stream,timeout.Token)!=1)throw new IOException("测速响应无效。");
            var pong=new byte[8];await stream.ReadExactlyAsync(pong,timeout.Token);timer.Stop();
            if(BinaryPrimitives.ReadInt64BigEndian(pong)!=payload)throw new IOException("测速响应不匹配。");
            return resolved with {Latency=(int)timer.ElapsedMilliseconds};
        }
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or JsonException) {return resolved;}
    }
    public static async Task<RouteEndpoint[]> ProbeAll(string instance,CancellationToken token=default)
    {
        if(!Domains.TryGetValue(instance,out var domains))throw new InvalidDataException("未知服务器。");
        return await Task.WhenAll(domains.Select(async domain=>{try{return await Probe(domain,token);}catch{return new RouteEndpoint(domain,domain,25565,-1);}}));
    }
    public static async Task<RouteEndpoint> Select(string instance,string choice,CancellationToken token=default)
    {
        var results=await ProbeAll(instance,token);
        if(choice is "0" or "1") {var selected=results[int.Parse(choice)];if(selected.Latency<0)throw new IOException("所选线路暂不可达。可切换另一条线路或选择自动。");return selected;}
        return results.Where(x=>x.Latency>=0).OrderBy(x=>x.Latency).ThenBy(_=>Random.Shared.Next()).FirstOrDefault()??throw new IOException("两条线路当前均不可达，请稍后重试。");
    }
    private static void WriteVarInt(Stream stream,int value){uint n=(uint)value;while(n>127){stream.WriteByte((byte)((n&127)|128));n>>=7;}stream.WriteByte((byte)n);}
    private static async Task<int> ReadVarInt(Stream stream,CancellationToken token){var value=0;var buffer=new byte[1];for(var i=0;i<5;i++){await stream.ReadExactlyAsync(buffer,token);value|=(buffer[0]&127)<<(7*i);if((buffer[0]&128)==0)return value;}throw new IOException("状态包过长。");}
}
