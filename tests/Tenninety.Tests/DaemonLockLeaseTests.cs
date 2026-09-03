using System.Collections.Concurrent;
using Tenninety.Execution;

namespace Tenninety.Tests;

/// <summary>
/// Daemon-lock lease contract: the lease is typed, knows its repository identity, tracks
/// disposal, and trusted operations can fail closed on a disposed or foreign lease.
/// </summary>
public class DaemonLockLeaseTests
{
    [Fact]
    public void Acquire_returns_a_live_lease_bound_to_the_workspace()
    {
        using var repo = new TestGitRepo();
        using var lease = DaemonLock.Acquire(repo.Root);
        Assert.False(lease.IsDisposed);
        Assert.Equal(System.IO.Path.GetFullPath(repo.Root), lease.WorkspaceRoot);
        Assert.Equal(
            System.IO.Path.GetFullPath(DaemonLock.ResolveCommonGitDirectory(repo.Root)),
            lease.CanonicalGitIdentity);
        lease.ThrowIfNotLiveFor(repo.Root); // live + same repository: no exception
    }

    [Fact]
    public void Dispose_marks_the_lease_dead()
    {
        using var repo = new TestGitRepo();
        var lease = DaemonLock.Acquire(repo.Root);
        lease.Dispose();
        Assert.True(lease.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => lease.ThrowIfNotLiveFor(repo.Root));
        lease.Dispose(); // idempotent
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public void Lease_rejects_a_different_repository()
    {
        using var repo = new TestGitRepo();
        using var other = new TestGitRepo();
        using var lease = DaemonLock.Acquire(repo.Root);
        Assert.Throws<InvalidOperationException>(() => lease.ThrowIfNotLiveFor(other.Root));
    }

    [Fact]
    public void The_lease_excludes_concurrent_acquisition_of_the_same_workspace()
    {
        using var repo = new TestGitRepo();
        using var lease = DaemonLock.Acquire(repo.Root);
        Assert.Throws<InvalidOperationException>(() => DaemonLock.Acquire(repo.Root));
    }

    [Fact]
    public void Concurrent_disposal_of_one_guard_decrements_the_active_count_exactly_once()
    {
        using var repo = new TestGitRepo();
        var lease = DaemonLock.Acquire(repo.Root);

        // Two live guards, then outer-lease disposal: the OS handle stays held until the
        // LAST real guard exits.
        var guard1 = lease.BeginUseFor(repo.Root);
        var guard2 = lease.BeginUseFor(repo.Root);
        Assert.False(lease.IsDisposed);
        lease.Dispose();
        Assert.True(lease.IsDisposed);

        // Many threads dispose the SAME first guard concurrently, each more than once.
        // Exactly-once disposal must decrement the active count by exactly ONE in total:
        // a double decrement would drop it to zero and release the OS handle here.
        const int participants = 8;
        var startBarrier = new Barrier(participants);
        var failures = new ConcurrentQueue<string>();
        var threads = new List<Thread>(participants);
        for (var i = 0; i < participants; i++)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Assert.True(startBarrier.SignalAndWait(TimeSpan.FromSeconds(30)));
                    guard1.Dispose();
                    guard1.Dispose(); // repeated disposal on the same guard: no-op
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex.ToString());
                }
            });
            thread.Start();
            threads.Add(thread);
        }
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a disposer thread hung");
        Assert.True(failures.IsEmpty,
            "concurrent disposal threads failed: " + string.Join(Environment.NewLine, failures));

        // guard2 is STILL active: reacquisition of the same repository must fail.
        Assert.Throws<InvalidOperationException>(() => DaemonLock.Acquire(repo.Root));

        guard2.Dispose();

        // The true final guard has exited: the deferred disposal completed and the same
        // repository can be acquired again.
        using var reacquired = DaemonLock.Acquire(repo.Root);
        Assert.False(reacquired.IsDisposed);
    }
}
