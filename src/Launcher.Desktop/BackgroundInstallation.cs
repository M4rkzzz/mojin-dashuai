using System.Windows.Threading;
using Boshan.Launcher;

namespace Boshan.Desktop;

public static class BackgroundInstallation
{
    public static Task Run(Func<Task> work,CancellationToken token=default)=>Task.Run(work,token);
}

public sealed class DispatcherTransferProgress : IProgress<TransferProgress>,IDisposable
{
    private readonly object gate=new();
    private readonly Dispatcher dispatcher;
    private readonly Action<TransferProgress> publish;
    private readonly Action<TransferProgress>? phaseChanged;
    private readonly Timer timer;
    private TransferProgress? pending,current;
    private string? phase;
    private bool queued,disposed;
    public DispatcherTransferProgress(Dispatcher dispatcher,Action<TransferProgress> publish,Action<TransferProgress>? phaseChanged=null)
    {
        this.dispatcher=dispatcher;this.publish=publish;this.phaseChanged=phaseChanged;
        timer=new(_=>Schedule(),null,200,200);
    }
    public TransferProgress? Current {get{lock(gate)return current;}}
    public void Report(TransferProgress value)
    {
        bool changed;
        lock(gate)
        {
            if(disposed)return;
            current=pending=value;changed=phase!=value.Phase;phase=value.Phase;
        }
        if(changed){phaseChanged?.Invoke(value);Schedule();}
    }
    private void Schedule()
    {
        lock(gate){if(disposed||queued||pending is null||dispatcher.HasShutdownStarted)return;queued=true;}
        // At most one pending UI callback, even when thousands of tiny files finish together.
        try{dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(Drain));}
        catch(InvalidOperationException){lock(gate)queued=false;}
    }
    private void Drain()
    {
        TransferProgress? value;
        lock(gate){queued=false;if(disposed)return;value=pending;pending=null;}
        if(value is not null)publish(value);
    }
    public void Dispose(){lock(gate){disposed=true;pending=null;}timer.Dispose();}
}
