using System.Text;
using Tenninety.Execution.Candidates;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Phase 2 parser/policy tests: raw NUL-delimited `ls-tree -r -z` records are parsed
/// byte-accurately and every unsupported mode, malformed record, non-blob object, invalid
/// UTF-8 path, rooted/colon form, non-NFC form, reserved segment, structural defect and limit
/// violation fails closed.
/// </summary>
public class GitTreeMaterializerTests
{
    private const int MaxFiles = 1_000;
    private const int MaxPathBytes = 4_096;

    private static byte[] Listing(params (string Mode, string Type, string Sha, string Path)[] entries)
    {
        var text = string.Concat(entries.Select(e => $"{e.Mode} {e.Type} {e.Sha}\t{e.Path}\0"));
        return Encoding.UTF8.GetBytes(text);
    }

    private const string BlobSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Parses_regular_and_executable_blob_records()
    {
        var entries = GitTreeListingParser.Parse(Listing(
            ("100644", "blob", BlobSha, "file.txt"),
            ("100755", "blob", BlobSha, "scripts/run.sh"),
            ("100644", "blob", BlobSha, "name with spaces.txt")), MaxFiles, MaxPathBytes);

        Assert.Equal(3, entries.Count);
        Assert.Equal("100644", entries[0].Mode);
        Assert.Equal("blob", entries[0].ObjectType);
        Assert.Equal(BlobSha, entries[0].ObjectSha);
        Assert.Equal("file.txt", entries[0].Path);
        Assert.Equal("scripts/run.sh", entries[1].Path);
        Assert.Equal("name with spaces.txt", entries[2].Path);
    }

    [Fact]
    public void Parses_an_empty_listing_to_zero_entries()
    {
        // The zero-byte output is the only valid empty-tree representation.
        Assert.Empty(GitTreeListingParser.Parse([], MaxFiles, MaxPathBytes));
    }

    [Fact]
    public void Symlink_gitlink_tree_and_unknown_modes_fail_closed()
    {
        foreach (var (mode, type) in new[]
                 {
                     ("120000", "blob"),   // symlink
                     ("160000", "commit"), // gitlink/submodule
                     ("040000", "tree"),   // tree in an invalid position
                     ("100664", "blob"),   // unknown/unsupported mode
                     ("100777", "blob"),   // unknown/unsupported mode
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                GitTreeListingParser.Parse(Listing(
                    ("100644", "blob", BlobSha, "ok.txt"),
                    (mode, type, BlobSha, "hostile")), MaxFiles, MaxPathBytes);
            });
            Assert.Contains($"'{mode}'", ex.Message);
        }
    }

    [Fact]
    public void Malformed_records_fail_closed()
    {
        // No metadata/path separator.
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(Encoding.UTF8.GetBytes("garbage without tab\0"), MaxFiles, MaxPathBytes);
        });
        // Metadata with the wrong shape.
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(Encoding.UTF8.GetBytes("100644 blob\tx\0"), MaxFiles, MaxPathBytes);
        });
        // Object id too short.
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(Listing(("100644", "blob", "0123456789abcdef", "x")), MaxFiles, MaxPathBytes);
        });
        // Object id not hex.
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(Listing(("100644", "blob", new string('z', 40), "x")), MaxFiles, MaxPathBytes);
        });
    }

    [Fact]
    public void Structural_listing_defects_fail_closed()
    {
        // Missing terminating NUL.
        var missingNul = Encoding.UTF8.GetBytes($"100644 blob {BlobSha}\tfile.txt");
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(missingNul, MaxFiles, MaxPathBytes);
        });
        Assert.Contains("terminating NUL", ex.Message);

        // Consecutive NUL bytes (empty records), anywhere in the listing.
        var consecutive = Encoding.UTF8.GetBytes($"100644 blob {BlobSha}\tfile.txt\0\0");
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(consecutive, MaxFiles, MaxPathBytes);
        });
        var leadingEmpty = Encoding.UTF8.GetBytes($"\0100644 blob {BlobSha}\tfile.txt\0");
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(leadingEmpty, MaxFiles, MaxPathBytes);
        });

        // Trailing junk after the terminating NUL.
        var trailingJunk = Encoding.UTF8.GetBytes($"100644 blob {BlobSha}\tfile.txt\0junk");
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(trailingJunk, MaxFiles, MaxPathBytes);
        });
    }

    [Fact]
    public void Parser_enforces_max_files_while_processing_records()
    {
        var listing = Listing(
            ("100644", "blob", BlobSha, "a.txt"),
            ("100644", "blob", BlobSha, "b.txt"),
            ("100644", "blob", BlobSha, "c.txt"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(listing, maxFiles: 2, maxPathBytes: MaxPathBytes);
        });
        Assert.Contains("maximum of 2", ex.Message);
        // The exact boundary still succeeds.
        Assert.Equal(3, GitTreeListingParser.Parse(listing, maxFiles: 3, maxPathBytes: MaxPathBytes).Count);
    }

    [Fact]
    public void Parser_enforces_the_maximum_path_length()
    {
        var listing = Listing(("100644", "blob", BlobSha, new string('a', 300) + ".txt"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse(listing, MaxFiles, maxPathBytes: 256);
        });
        Assert.Contains("maximum of 256", ex.Message);
        // Boundary: exactly the configured byte length succeeds (252 + ".txt" = 256 bytes).
        var boundaryListing = Listing(("100644", "blob", BlobSha, new string('a', 252) + ".txt"));
        Assert.Single(GitTreeListingParser.Parse(boundaryListing, MaxFiles, maxPathBytes: 256));
    }

    [Fact]
    public void Invalid_utf8_paths_fail_closed()
    {
        var header = Encoding.ASCII.GetBytes($"100644 blob {BlobSha}\t");
        var bytes = new List<byte>(header) { 0xFF, 0xFE, (byte)'x', 0 };
        Assert.Throws<InvalidOperationException>(() =>
        {
            GitTreeListingParser.Parse([.. bytes], MaxFiles, MaxPathBytes);
        });
    }

    [Fact]
    public void Unsafe_paths_fail_closed()
    {
        foreach (var path in new[]
                 {
                     "",              // impossible post-split, defensive
                     "/etc/passwd",   // POSIX absolute
                     "C:/evil",       // Windows drive-absolute
                     "C:evil",        // drive-relative
                     "file:stream",   // alternate-data-stream/colon ambiguity
                     "//server/share",// UNC/device form
                     "\\\\server\\share", // UNC form
                     "../evil",       // parent traversal
                     "a/../../evil",  // traversal
                     "./file",        // non-canonical
                     "a//b",          // empty segment
                     "a/",            // trailing slash
                     "a\\b",          // backslash ambiguity
                     "we\tird",       // tab delimiter
                     "new\nline",     // newline delimiter
                     "ret\rurn",      // carriage return
                     ".git/config",   // reserved segment
                     ".GIT/config",   // reserved segment, case-insensitive
                     "nested/.git/config", // nested reserved segment
                     "nested/.GIT/x", // nested reserved segment, case-insensitive
                     "cafe\u0301.txt",// decomposed Unicode (not NFC)
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                GitTreeListingParser.Parse(Listing(("100644", "blob", BlobSha, path)), MaxFiles, MaxPathBytes);
            });
            Assert.Contains("safe repository-relative path", ex.Message);
        }
    }

    [Fact]
    public void Path_policy_accepts_exactly_the_safe_shapes()
    {
        Assert.True(RepositoryPathPolicy.IsSafeTreePath("a.txt"));
        Assert.True(RepositoryPathPolicy.IsSafeTreePath("src/file.cs"));
        Assert.True(RepositoryPathPolicy.IsSafeTreePath(".gitignore"));
        Assert.True(RepositoryPathPolicy.IsSafeTreePath(".tenninety/config.json"));
        Assert.True(RepositoryPathPolicy.IsSafeTreePath("a b.txt"));
        Assert.True(RepositoryPathPolicy.IsSafeTreePath("..gitignore")); // reserved check is segment-exact
        Assert.True(RepositoryPathPolicy.IsSafeTreePath(" ")); // legal POSIX name, unambiguous in NUL data
        Assert.False(RepositoryPathPolicy.IsSafeTreePath(".."));
    }

    [Fact]
    public void Path_policy_rejects_every_unsafe_shape()
    {
        foreach (var path in new[]
                 {
                     null, "", ".", "..", "/abs", "C:/evil", "C:evil", "file:stream",
                     "//server/share", "\\\\server\\share", "a/../b", "./a", "a//b", "a/",
                     "a\\b", "a\tb", "a\nb", ".git", ".git/x", ".GIT/x", "nested/.git/config",
                     "a\0b", "caf\u00e9.txt", "caf\u0301.txt",
                 })
        {
            Assert.False(RepositoryPathPolicy.IsSafeTreePath(path), $"'{path}' must be rejected");
        }
    }

    [Fact]
    public void Mode_policy_accepts_only_v1_modes()
    {
        Assert.True(RepositoryPathPolicy.IsSupportedMode("100644"));
        Assert.True(RepositoryPathPolicy.IsSupportedMode("100755"));
        foreach (var mode in new[] { null, "", "100643", "120000", "160000", "040000", "100664" })
            Assert.False(RepositoryPathPolicy.IsSupportedMode(mode));
    }

    [Fact]
    public void Blob_reads_are_size_capped()
    {
        using var repo = new TestGitRepo();
        var bytes = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();
        repo.WriteFile("blob.bin", bytes);
        var sha = repo.Commit("blob cap fixture");
        var listing = GitTreeListingParser.Parse(repo.Git.LsTreeRecursiveRaw(sha, 1 << 20), MaxFiles, MaxPathBytes);
        var oid = listing.Single().ObjectSha;

        Assert.Throws<GitOutputLimitExceededException>(() => repo.Git.ReadBlobRaw(oid, 10));
        Assert.True(bytes.SequenceEqual(repo.Git.ReadBlobRaw(oid, 512)));
        Assert.True(bytes.SequenceEqual(repo.Git.ReadBlobRaw(oid, 4096)));
    }

    [Fact]
    public void Streaming_blob_writes_respect_the_cap_and_clean_partials()
    {
        using var repo = new TestGitRepo();
        var bytes = new byte[512];
        new Random(7).NextBytes(bytes);
        repo.WriteFile("blob.bin", bytes);
        var sha = repo.Commit("streaming cap fixture");
        var oid = GitTreeListingParser.Parse(repo.Git.LsTreeRecursiveRaw(sha, 1 << 20), MaxFiles, MaxPathBytes)
            .Single().ObjectSha;

        var destination = Path.Combine(Path.GetTempPath(), $"tenninety-cap-{Guid.NewGuid():N}.bin");
        try
        {
            Assert.Throws<GitOutputLimitExceededException>(
                () => repo.Git.WriteBlobToFile(oid, destination, maxBytes: 10));
            Assert.False(File.Exists(destination)); // partial file removed

            var written = repo.Git.WriteBlobToFile(oid, destination, maxBytes: 512);
            Assert.Equal(512, written); // exact boundary succeeds
            Assert.True(bytes.SequenceEqual(File.ReadAllBytes(destination)));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public void Materializer_rejects_missing_symlinked_or_nonempty_destinations()
    {
        using var repo = new TestGitRepo();
        repo.WriteFile("a.txt", "content");
        var sha = repo.Commit("destination fixture");
        var materializer = new GitTreeMaterializer(repo.Git);

        var missing = Path.Combine(Path.GetTempPath(), $"tenninety-missing-{Guid.NewGuid():N}");
        Assert.Throws<InvalidOperationException>(() =>
        {
            materializer.Materialize(missing, sha);
        });

        var emptyDestination = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"tenninety-empty-{Guid.NewGuid():N}"));
        try
        {
            // A symlinked destination is rejected even though it resolves to a real empty dir.
            var linkDestination = Path.Combine(
                Path.GetTempPath(), $"tenninety-link-{Guid.NewGuid():N}");
            Directory.CreateSymbolicLink(linkDestination, emptyDestination.FullName);
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                {
                    materializer.Materialize(linkDestination, sha);
                });
            }
            finally
            {
                File.Delete(linkDestination);
            }

            // SHA validation still happens on a valid empty destination.
            Assert.Throws<InvalidOperationException>(() =>
            {
                materializer.Materialize(emptyDestination.FullName, "not-a-sha");
            });
            Assert.Throws<Tenninety.Git.GitException>(() =>
            {
                materializer.Materialize(emptyDestination.FullName, new string('e', 40));
            });
        }
        finally
        {
            emptyDestination.Delete(recursive: true);
        }

        var fileDestination = Path.Combine(Path.GetTempPath(), $"tenninety-file-{Guid.NewGuid():N}");
        File.WriteAllText(fileDestination, "not a directory");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                materializer.Materialize(fileDestination, sha);
            });
        }
        finally
        {
            File.Delete(fileDestination);
        }

        var nonEmpty = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"tenninety-nonempty-{Guid.NewGuid():N}"));
        try
        {
            File.WriteAllText(Path.Combine(nonEmpty.FullName, "already-here.txt"), "x");
            Assert.Throws<InvalidOperationException>(() =>
            {
                materializer.Materialize(nonEmpty.FullName, sha);
            });
        }
        finally
        {
            nonEmpty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Materializer_rejects_non_commit_and_malformed_shas()
    {
        using var repo = new TestGitRepo();
        repo.WriteFile("a.txt", "content");
        repo.Commit("sha fixture");
        var destination = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"tenninety-sha-{Guid.NewGuid():N}"));
        try
        {
            var materializer = new GitTreeMaterializer(repo.Git);
            Assert.Throws<InvalidOperationException>(() =>
            {
                materializer.Materialize(destination.FullName, "not-a-sha");
            });
            Assert.Throws<Tenninety.Git.GitException>(() =>
            {
                materializer.Materialize(destination.FullName, new string('e', 40));
            });
            // The empty destination stays empty: nothing was materialized.
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination.FullName));
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    [Fact]
    public void Materialization_limits_are_bounded_and_positive()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new GitTreeMaterializer(new GitService(Path.GetTempPath()),
                new MaterializationLimits { MaxFiles = 0 }));
        Assert.Throws<InvalidOperationException>(() =>
            new GitTreeMaterializer(new GitService(Path.GetTempPath()),
                new MaterializationLimits { MaxFiles = 1_000_001 }));
        Assert.Throws<InvalidOperationException>(() =>
            new GitTreeMaterializer(new GitService(Path.GetTempPath()),
                new MaterializationLimits { MaxTotalBytes = 0 }));
        Assert.Throws<InvalidOperationException>(() =>
            new GitTreeMaterializer(new GitService(Path.GetTempPath()),
                new MaterializationLimits { MaxPathBytes = 0 }));
        Assert.Throws<InvalidOperationException>(() =>
            new GitTreeMaterializer(new GitService(Path.GetTempPath()),
                new MaterializationLimits { MaxPathBytes = 65_537 }));
        Assert.Throws<InvalidOperationException>(() =>
            new GitTreeMaterializer(new GitService(Path.GetTempPath()),
                new MaterializationLimits { MaxTreeListingBytes = 0 }));
    }
}
