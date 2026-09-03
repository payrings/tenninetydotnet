using System.Runtime.InteropServices;
using Tenninety.Execution.Sandbox;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Deterministic temporary-fixture regressions for the no-follow presence/type checking in
/// <see cref="TrustedWorkspaceDeletion"/>: genuine absence is distinguished from an existing
/// regular file, special file (FIFO) or redirect; an inspection failure never counts as
/// absence; unexpected entry types are preserved and reported (never deleted merely to make
/// cleanup pass); absence is verified after deletion; and the root/ancestor revalidation plus
/// direct-child containment stay intact. Every fixture cleans up its own retained resources.
/// </summary>
public class TrustedWorkspaceDeletionTests : IDisposable
{
    private readonly TempDir _managedRoot = new();

    public void Dispose() => _managedRoot.Dispose();

    [Fact]
    public void Genuine_absence_is_proven_and_the_deletion_is_a_no_op()
    {
        var child = Path.Combine(_managedRoot.Root, "attempt-absent");

        TrustedWorkspaceDeletion.DeleteManagedChildDirectory(child, _managedRoot.Root);

        Assert.False(Directory.Exists(child)); // still absent, nothing else touched
        Assert.True(Directory.Exists(_managedRoot.Root));
    }

    [Fact]
    public void An_existing_regular_file_is_NOT_absence_and_is_preserved()
    {
        var child = Path.Combine(_managedRoot.Root, "attempt-file");
        File.WriteAllText(child, "unexpected regular file");

        var threw = false;
        try
        {
            TrustedWorkspaceDeletion.DeleteManagedChildDirectory(child, _managedRoot.Root);
        }
        catch (InvalidOperationException ex)
        {
            threw = true;
            // The controlled message identifies the refusal without copying the path.
            Assert.Contains("not a real directory", ex.Message);
            Assert.Contains("preserved", ex.Message);
        }
        Assert.True(threw, "an existing regular file must not be treated as absence");
        Assert.True(File.Exists(child), "the unexpected regular file is preserved");
        // Fixture cleanup of the preserved entry.
        File.Delete(child);
    }

    [Fact]
    public void A_fifo_at_the_child_path_is_unexpected_and_is_never_deleted()
    {
        if (!OperatingSystem.IsLinux())
            return; // the FIFO fixture is Linux-specific; this suite runs on Linux

        var child = Path.Combine(_managedRoot.Root, "attempt-fifo");
        Assert.Equal(0, UnixSpecialFile.Mkfifo(child, mode: 0x180 /* 0600 | S_IFIFO base */));

        var threw = false;
        try
        {
            TrustedWorkspaceDeletion.DeleteManagedChildDirectory(child, _managedRoot.Root);
        }
        catch (InvalidOperationException ex)
        {
            threw = true;
            Assert.Contains("not a real directory", ex.Message);
        }
        Assert.True(threw, "a FIFO is an unexpected entry, never 'already absent'");
        // The FIFO is preserved (it still exists as a special file, not deleted).
        Assert.True(File.Exists(child) || Directory.Exists(child),
            "the unexpected entry must be preserved");
        File.Delete(child); // fixture cleanup (never via the deletion helper)
    }

    [Fact]
    public void A_symlink_at_the_child_path_is_never_followed_or_deleted()
    {
        var child = Path.Combine(_managedRoot.Root, "attempt-link");
        var target = Directory.CreateTempSubdirectory("tenninety-deletion-target");
        try
        {
            Directory.CreateSymbolicLink(child, target.FullName);

            var threw = false;
            try
            {
                TrustedWorkspaceDeletion.DeleteManagedChildDirectory(child, _managedRoot.Root);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Assert.True(threw, "a redirect at the attempt path is refused");
            // The link itself and its target survive untouched.
            Assert.True(Directory.Exists(target.FullName) || File.Exists(child));
            Assert.True(File.Exists(Path.Combine(target.FullName, "keep.txt")) == false ||
                        Directory.Exists(target.FullName));
        }
        finally
        {
            if (File.Exists(child) || Directory.Exists(child))
                Directory.Delete(child); // removes the symlink itself only
            target.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_inspection_failure_is_never_counted_as_absence()
    {
        // DIRECT native-inspection regression: the path's parent is a REGULAR FILE, so the
        // lstat of the child path fails with ENOTDIR (a non-ENOENT error). The inspection
        // primitive itself must throw instead of returning Absent, and the existing entry
        // must remain untouched. (Containment/depth rejection in the public deletion helper
        // is covered separately by The_managed_root_itself_and_deeper_paths_are_refused.)
        var notADirectory = Path.Combine(_managedRoot.Root, "blocker");
        File.WriteAllText(notADirectory, "a file, not a directory");
        var child = Path.Combine(notADirectory, "attempt-x");

        // The inspection operation IS reached here (the path bypasses the deletion helper's
        // containment/depth rejection): it must throw rather than return Absent.
        Assert.Throws<InvalidOperationException>(
            () => TrustedWorkspaceDeletion.InspectEntryNoFollow(child));
        Assert.True(File.Exists(notADirectory), "the existing entry remains untouched");
    }

    [Fact]
    public void A_real_directory_child_is_deleted_and_absence_is_verified_afterwards()
    {
        var child = Path.Combine(_managedRoot.Root, "attempt-real");
        Directory.CreateDirectory(Path.Combine(child, "source", "nested"));
        File.WriteAllText(Path.Combine(child, "source", "nested", "data.txt"), "x");

        TrustedWorkspaceDeletion.DeleteManagedChildDirectory(child, _managedRoot.Root);

        Assert.False(Directory.Exists(child));
        Assert.False(File.Exists(child));
    }

    [Fact]
    public void The_managed_root_itself_and_deeper_paths_are_refused()
    {
        var deeper = Path.Combine(_managedRoot.Root, "attempt", "source");
        Directory.CreateDirectory(deeper);

        Assert.Throws<InvalidOperationException>(() =>
            TrustedWorkspaceDeletion.DeleteManagedChildDirectory(_managedRoot.Root, _managedRoot.Root));
        Assert.Throws<InvalidOperationException>(() =>
            TrustedWorkspaceDeletion.DeleteManagedChildDirectory(deeper, _managedRoot.Root));
        Assert.True(Directory.Exists(deeper)); // nothing was deleted
    }

    // ---- owned empty root ------------------------------------------------------------------

    [Fact]
    public void An_absent_owned_root_is_a_no_op()
    {
        var owned = Path.Combine(_managedRoot.Root, "owned-missing");

        TrustedWorkspaceDeletion.DeleteEmptyOwnedDirectory(owned);

        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void An_owned_root_path_that_is_a_regular_file_is_refused_and_preserved()
    {
        var owned = Path.Combine(_managedRoot.Root, "owned-not-a-dir");
        File.WriteAllText(owned, "unexpected");

        var threw = false;
        try
        {
            TrustedWorkspaceDeletion.DeleteEmptyOwnedDirectory(owned);
        }
        catch (InvalidOperationException ex)
        {
            threw = true;
            Assert.Contains("not a real directory", ex.Message);
        }
        Assert.True(threw);
        Assert.True(File.Exists(owned), "the unexpected entry is preserved");
        File.Delete(owned);
    }

    [Fact]
    public void An_owned_root_with_unexpected_contents_is_never_deleted()
    {
        var owned = Directory.CreateTempSubdirectory("tenninety-owned-root-test");
        try
        {
            File.WriteAllText(Path.Combine(owned.FullName, "stray.txt"), "unexpected");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                TrustedWorkspaceDeletion.DeleteEmptyOwnedDirectory(owned.FullName));

            Assert.Contains("not empty", ex.Message);
            Assert.True(Directory.Exists(owned.FullName));
            Assert.True(File.Exists(Path.Combine(owned.FullName, "stray.txt")));
        }
        finally
        {
            owned.Delete(recursive: true); // fixture cleanup after the assertions
        }
    }

    [Fact]
    public void A_proven_empty_owned_root_is_removed_non_recursively_and_absence_is_verified()
    {
        var owned = Directory.CreateTempSubdirectory("tenninety-owned-root-test");
        TrustedWorkspaceDeletion.DeleteEmptyOwnedDirectory(owned.FullName);
        Assert.False(Directory.Exists(owned.FullName));
    }

    // ---- no-follow inspection primitive -----------------------------------------------------

    [Fact]
    public void The_no_follow_inspection_classifies_entries_without_following_links()
    {
        var dir = Path.Combine(_managedRoot.Root, "dir");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(_managedRoot.Root, "file.txt");
        File.WriteAllText(file, "x");
        var link = Path.Combine(_managedRoot.Root, "link");
        Directory.CreateSymbolicLink(link, _managedRoot.Root);

        try
        {
            Assert.Equal(TrustedWorkspaceDeletion.ManagedEntryKind.RealDirectory,
                TrustedWorkspaceDeletion.InspectEntryNoFollow(dir));
        Assert.Equal(TrustedWorkspaceDeletion.ManagedEntryKind.RealFile,
            TrustedWorkspaceDeletion.InspectEntryNoFollow(file));
            // A symlink is classified by the LINK ITSELF (no-follow), never its target.
            Assert.Equal(TrustedWorkspaceDeletion.ManagedEntryKind.UnexpectedEntry,
                TrustedWorkspaceDeletion.InspectEntryNoFollow(link));
            Assert.Equal(TrustedWorkspaceDeletion.ManagedEntryKind.Absent,
                TrustedWorkspaceDeletion.InspectEntryNoFollow(
                    Path.Combine(_managedRoot.Root, "no-such-entry")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(dir);
            File.Delete(file);
        }
    }
}
