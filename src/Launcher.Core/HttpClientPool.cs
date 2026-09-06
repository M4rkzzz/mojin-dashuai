namespace Boshan.Launcher;

/// <summary>Retire old proxy connections without interrupting active requests.</summary>
internal sealed class HttpClientPool(Func<LauncherSettings,HttpClient> create)
{
    internal sealed class Lease(HttpClientPool owner, Entry entry) : IDisposable
    {
        private bool disposed;
        public HttpClient Client => entry.Client;
        public void Dispose()
        {
            lock(owner.gate)
            {
                if(disposed)return;disposed=true;
                if(--entry.Users==0&&entry.Retired)entry.Client.Dispose();
            }
        }
    }
    internal sealed class Entry(HttpClient client)
    {public readonly HttpClient Client=client;public int Users;public bool Retired;}
    private readonly object gate=new();
    private Entry? current;
    private string? key;
    public Lease Acquire(LauncherSettings settings)
    {
        lock(gate)
        {
            Update(settings);
            current!.Users++;
            return new(this,current);
        }
    }
    public void Update(LauncherSettings settings)
    {
        lock(gate)
        {
            var next=settings.ProxyMode+"\n"+settings.Proxy;
            if(key==next)return;
            var replacement=new Entry(create(settings));
            if(current is not null)
            {current.Retired=true;if(current.Users==0)current.Client.Dispose();}
            current=replacement;key=next;
        }
    }
}
