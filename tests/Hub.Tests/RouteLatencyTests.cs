using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Boshan.Launcher;
using Xunit;

namespace Boshan.Tests;
public sealed class RouteLatencyTests
{
    private static async Task<int> Number(Stream stream){var value=0;var one=new byte[1];for(var i=0;i<5;i++){await stream.ReadExactlyAsync(one);value|=(one[0]&127)<<(7*i);if(one[0]<128)return value;}throw new IOException();}
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyPongRoundTripIsTimedAndPayloadMustMatch(bool corrupt)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var endpoint=(IPEndPoint)listener.LocalEndpoint;
        var server=Task.Run(async()=>{
            using var client=await listener.AcceptTcpClientAsync();client.NoDelay=true;using var stream=client.GetStream();
            var hello=new byte[await Number(stream)];await stream.ReadExactlyAsync(hello);
            Assert.Equal(1,await Number(stream));Assert.Equal(0,await Number(stream));
            await Task.Delay(300);
            var json=Encoding.UTF8.GetBytes("{\"version\":{\"name\":\"fixture\"}}");
            byte[] forgeExtension=[3,123,125,0];
            await stream.WriteAsync(new byte[]{(byte)(json.Length+2+forgeExtension.Length),0,(byte)json.Length});await stream.WriteAsync(json);await stream.WriteAsync(forgeExtension);
            var ping=new byte[10];await stream.ReadExactlyAsync(ping);Assert.Equal(9,ping[0]);Assert.Equal(1,ping[1]);
            await Task.Delay(25);if(corrupt)ping[^1]^=1;await stream.WriteAsync(ping);
        });
        var timer=Stopwatch.StartNew();var result=await Routes.Probe(new RouteEndpoint("localhost","127.0.0.1",endpoint.Port,-1));timer.Stop();await server;
        if(corrupt)Assert.Equal(-1,result.Latency);
        else{Assert.True(result.Latency>=15);Assert.True(timer.ElapsedMilliseconds-result.Latency>=250,"Status retrieval was included in the displayed ping");}
    }
}
