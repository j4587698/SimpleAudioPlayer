namespace SimpleAudioPlayer.Utils;

public class ContextAwareScheduler(
    SynchronizationContext? syncContext = null,
    TaskScheduler? scheduler = null)
{
    private readonly SynchronizationContext? _syncContext = syncContext ?? SynchronizationContext.Current;
    private readonly TaskScheduler _taskScheduler = scheduler ?? TaskScheduler.Default;

    public void Post(Action action)
    {
        if (_syncContext != null)
        {
            _syncContext.Post(_ => action(), null);
        }
        else
        {
            Task.Factory.StartNew(action, 
                CancellationToken.None, 
                TaskCreationOptions.DenyChildAttach, 
                _taskScheduler);
        }
    }
}