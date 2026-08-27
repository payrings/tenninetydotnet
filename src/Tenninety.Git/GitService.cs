using Tenninety.Core;

namespace Tenninety.Git;

public sealed class GitException(string command, int exitCode, string stderr)
    : Exception($"git '{command}' failed (exit {exitCode}): {stderr.Trim()}");

public record GitCommit(string Sha, string Subject, string Author, string Date);

/// <summary>Git-first state operations (Part I.2 principle 4, Part VI.3 safety rules).</summary>
public interface IGitService
{
    string RepoPath { get; }
    bool IsRepository();
    void Init();
    bool IsClean();
    bool IsPathClean(string relativePath);
    string CurrentBranch();
    bool BranchExists(string branch);
    void CreateAndCheckoutBranch(string branch);
    void CheckoutBranch(string branch);
    /// <summary>Integrates the current main tip into an existing work branch before resuming.</summary>
    void MergeMainIntoCurrentBranch();
    /// <summary>Stages all changes and commits. Returns null when the tree was clean.</summary>
    string? CommitAll(string message);
    /// <summary>Stages only the given paths and commits them; null when nothing changed.</summary>
    string? CommitPaths(IEnumerable<string> relativePaths, string message);
    /// <summary>Squash-merges a work branch into main as ONE identifiable commit; returns the new sha.</summary>
    string SquashMergeToMain(string branch, string message);
    /// <summary>Bounded unified patch of branch vs main (head+tail elided).</summary>
    string DiffPatchAgainstMain(string branch, int maxChars = 20000);
    string HeadSha();
    string DiffHeadStat();
    string DiffAgainstMain(string branch);
    IReadOnlyList<GitCommit> RecentCommits(int count);
    GitCommit? FindCommit(string shaOrRef);
    /// <summary>Mechanical revert of a commit as a new commit (no history rewrite).</summary>
    void RevertCommitNoEdit(string sha);
    /// <summary>True when the commit is reachable from the current main branch.</summary>
    bool IsAncestorOfMain(string sha);
    /// <summary>Deletes a branch. Force skips the merged-check (used right after a squash
    /// merge, where content is provably on main even though -d would refuse).</summary>
    void DeleteBranchSafe(string branch, bool force = false);
}

public sealed class GitService : IGitService
{
    private static readonly string[] EnvironmentAllowlist =
    [
        "PATH", "HOME", "LANG", "LC_ALL", "USER", "LOGNAME", "TMPDIR",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "XDG_CONFIG_HOME",
    ];

    public string RepoPath { get; }

    public GitService(string repoPath) => RepoPath = Path.GetFullPath(repoPath);

    public bool IsRepository()
    {
        var dir = Path.Combine(RepoPath, ".git");
        return Directory.Exists(dir) || File.Exists(dir);
    }

    public void Init()
    {
        Run("init", "-b", TenNinety.MainBranch);
        // Ensure commits are possible even on machines without global git identity.
        if (!HasIdentity())
        {
            Run("config", "user.name", "tenninety");
            Run("config", "user.email", "tenninety@localhost");
        }
    }

    private bool HasIdentity() =>
        TryRun("config", "user.name").ExitCode == 0 && TryRun("config", "user.email").ExitCode == 0;

    public bool IsClean() => Run("status", "--porcelain").Output.Trim().Length == 0;

    public bool IsPathClean(string relativePath) =>
        Run("status", "--porcelain", "--", relativePath).Output.Trim().Length == 0;

    public string CurrentBranch() => Run("rev-parse", "--abbrev-ref", "HEAD").Output.Trim();

    public bool BranchExists(string branch) =>
        TryRun("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;

    public void CreateAndCheckoutBranch(string branch)
    {
        if (BranchExists(branch))
            throw new InvalidOperationException($"branch '{branch}' already exists — refusing to reuse stale state.");
        Run("checkout", "-b", branch);
    }

    public void CheckoutBranch(string branch) => Run("checkout", branch);

    public void MergeMainIntoCurrentBranch()
    {
        (int ExitCode, string Output, string Stderr) result;
        try
        {
            result = TryRun("merge", "--no-edit", TenNinety.MainBranch);
        }
        catch
        {
            TryAbort("merge");
            throw;
        }
        if (result.ExitCode == 0) return;
        TryAbort("merge");
        throw new GitException($"merge --no-edit {TenNinety.MainBranch}", result.ExitCode, result.Stderr);
    }

    public string? CommitAll(string message)
    {
        Run("add", "-A");
        ExcludeSecretShapedStagedFiles();
        return CommitStaged(message);
    }

    /// <summary>Secret-shaped NEW files are unstaged and repo-locally excluded instead of being
    /// committed (Part VI). Already-tracked modifications stay staged – hiding those would be
    /// misleading, since their history exists regardless.</summary>
    private void ExcludeSecretShapedStagedFiles()
    {
        // Check additions relative to HEAD. Asking ls-files after `git add` cannot distinguish
        // a newly-added secret from a previously tracked file because both are in the index.
        var additions = TryRun("diff", "--cached", "--diff-filter=A", "--name-only", "-z").Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in additions)
        {
            if (!Core.Security.Sanitizer.IsExcludedFile(path)) continue;
            Run("rm", "--cached", "--ignore-unmatch", "--", path);
            EnsureSecretPatternsExcluded(path);
        }
    }

    private void EnsureSecretPatternsExcluded(string path)
    {
        var infoExclude = ResolveGitPath("info/exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(infoExclude)!);
        var existing = File.Exists(infoExclude)
            ? File.ReadAllLines(infoExclude).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var required = Core.Security.Sanitizer.ExcludedFilePatterns
            .Append(GitIgnoreLiteral(path));
        var missing = required
            .Where(pattern => !existing.Contains(pattern))
            .ToArray();
        if (missing.Length > 0) File.AppendAllLines(infoExclude, missing);
    }

    private static string GitIgnoreLiteral(string path)
    {
        if (path.IndexOfAny(['\r', '\n']) >= 0) return "*secret*";
        var escaped = new System.Text.StringBuilder("/");
        foreach (var c in path.Replace('\\', '/'))
        {
            if (c is '*' or '?' or '[' or ']' or ' ') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>Stages ONLY the given paths (repo-relative) and commits them, so framework
    /// commands never capture unrelated user work. Returns null when nothing changed.</summary>
    public string? CommitPaths(IEnumerable<string> relativePaths, string message)
    {
        var paths = relativePaths.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return null;
        foreach (var path in paths) Run("add", "--", path);

        var diffArgs = new List<string> { "diff", "--cached", "--quiet", "--" };
        diffArgs.AddRange(paths);
        if (TryRun(diffArgs.ToArray()).ExitCode == 0) return null;

        // --only constructs the commit from these paths and leaves unrelated staged user
        // work in the index instead of silently capturing it in a framework commit.
        var commitArgs = new List<string> { "commit", "--no-verify", "--only", "-m", message, "--" };
        commitArgs.AddRange(paths);
        Run(commitArgs.ToArray());
        return HeadSha();
    }

    /// <summary>
    /// Squash-merges the work branch into main as ONE identifiable commit and returns its sha.
    /// Always squashing guarantees that reverting the promotion commit reverts the complete
    /// work package, even when earlier attempts left several commits on the branch.
    /// </summary>
    public string SquashMergeToMain(string branch, string message)
    {
        if (!IsClean())
            throw new InvalidOperationException("work branch must be clean before promotion.");
        CheckoutBranch(TenNinety.MainBranch);
        var result = TryRun("merge", "--squash", branch);
        if (result.ExitCode != 0)
            throw new GitException($"merge --squash {branch}", result.ExitCode, result.Stderr);
        // The squash merge already populated the index. Commit that reviewed index exactly;
        // never run `git add -A` here, which could capture post-review untracked files.
        var sha = CommitStaged(message)
            ?? throw new InvalidOperationException($"squash merge of '{branch}' produced no changes.");
        return sha;
    }

    public string HeadSha() => Run("rev-parse", "HEAD").Output.Trim();

    public string DiffHeadStat() => Run("diff", "HEAD", "--stat").Output;

    public string DiffAgainstMain(string branch) =>
        Run("diff", $"{TenNinety.MainBranch}...{branch}", "--stat").Output;

    /// <summary>Bounded unified patch of branch vs main; long patches keep head and tail with
    /// an elision marker so a model sees both the opening context and the latest changes.</summary>
    public string DiffPatchAgainstMain(string branch, int maxChars = 20000)
    {
        var patch = Run("diff", $"{TenNinety.MainBranch}...{branch}").Output;
        if (patch.Length <= maxChars) return patch;
        var headLen = maxChars * 3 / 4;
        var tailLen = maxChars - headLen;
        return patch[..headLen] + "\n… [diff truncated – showing head and tail] …\n" + patch[^tailLen..];
    }

    public IReadOnlyList<GitCommit> RecentCommits(int count)
    {
        var output = Run("log", $"-{count}", "--pretty=format:%H%x1f%s%x1f%an%x1f%aI").Output;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\x1f'))
            .Where(p => p.Length == 4)
            .Select(p => new GitCommit(p[0], p[1], p[2], p[3]))
            .ToList();
    }

    public GitCommit? FindCommit(string shaOrRef)
    {
        var r = TryRun("show", "-s", "--pretty=format:%H%x1f%s%x1f%an%x1f%aI", shaOrRef);
        if (r.ExitCode != 0) return null;
        var p = r.Output.Trim().Split('\x1f');
        return p.Length == 4 ? new GitCommit(p[0], p[1], p[2], p[3]) : null;
    }

    public void RevertCommitNoEdit(string sha)
    {
        try
        {
            Run("revert", "--no-edit", sha);
        }
        catch
        {
            TryAbort("revert");
            throw;
        }
    }

    /// <summary>merge-base –is-ancestor: exit 0 proves the commit is on main.</summary>
    public bool IsAncestorOfMain(string sha) =>
        TryRun("merge-base", "--is-ancestor", sha, TenNinety.MainBranch).ExitCode == 0;

    public void DeleteBranchSafe(string branch, bool force = false) =>
        Run("branch", force ? "-D" : "-d", branch);

    private string? CommitStaged(string message)
    {
        if (TryRun("diff", "--cached", "--quiet").ExitCode == 0) return null;
        Run("commit", "--no-verify", "-m", message);
        return HeadSha();
    }

    private string ResolveGitPath(string gitPath)
    {
        var resolved = Run("rev-parse", "--git-path", gitPath).Output.Trim();
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(Path.Combine(RepoPath, resolved));
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var (exitCode, output, stderr) = TryRun(args);
        if (exitCode != 0) throw new GitException(string.Join(' ', args), exitCode, stderr);
        return (exitCode, output);
    }

    private (int ExitCode, string Output, string Stderr) TryRun(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment.Clear();
        foreach (var key in EnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value)) psi.Environment[key] = value;
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start git process.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (!proc.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
                throw new TimeoutException(
                    $"git command timed out and did not terminate: {string.Join(' ', args)}");
            try { Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(5)); } catch { }
            throw new TimeoutException($"git command timed out: {string.Join(' ', args)}");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (proc.ExitCode, stdout, stderr);
    }

    private void TryAbort(string operation)
    {
        try { TryRun(operation, "--abort"); } catch { }
    }
}
