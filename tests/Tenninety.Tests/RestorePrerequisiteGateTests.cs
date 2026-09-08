using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Invalid Restore prerequisites fail BEFORE any Docker inspection, probe, session or
/// container creation: a recording Docker transport proves ZERO Docker calls for a missing
/// lock file, a malformed lock file, duplicate lock fields, an oversized lock file, excessive
/// directory depth and excessive project count. Structural prerequisite validation is cheap,
/// local and no-follow; the locked restore remains the authoritative lock-CURRENCY check.
/// </summary>
public sealed class RestorePrerequisiteGateTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly TempDir _managedRoot = new();

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private (SandboxTesterGate Gate, CountingTransport Transport) Gate()
    {
        var git = new GitService(_repo.Root);
        var config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            Sandbox = new SandboxConfig
            {
                WorkspaceRoot = _managedRoot.Root,
                Roles = new SandboxRolesConfig
                {
                    Coder = new CoderSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.CoderImageId,
                        ModelEndpoint = "http://coder-model:8000/v1",
                    },
                    Reviewer = new ReviewerSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.ReviewerImageId,
                    },
                    Tester = new TesterSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.TesterImageId,
                    },
                },
            },
        };
        var restore = config.Sandbox.Roles.Tester.Restore;
        restore.Enabled = true;
        restore.NetworkName = "tenninety-restore";
        restore.ProxyUrl = "http://restore-proxy:3128";
        restore.ApprovedFeeds = ["https://packages.example.test/v3/index.json"];
        restore.Acceptance = new SandboxRestoreAcceptance
        {
            Version = SandboxRestoreAcceptance.CurrentVersion,
            Accepted = true,
            Repository = SandboxTesterGate.RepositoryIdentity(_repo.Root),
            Instance = "tenninety",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1).ToUniversalTime().ToString("O"),
            NetworkId = PreflightFakeTransport.NetworkIdFixed,
            FirewallProfile = "restore-egress-v1",
            StorageQuotaId = "restore-quota-v1",
            StorageQuotaBytes = 8L * 1024 * 1024 * 1024,
            HardQuotaEnforced = true,
            OperatorAcknowledged = true,
        };
        restore.Acceptance.FeedPolicySha256 = restore.ComputeFeedPolicySha256();

        var transport = new CountingTransport();
        // Production runtime/preflight factories (null seams): they may be CONSTRUCTED but
        // must never be USED before the prerequisites hold — every Docker CLI invocation
        // goes through the counting transport, which records it and fails loudly.
        var gate = new SandboxTesterGate(
            git, config, log: null,
            transportFactory: () => transport,
            runtimeFactory: null,
            preflightFactory: null,
            deleteWorkspaceOverride: _ => Task.CompletedTask);
        return (gate, transport);
    }

    private string CommitCandidate()
    {
        var git = new GitService(_repo.Root);
        git.Init();
        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"), ".tenninety/\n");
        return git.CommitAll("candidate")!;
    }

    private TesterRunContext Context(string sha, string branch) => new()
    {
        Candidate = new CandidateRevision(branch, sha, sha),
        WorkPackageId = "WP-001",
        Attempt = 1,
    };

    private static void WriteProject(TempDir repo, string relative, string? lockContent)
    {
        var projectPath = repo.Path(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        if (lockContent is not null)
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(projectPath)!, "packages.lock.json"),
                lockContent);
    }

    private static void AssertValidLock(string json)
    {
        var valid = RestorePrerequisiteValidator.IsWellFormedLockFile(
            System.Text.Encoding.UTF8.GetBytes(json), out var problem);
        Assert.True(valid, problem);
    }

    private static string AssertInvalidLock(string json)
    {
        var valid = RestorePrerequisiteValidator.IsWellFormedLockFile(
            System.Text.Encoding.UTF8.GetBytes(json), out var problem);
        Assert.False(valid);
        Assert.NotEmpty(problem);
        return problem;
    }

    private async Task<TesterInfrastructureException> RunAndCaptureAsync(string sha)
    {
        var (gate, transport) = Gate();
        var exception = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(Context(sha, "main")));
        // Not a single Docker CLI invocation may have been attempted: no inspection, no
        // probe, no session, no container.
        Assert.Equal(0, transport.Calls);
        Assert.Contains("restore prerequisite check", exception.Message);
        return exception;
    }

    [Fact]
    public async Task A_missing_lock_file_fails_with_zero_docker_calls()
    {
        WriteProject(_repo, "App.csproj", lockContent: null);
        var sha = CommitCandidate();

        var ex = await RunAndCaptureAsync(sha);

        Assert.Contains("'App.csproj'", ex.Message);
        Assert.Contains("has no 'packages.lock.json'", ex.Message);
    }

    [Fact]
    public async Task A_malformed_lock_file_fails_with_zero_docker_calls()
    {
        WriteProject(_repo, "App.csproj", lockContent: "{ not json ");
        var sha = CommitCandidate();

        var ex = await RunAndCaptureAsync(sha);

        Assert.Contains("not a well-formed packages.lock.json", ex.Message);
        Assert.Contains("malformed JSON", ex.Message);
    }

    [Fact]
    public async Task Duplicate_lock_fields_fail_with_zero_docker_calls()
    {
        WriteProject(_repo, "App.csproj", lockContent:
            "{ \"version\": 1, \"version\": 1, \"dependencies\": { \"net10.0\": {} } }");
        var sha = CommitCandidate();

        var ex = await RunAndCaptureAsync(sha);

        Assert.Contains("not a well-formed packages.lock.json", ex.Message);
        Assert.Contains("duplicate fields are rejected", ex.Message);
    }

    [Fact]
    public void Direct_dependency_with_requested_is_structurally_valid()
    {
        AssertValidLock("""
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "Example.Direct": {
                    "type": "Direct",
                    "requested": "[1.2.3, )",
                    "resolved": "1.2.3",
                    "contentHash": "direct-hash"
                  }
                }
              }
            }
            """);
    }

    [Fact]
    public void Transitive_dependency_without_requested_is_structurally_valid()
    {
        AssertValidLock("""
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "Example.Transitive": {
                    "type": "Transitive",
                    "resolved": "4.5.6",
                    "contentHash": "transitive-hash"
                  }
                }
              }
            }
            """);
    }

    [Fact]
    public void Project_dependency_without_package_fields_is_structurally_valid()
    {
        AssertValidLock("""
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "Referenced.Project": {
                    "type": "Project",
                    "dependencies": { "Example.Transitive": "[4.5.6, )" }
                  }
                }
              }
            }
            """);
    }

    [Fact]
    public void Version_2_central_transitive_dependency_is_structurally_valid()
    {
        AssertValidLock("""
            {
              "version": 2,
              "dependencies": {
                "net10.0": {
                  "Example.Central": {
                    "type": "CentralTransitive",
                    "requested": "[7.0.0, )",
                    "resolved": "7.0.0",
                    "contentHash": "central-hash"
                  }
                }
              }
            }
            """);
    }

    [Fact]
    public void Representative_committed_test_lock_dependency_shapes_are_valid()
    {
        AssertValidLock("""
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "xunit": {
                    "type": "Direct",
                    "requested": "[2.9.3, )",
                    "resolved": "2.9.3",
                    "contentHash": "direct-hash",
                    "dependencies": { "xunit.assert": "2.9.3" }
                  },
                  "xunit.assert": {
                    "type": "Transitive",
                    "resolved": "2.9.3",
                    "contentHash": "transitive-hash"
                  },
                  "tenninety.core": { "type": "Project" },
                  "tenninety.execution": {
                    "type": "Project",
                    "dependencies": { "Tenninety.Core": "[1.0.0, )" }
                  }
                }
              }
            }
            """);
    }

    [Fact]
    public void Version_3_aliased_target_layout_is_structurally_valid()
    {
        AssertValidLock("""
            {
              "version": 3,
              "linux": {
                "framework": ".NETCoreApp,Version=v10.0",
                "dependencies": {
                  "Example.Transitive": {
                    "type": "Transitive",
                    "resolved": "4.5.6",
                    "contentHash": "transitive-hash"
                  }
                }
              }
            }
            """);
    }

    [Theory]
    [InlineData("\"Unknown\"", "unknown 'type'")]
    [InlineData("42", "string 'type'")]
    public void Unknown_or_malformed_dependency_type_is_rejected_with_a_controlled_diagnostic(
        string typeJson, string expected)
    {
        var problem = AssertInvalidLock($$"""
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "Bad.Dependency": {
                    "type": {{typeJson}},
                    "requested": "[1.0.0, )",
                    "resolved": "1.0.0",
                    "contentHash": "hash"
                  }
                }
              }
            }
            """);

        Assert.Contains(expected, problem);
        Assert.DoesNotContain(typeJson, problem);
    }

    [Fact]
    public void A_direct_dependency_without_requested_is_rejected()
    {
        var problem = AssertInvalidLock("""
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "Bad.Direct": {
                    "type": "Direct",
                    "resolved": "1.0.0",
                    "contentHash": "hash"
                  }
                }
              }
            }
            """);

        Assert.Contains("string 'requested'", problem);
    }

    [Fact]
    public void Multiple_solutions_without_projects_are_explicit_restore_targets()
    {
        File.WriteAllText(_repo.Path("B.slnx"), "<Solution />");
        File.WriteAllText(_repo.Path("A.sln"), "");

        var result = RestorePrerequisiteValidator.Validate(_repo.Root);

        Assert.True(result.IsValid);
        Assert.Equal(
            [SandboxPolicy.ContainerWorkspacePath + "/A.sln",
             SandboxPolicy.ContainerWorkspacePath + "/B.slnx"],
            result.RestoreTargets);
    }

    [Fact]
    public void No_result_without_restore_targets_can_report_valid()
    {
        var result = new RestorePrerequisiteValidator.RestorePrerequisites([], []);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task An_oversized_lock_file_fails_with_zero_docker_calls()
    {
        // A structurally otherwise fine document that exceeds the persisted byte bound:
        // the bound is enforced on the BOUNDED STREAM (at most limit + 1 bytes are read),
        // never by reading the whole file first.
        var padding = new string('p', (int)RestorePrerequisiteValidator.MaxLockFileBytes + 64);
        WriteProject(_repo, "App.csproj", lockContent:
            "{ \"version\": 1, \"dependencies\": { \"net10.0\": {} }, \"pad\": \"" +
            padding + "\" }");
        var sha = CommitCandidate();

        var ex = await RunAndCaptureAsync(sha);

        Assert.Contains("exceeds", ex.Message);
        Assert.Contains("bytes", ex.Message);
    }

    [Fact]
    public async Task Excessive_directory_depth_fails_with_zero_docker_calls_instead_of_silent_skipping()
    {
        var deep = string.Join('/', Enumerable.Repeat("level", RestorePrerequisiteValidator.MaxRecursionDepth + 2));
        WriteProject(_repo, deep + "/Deep.csproj", lockContent: null);
        var sha = CommitCandidate();

        var ex = await RunAndCaptureAsync(sha);

        Assert.Contains("bounded scan depth", ex.Message);
    }

    [Fact]
    public async Task Excessive_project_count_fails_with_zero_docker_calls_instead_of_silent_skipping()
    {
        for (var i = 0; i <= RestorePrerequisiteValidator.MaxProjectsExamined; i++)
            WriteProject(_repo, $"proj{i:000}/App.csproj", lockContent: null);
        var sha = CommitCandidate();

        var ex = await RunAndCaptureAsync(sha);

        Assert.Contains("more than " + RestorePrerequisiteValidator.MaxProjectsExamined +
                        " project files", ex.Message);
        Assert.Contains("refusing unbounded examination", ex.Message);
    }

    [Fact]
    public async Task A_structurally_valid_candidate_proceeds_past_the_prerequisite_check()
    {
        // Control: with a valid project+lock the flow must reach the Docker layer (the
        // counting transport records calls and fails the preflight loudly), proving the
        // zero-call assertions above mean "prerequisites held the gate BEFORE Docker" and
        // not "the gate never ran".
        WriteProject(_repo, "App.csproj", lockContent:
            "{ \"version\": 1, \"dependencies\": { \"net10.0\": {} } }");
        var git = new GitService(_repo.Root);
        git.Init();
        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"), ".tenninety/\n");
        var sha = git.CommitAll("candidate")!;

        var (gate, transport) = Gate();

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(Context(sha, "main")));

        Assert.True(transport.Calls > 0, "the flow must reach the Docker layer for this fixture");
        Assert.Contains("preflight", ex.Message);
    }

    /// <summary>A transport that records every invocation and fails the call: any Docker use
    /// during the prerequisite phase would both register here and fail the flow loudly.</summary>
    private sealed class CountingTransport : IDockerCliTransport, IDisposable
    {
        public int Calls { get; private set; }

        public Task<DockerCliResult> RunAsync(
            DockerCliInvocation invocation, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException(
                "no docker call may happen while prerequisites are being validated");
        }

        public void Dispose() { }
    }
}
