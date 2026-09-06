using System.Net;
using Boshan.Launcher;
using Xunit;

namespace Hub.Tests;

public sealed class NetworkOptimizationTests
{
    [Theory]
    [InlineData("0", "first")]
    [InlineData("1", "second")]
    public async Task FixedRouteNeverProbesTheOtherRoute(string choice,string expected)
    {
        var calls=new List<string>();
        var route=await Routes.SelectRoutes(["first","second"],choice,(domain,_)=>
        {calls.Add(domain);return Task.FromResult(new RouteEndpoint(domain,domain,25565,20));},default);
        Assert.Equal(expected,route.Domain);Assert.Equal([expected],calls);
    }
    [Fact] public async Task AutomaticRouteStillChecksBothEndpointsAndRejectsOfflineRoutes()
    {
        var calls=new List<string>();
        var route=await Routes.SelectRoutes(["first","second"],"auto",(domain,_)=>
        {calls.Add(domain);return Task.FromResult(new RouteEndpoint(domain,domain,25565,domain=="first"?-1:90));},default);
        Assert.Equal("second",route.Domain);Assert.Equal(2,calls.Count);
    }
    [Theory]
    [InlineData(HttpStatusCode.BadGateway,2)]
    [InlineData(HttpStatusCode.ServiceUnavailable,2)]
    [InlineData(HttpStatusCode.GatewayTimeout,2)]
    [InlineData(HttpStatusCode.Unauthorized,1)]
    [InlineData(HttpStatusCode.NotFound,1)]
    [InlineData(HttpStatusCode.TooManyRequests,1)]
    public async Task OnlyTransientMetadataGetsRetry(HttpStatusCode status,int expected)
    {
        int count=0;
        using var client=new HttpClient(new Handler(request=>
        {
            Assert.Equal(HttpMethod.Get,request.Method);
            Assert.Equal("https://example.test/manifest",request.RequestUri!.AbsoluteUri);
            count++;return new(status){Content=new StringContent("error")};
        }));
        var failure=await Assert.ThrowsAsync<NetworkFailure>(()=>NetworkPolicy.ReadMetadata(client,new("https://example.test/manifest"),"获取清单",default));
        Assert.Equal(expected,count);Assert.Equal(expected,failure.Diagnostic.Attempt);
    }
    [Fact] public async Task TransientFailureCanRecoverWithoutChangingOrigin()
    {
        int count=0;
        using var client=new HttpClient(new Handler(_=>++count==1?throw new HttpRequestException(HttpRequestError.ConnectionError,"lost"):new(HttpStatusCode.OK){Content=new StringContent("ok")}));
        Assert.Equal("ok",System.Text.Encoding.UTF8.GetString(await NetworkPolicy.ReadMetadata(client,new("https://example.test/manifest"),"获取清单",default)));
        Assert.Equal(2,count);
    }
    [Fact] public async Task CancellationDoesNotRetry()
    {
        using var cancel=new CancellationTokenSource();int count=0;
        using var client=new HttpClient(new Handler(_=>{count++;cancel.Cancel();return new(HttpStatusCode.ServiceUnavailable);}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>NetworkPolicy.ReadMetadata(client,new("https://example.test/manifest"),"获取清单",cancel.Token));
        Assert.Equal(1,count);
    }
    [Fact] public async Task SharedProbeAllowsOneCallerToCancelAndDoesNotCacheCompletedResults()
    {
        var work=new InFlightRequests<int>();var ready=new TaskCompletionSource();var release=new TaskCompletionSource<int>();int calls=0;
        async Task<int> Probe(CancellationToken token){Interlocked.Increment(ref calls);ready.TrySetResult();return await release.Task.WaitAsync(token);}
        using var cancel=new CancellationTokenSource();
        var first=work.Run("route",Probe,cancel.Token);await ready.Task;
        var second=work.Run("route",Probe,default);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        release.SetResult(42);Assert.Equal(42,await second);Assert.Equal(1,calls);
        Assert.Equal(42,await work.Run("route",Probe,default));Assert.Equal(2,calls);
    }
    [Fact] public async Task LastCancelledWaiterStopsUnderlyingProbe()
    {
        var work=new InFlightRequests<int>();var ready=new TaskCompletionSource();var stopped=new TaskCompletionSource();
        using var cancel=new CancellationTokenSource();
        var result=work.Run("route",async token=>
        {
            ready.SetResult();try{await Task.Delay(Timeout.Infinite,token);return 0;}
            finally{stopped.SetResult();}
        },cancel.Token);
        await ready.Task;cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>result);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact] public void ProxyChangeKeepsActiveRequestsAliveAndReusesUnchangedPool()
    {
        var handlers=new List<Handler>();
        var pool=new HttpClientPool(_=>{var handler=new Handler(_=>new(HttpStatusCode.OK));handlers.Add(handler);return new(handler);});
        using var first=pool.Acquire(new());using(var second=pool.Acquire(new()))Assert.Same(first.Client,second.Client);
        pool.Update(new(){ProxyMode="manual",Proxy="http://127.0.0.1:7890"});Assert.False(handlers[0].Disposed);
        first.Dispose();Assert.True(handlers[0].Disposed);Assert.False(handlers[1].Disposed);Assert.Equal(2,handlers.Count);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {
        public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
}
