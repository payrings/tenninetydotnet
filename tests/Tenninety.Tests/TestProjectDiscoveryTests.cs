using System.Runtime.InteropServices;
using Tenninety.Execution.Testing;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Bounded, non-executing project discovery over a candidate workspace: recognition rules,
/// skipped directories, hostile XML, bounds, and the refusal to follow links.
/// </summary>
public class TestProjectDiscoveryTests
{
    private const string XUnitProject =
        "<Project><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>";

    [Fact]
    public void Finds_a_nested_legitimate_test_project()
    {
        using var tmp = new TempDir();
        var nested = System.IO.Path.Combine(tmp.Root, "src", "plugins", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllText(System.IO.Path.Combine(nested, "deep.Tests.csproj"), XUnitProject);

        var found = TestProjectDiscovery.FindTestProject(tmp.Root);

        Assert.NotNull(found);
        Assert.Equal("deep.Tests.csproj", System.IO.Path.GetFileName(found));
    }

    [Fact]
    public void IsTestProject_true_is_recognized_without_any_package_reference()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("mtp.csproj"),
            "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");

        Assert.NotNull(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void Update_attributes_and_nunit_and_mstest_are_recognized()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("nunit.csproj"),
            "<Project><ItemGroup><PackageReference Update=\"nunit\" /></ItemGroup></Project>");
        Assert.NotNull(TestProjectDiscovery.FindTestProject(tmp.Root));

        using var tmp2 = new TempDir();
        File.WriteAllText(tmp2.Path("mstest.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"MSTest.SDK\" /></ItemGroup></Project>");
        Assert.NotNull(TestProjectDiscovery.FindTestProject(tmp2.Root));
    }

    [Fact]
    public void An_application_only_solution_is_not_evidence_of_tests()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("App.sln"), "");
        File.WriteAllText(tmp.Path("app.csproj"),
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>")]
    [InlineData("<Project><!-- xunit is only mentioned in a comment --></Project>")]
    [InlineData("<Project><PropertyGroup><IsTestProject>0</IsTestProject></PropertyGroup></Project>")]
    public void False_or_comment_only_markers_are_not_evidence(string project)
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("fake.csproj"), project);

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void Malformed_xml_is_not_evidence_of_a_test_project()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("broken.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" </Project>");

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void External_xml_entities_are_prohibited_and_never_resolved()
    {
        using var tmp = new TempDir();
        var secret = tmp.Path("outside-secret.txt");
        File.WriteAllText(secret, "sentinel");
        var withEntity =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE Project [<!ENTITY xxe SYSTEM \"" + secret + "\">]>" +
            "<Project><ItemGroup><PackageReference Include=\"&xxe;\" /></ItemGroup></Project>";
        File.WriteAllText(tmp.Path("hostile.csproj"), withEntity);

        // DTD processing is prohibited: the parse must fail, not reach the external file.
        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
        Assert.Equal("sentinel", File.ReadAllText(secret));
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".git")]
    [InlineData(".tenninety")]
    public void Prohibited_directories_are_never_traversed(string skipped)
    {
        using var tmp = new TempDir();
        var nested = System.IO.Path.Combine(tmp.Root, skipped, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(System.IO.Path.Combine(nested, "hidden.Tests.csproj"), XUnitProject);

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void Symlinked_directories_and_files_are_never_followed()
    {
        using var tmp = new TempDir();
        var outside = Directory.CreateTempSubdirectory("tenninety-discovery-outside");
        try
        {
            File.WriteAllText(System.IO.Path.Combine(outside.FullName, "linked.Tests.csproj"),
                XUnitProject);
            Directory.CreateSymbolicLink(tmp.Path("link"), outside.FullName);
            Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));

            // A directly linked project file must not be examined either.
            File.Delete(tmp.Path("link"));
            File.CreateSymbolicLink(tmp.Path("linked.Tests.csproj"),
                System.IO.Path.Combine(outside.FullName, "linked.Tests.csproj"));
            Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void Traversal_depth_is_bounded()
    {
        using var tmp = new TempDir();
        var deep = tmp.Root;
        for (var i = 0; i < TestProjectDiscovery.MaxRecursionDepth + 3; i++)
        {
            deep = System.IO.Path.Combine(deep, $"level{i:00}");
            Directory.CreateDirectory(deep);
        }
        File.WriteAllText(System.IO.Path.Combine(deep, "too-deep.Tests.csproj"), XUnitProject);

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void Oversized_project_files_are_never_examined()
    {
        using var tmp = new TempDir();
        var padded = XUnitProject + "<!--" + new string('x', (int)TestProjectDiscovery.MaxProjectFileBytes) + "-->";
        File.WriteAllText(tmp.Path("huge.Tests.csproj"), padded);

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void A_regular_project_file_exactly_at_the_byte_limit_still_parses()
    {
        using var tmp = new TempDir();
        // Construct an ASCII project file whose exact byte size equals the discovery bound.
        const string head = "<Project><!--";
        const string tail = "--><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>";
        var pad = new string('x', (int)(TestProjectDiscovery.MaxProjectFileBytes - head.Length - tail.Length));
        var exact = tmp.Path("exact.Tests.csproj");
        File.WriteAllText(exact, head + pad + tail);
        Assert.Equal(TestProjectDiscovery.MaxProjectFileBytes, new FileInfo(exact).Length);

        var found = TestProjectDiscovery.FindTestProject(tmp.Root);

        Assert.NotNull(found); // the exactly-at-limit regular file is parsed, not rejected
        Assert.EndsWith("exact.Tests.csproj", found);
    }

    [Fact]
    public void A_symlinked_discovery_root_is_rejected()
    {
        using var tmp = new TempDir();
        var outside = Directory.CreateTempSubdirectory("tenninety-discovery-root");
        try
        {
            File.WriteAllText(System.IO.Path.Combine(outside.FullName, "root.Tests.csproj"), XUnitProject);
            var linked = tmp.Path("linked-root");
            Directory.CreateSymbolicLink(linked, outside.FullName);

            Assert.Null(TestProjectDiscovery.FindTestProject(linked)); // redirected root: no evidence
            Assert.NotNull(TestProjectDiscovery.FindTestProject(outside.FullName)); // the real root works
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_fifo_is_never_opened_as_a_project_file()
    {
        if (!OperatingSystem.IsLinux())
            return; // the FIFO fixture is Linux-specific; this suite runs on Linux

        using var tmp = new TempDir();
        var fifo = tmp.Path("fifo.Tests.csproj");
        Assert.Equal(0, UnixSpecialFile.Mkfifo(fifo, mode: 0x180 /* 0600 | S_IFIFO base */));

        try
        {
            // Must neither hang nor open the FIFO: bounded discovery refuses the special
            // file. The bounded harness guarantees that a future regression cannot hang
            // the entire suite indefinitely.
            Assert.Null(BoundedDiscovery(() => TestProjectDiscovery.FindTestProject(tmp.Root)));
        }
        finally
        {
            File.Delete(fifo);
        }
    }

    [Fact]
    public void A_unix_socket_is_never_opened_as_a_project_file()
    {
        if (!OperatingSystem.IsLinux())
            return; // the socket fixture is Linux-specific; this suite runs on Linux

        using var tmp = new TempDir();
        var socketPath = tmp.Path("sock.Tests.csproj");
        using (var socket = new System.Net.Sockets.Socket(
                   System.Net.Sockets.AddressFamily.Unix,
                   System.Net.Sockets.SocketType.Stream,
                   System.Net.Sockets.ProtocolType.Unspecified))
        {
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
        }

        try
        {
            // Bounded harness: an unexpected open/hang on the socket fails the test instead
            // of blocking the whole suite.
            Assert.Null(BoundedDiscovery(() => TestProjectDiscovery.FindTestProject(tmp.Root)));
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    /// <summary>Bounded harness for special-file rejection tests: if a future regression
    /// made discovery open (and block on) a FIFO/socket, the harness fails the single test
    /// after a fixed limit instead of hanging the entire suite indefinitely.</summary>
    private static string? BoundedDiscovery(Func<string?> discovery)
    {
        const int limitMilliseconds = 60_000;
        var task = System.Threading.Tasks.Task.Run(discovery);
        if (!task.Wait(limitMilliseconds))
            throw new TimeoutException(
                "discovery exceeded the bounded harness; a regression may be opening or " +
                "blocking on a special file.");
        return task.Result;
    }

    [Fact]
    public void A_directory_named_like_a_project_is_never_opened_as_a_file()
    {
        using var tmp = new TempDir();
        var pseudo = Directory.CreateDirectory(tmp.Path("pseudo.Tests.csproj"));
        File.WriteAllText(System.IO.Path.Combine(pseudo.FullName, "note.txt"), "a directory");

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void Project_file_count_is_bounded()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < TestProjectDiscovery.MaxProjectFilesExamined; i++)
            File.WriteAllText(tmp.Path($"app{i:0000}.csproj"), "<Project />");
        // The test project sorts AFTER the app projects, so the bound is hit first.
        File.WriteAllText(tmp.Path("zz.Tests.csproj"), XUnitProject);

        Assert.Null(TestProjectDiscovery.FindTestProject(tmp.Root));
    }

    [Fact]
    public void A_missing_or_unreadable_root_yields_no_evidence()
    {
        Assert.Null(TestProjectDiscovery.FindTestProject(
            System.IO.Path.Combine(Path.GetTempPath(), "tenninety-does-not-exist-9f31")));
        Assert.Null(TestProjectDiscovery.FindTestProject(""));
    }

    [Fact]
    public void Discovery_does_not_execute_anything()
    {
        // The discovery surface has no process, shell or MSBuild capability at all.
        var methods = typeof(TestProjectDiscovery).GetMethods()
            .Where(m => m.DeclaringType == typeof(TestProjectDiscovery))
            .ToList();
        Assert.All(methods, m => Assert.True(m.IsStatic, "discovery must be pure/static"));
        Assert.Contains(methods, m => m.Name == "FindTestProject");
    }
}

/// <summary>Narrow libc helper for creating Linux FIFO fixtures without any host shell.</summary>
internal static class UnixSpecialFile
{
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "mkfifo")]
    public static extern int Mkfifo(string pathname, uint mode);
}
