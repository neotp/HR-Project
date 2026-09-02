namespace HrProject.Client.Services;

public sealed class LoadingState
{
    private const int MinimumDisplayMilliseconds = 250;
    private int activeRequests;
    private int requestVersion;
    private DateTime visibleSinceUtc;

    public bool IsVisible { get; private set; }
    public event Action? Changed;

    public IDisposable BeginRequest()
    {
        activeRequests++;
        if (activeRequests == 1)
        {
            requestVersion++;
            if (!IsVisible)
            {
                IsVisible = true;
                visibleSinceUtc = DateTime.UtcNow;
                Changed?.Invoke();
            }
        }
        return new RequestScope(this);
    }

    private void EndRequest()
    {
        if (activeRequests > 0)
            activeRequests--;
        if (activeRequests != 0)
            return;

        if (!IsVisible)
            return;

        var version = requestVersion;
        var elapsed = DateTime.UtcNow - visibleSinceUtc;
        var remaining = MinimumDisplayMilliseconds - (int)elapsed.TotalMilliseconds;
        if (remaining > 0)
            _ = HideAfterDelay(remaining, version);
        else
            Hide(version);
    }

    private async Task HideAfterDelay(int milliseconds, int version)
    {
        await Task.Delay(milliseconds);
        Hide(version);
    }

    private void Hide(int version)
    {
        if (activeRequests != 0 || version != requestVersion || !IsVisible)
            return;
        IsVisible = false;
        Changed?.Invoke();
    }

    private sealed class RequestScope(LoadingState owner) : IDisposable
    {
        private LoadingState? state = owner;

        public void Dispose() => Interlocked.Exchange(ref state, null)?.EndRequest();
    }
}
