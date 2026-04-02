namespace Honua.StacOpsDemo.Services;

internal sealed class DashboardLoadCoordinator : IDisposable
{
    private CancellationTokenSource? _activeLoad;

    public LoadHandle BeginLoad()
    {
        var nextLoad = new CancellationTokenSource();
        var previousLoad = _activeLoad;
        _activeLoad = nextLoad;
        previousLoad?.Cancel();

        return new LoadHandle(this, nextLoad);
    }

    public void CancelActiveLoad()
    {
        var activeLoad = _activeLoad;
        _activeLoad = null;
        activeLoad?.Cancel();
    }

    public void Dispose()
    {
        CancelActiveLoad();
    }

    private bool IsActive(CancellationTokenSource load)
    {
        return ReferenceEquals(_activeLoad, load);
    }

    internal readonly struct LoadHandle(DashboardLoadCoordinator owner, CancellationTokenSource load)
        : IDisposable
    {
        public CancellationToken Token => load.Token;

        public bool IsActive => owner.IsActive(load);

        public void Dispose()
        {
            if (owner.IsActive(load))
            {
                owner._activeLoad = null;
            }

            load.Dispose();
        }
    }
}
