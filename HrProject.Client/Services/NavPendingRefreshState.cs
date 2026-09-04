namespace HrProject.Client.Services;

public sealed class NavPendingRefreshState
{
    public event Action? RefreshRequested;

    public void RequestRefresh() => RefreshRequested?.Invoke();
}
