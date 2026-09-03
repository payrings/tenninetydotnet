namespace Tenninety.Execution.Sandbox;

/// <summary>Independent cleanup budget that is never inherited from cancelled attempt work.
/// It also bounds injected or defective session implementations that ignore cancellation.</summary>
internal static class SandboxCleanupDeadline
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    internal static async Task StopAsync(
        ISandboxSession session, TimeSpan? timeout = null)
    {
        using var cleanup = new CancellationTokenSource(timeout ?? DefaultTimeout);
        await session.StopAsync(cleanup.Token).WaitAsync(cleanup.Token);
    }

    internal static async Task DisposeAsync(
        ISandboxSession session, TimeSpan? timeout = null)
    {
        using var cleanup = new CancellationTokenSource(timeout ?? DefaultTimeout);
        await session.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
    }
}
