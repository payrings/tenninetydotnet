using System.Runtime.InteropServices;
using Tenninety.Execution.Testing;

namespace Tenninety.Tests;

public sealed class RestoreIntegrityValidatorTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly RestoreIntegrityValidator _validator = new();

    public RestoreIntegrityValidatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root.Root, "src", "Tests"));
        File.WriteAllText(Path.Combine(_root.Root, "README.md"), "baseline\n");
        File.WriteAllText(Path.Combine(_root.Root, "src", "Tests", "Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    [Fact]
    public void Fixed_package_and_project_obj_outputs_are_accepted_deterministically()
    {
        var (baseline, control) = Prepare();
        WriteDerived(".tenninety/restore-packages/pkg/data.bin", "package");
        WriteDerived("src/Tests/obj/project.assets.json", "assets");
        var limits = Limits();

        var first = _validator.VerifyPostRestore(baseline, control, limits, default);
        var second = _validator.VerifyPostRestore(baseline, control, limits, default);

        Assert.Equal(2, first.DerivedFiles);
        Assert.Equal(first.DerivedOutputSha256, second.DerivedOutputSha256);
        Assert.Matches("^[0-9a-f]{64}$", first.DerivedOutputSha256);
    }

    [Fact]
    public void Existing_candidate_content_cannot_be_changed_or_removed()
    {
        var (baseline, control) = Prepare();
        File.WriteAllText(Path.Combine(_root.Root, "README.md"), "changed\n");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _validator.VerifyPostRestore(baseline, control, Limits(), default));

        Assert.Contains("existing candidate", ex.Message);
    }

    [Fact]
    public void Output_outside_fixed_derived_roots_is_rejected()
    {
        var (baseline, control) = Prepare();
        WriteDerived("src/generated.cs", "unexpected");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _validator.VerifyPostRestore(baseline, control, Limits(), default));

        Assert.Contains("outside the fixed", ex.Message);
    }

    [Fact]
    public void Symlinks_and_hardlinks_are_rejected_no_follow()
    {
        var (baseline, control) = Prepare();
        var packageRoot = Path.Combine(_root.Root, ".tenninety", "restore-packages");
        Directory.CreateDirectory(packageRoot);
        File.CreateSymbolicLink(Path.Combine(packageRoot, "link"), "/etc/passwd");

        Assert.Throws<InvalidOperationException>(() =>
            _validator.VerifyPostRestore(baseline, control, Limits(), default));

        File.Delete(Path.Combine(packageRoot, "link"));
        var original = Path.Combine(packageRoot, "original");
        File.WriteAllText(original, "same inode");
        Assert.Equal(0, link(original, Path.Combine(packageRoot, "alias")));

        Assert.Throws<InvalidOperationException>(() =>
            _validator.VerifyPostRestore(baseline, control, Limits(), default));
    }

    [Fact]
    public void File_size_depth_and_directory_entry_budgets_fail_closed()
    {
        var (baseline, control) = Prepare();
        WriteDerived("src/Tests/obj/large.bin", "0123456789");
        Assert.Throws<InvalidOperationException>(() =>
            _validator.VerifyPostRestore(baseline, control,
                Limits() with { MaxDerivedFileBytes = 4 }, default));

        File.Delete(Path.Combine(_root.Root, "src", "Tests", "obj", "large.bin"));
        for (var i = 0; i < 20; i++)
            Directory.CreateDirectory(Path.Combine(
                _root.Root, ".tenninety", "restore-packages", "d" + i));
        Assert.Throws<InvalidOperationException>(() =>
            _validator.VerifyPostRestore(baseline, control,
                Limits() with { MaxDerivedFiles = 1 }, default));

        Directory.Delete(Path.Combine(_root.Root, ".tenninety", "restore-packages"), true);
        WriteDerived("src/Tests/obj/a/b/c/d/file", "deep");
        Assert.Throws<InvalidOperationException>(() =>
            _validator.VerifyPostRestore(baseline, control,
                Limits() with { MaxDepth = 4 }, default));
    }

    public void Dispose() => _root.Dispose();

    private (RestoreIntegrityValidator.Manifest Baseline,
        IReadOnlyDictionary<string, RestoreIntegrityValidator.Entry> Control) Prepare()
    {
        var baseline = _validator.CaptureBaseline(
            _root.Root, 16 * 1024 * 1024, 1000, 32, default);
        var controlRoot = Path.Combine(_root.Root, ".tenninety", "restore-control");
        Directory.CreateDirectory(controlRoot);
        File.WriteAllText(Path.Combine(controlRoot, "NuGet.Config"), "<configuration />");
        var control = _validator.CaptureTrustedControl(
            baseline, 16 * 1024 * 1024, 1008, 32, default);
        return (baseline, control);
    }

    private void WriteDerived(string relative, string content)
    {
        var path = Path.Combine(_root.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static RestoreIntegrityLimits Limits() => new(
        MaxDerivedFiles: 100,
        MaxDerivedFileBytes: 1024 * 1024,
        MaxDerivedLogicalBytes: 16 * 1024 * 1024,
        MaxDerivedAllocatedBytes: 32 * 1024 * 1024,
        MaxDepth: 32);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldpath, string newpath);
}
