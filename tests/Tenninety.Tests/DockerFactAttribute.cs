using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Opt-in gate for xUnit tests that require a live Docker daemon with a pinned test image.
///
/// Gating contract:
///  - When <c>TENNINETY_RUN_DOCKER_TESTS</c> is not exactly "1", the test is discovered and
///    reported skipped with a clear reason (never silently passed).
///  - When opted in, Skip is NOT set for any reason: a missing, malformed or unavailable
///    TENNINETY_TEST_IMAGE, or unavailable Docker, fails the test body through
///    <see cref="DockerTestHelper"/> instead of converting a requested run into a skip.
///  - Images are never pulled and no mutable tag or digest is suggested.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        var runDocker = Environment.GetEnvironmentVariable("TENNINETY_RUN_DOCKER_TESTS");
        if (runDocker != "1")
        {
            Skip = "Docker integration test skipped: TENNINETY_RUN_DOCKER_TESTS is not set to " +
                   "'1'. Opt in with TENNINETY_RUN_DOCKER_TESTS=1 and an exact " +
                   "TENNINETY_TEST_IMAGE (sha256:<64 lowercase hex> local image ID) to run it.";
        }
        // Opted in: no Skip. The test helper validates the image and fails the test when
        // the contract is not met.
    }
}

/// <summary>
/// Validates the opt-in environment and resolves the exact test image through the PRODUCTION
/// typed Docker CLI (never pulls, never suggests pulling). Once opted in, any contract
/// violation fails the test.
/// </summary>
public static class DockerTestHelper
{
    /// <summary>Minimum executable contract for TENNINETY_TEST_IMAGE: a LOCALLY PRESENT Linux
    /// image whose exact ID equals the provided sha256 value, whose Dockerfile declares an
    /// explicit numeric non-root user (e.g. USER 1000:1000), has no ENTRYPOINT, and contains
    /// the POSIX utilities `sleep` and `touch`. Nothing else is assumed.</summary>
    public static async Task<DockerImageInfo> ResolveTestImageAsync()
    {
        var runDocker = Environment.GetEnvironmentVariable("TENNINETY_RUN_DOCKER_TESTS");
        if (runDocker != "1")
            throw new InvalidOperationException(
                "Docker integration tests require TENNINETY_RUN_DOCKER_TESTS=1.");

        var testImage = Environment.GetEnvironmentVariable("TENNINETY_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(testImage))
            throw new InvalidOperationException(
                "TENNINETY_TEST_IMAGE is required once TENNINETY_RUN_DOCKER_TESTS=1: provide an " +
                "exact sha256:<64 lowercase hex> local image ID that already exists on this daemon.");
        if (!testImage.StartsWith("sha256:", StringComparison.Ordinal) ||
            testImage.Length != 71 ||
            !testImage[7..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new InvalidOperationException(
                "TENNINETY_TEST_IMAGE must be an exact 'sha256:<64 lowercase hex>' local image ID.");

        var transport = new DockerCliProcessTransport();
        try
        {
            var cli = new DockerCli(transport);
            // InspectImageAsync verifies the exact local image ID and never pulls: a missing
            // or mismatched image fails the test here.
            return await cli.InspectImageAsync(testImage);
        }
        finally
        {
            transport.Dispose();
        }
    }
}

/// <summary>
/// Base opt-in gate for the five Docker role/end-to-end categories. Each derived attribute
/// reads its OWN documented opt-in environment variable; when it is not exactly "1" the test
/// is discovered and reported skipped with the precise prerequisite contract. When opted in,
/// Skip is NOT set: missing or malformed images, endpoints, networks or Restore acceptance
/// fail the test body through <see cref="DockerTestHelper"/> before any Docker container or
/// network use — a requested run is never converted into a skip.
/// </summary>
public abstract class DockerCategoryFactAttribute : FactAttribute
{
    protected DockerCategoryFactAttribute(string optInVar, string prerequisites)
    {
        var run = Environment.GetEnvironmentVariable(optInVar);
        if (run != "1")
        {
            Skip = $"{optInVar} is not set to '1'; the test is discovered but skipped. " +
                   $"Opt in with {optInVar}=1 and the documented prerequisites: {prerequisites}";
        }
    }
}

/// <summary>Live Coder gate: full real-Docker materialization → spec → create → exec →
/// removal-proof → scan/promotion against a disposable authoritative repository.</summary>
public sealed class DockerCoderFactAttribute : DockerCategoryFactAttribute
{
    public DockerCoderFactAttribute()
        : base("TENNINETY_RUN_DOCKER_CODER_TESTS",
            "TENNINETY_CODER_TEST_IMAGE + TENNINETY_REVIEWER_TEST_IMAGE + " +
            "TENNINETY_TESTER_TEST_IMAGE (exact sha256:<64 hex> local image IDs, numeric " +
            "non-root USER, no ENTRYPOINT), TENNINETY_TEST_MODEL_NETWORK (pre-existing " +
            "non-reserved network name) and TENNINETY_CODER_TEST_MODEL_ENDPOINT " +
            "(container-reachable http(s) URL). Images are never pulled; the model network " +
            "must already exist.")
    {
    }
}

/// <summary>Live Reviewer gate: real offline Docker Reviewer session driven by a deterministic
/// scripted host-side model client; guest writes discarded; removal proven.</summary>
public sealed class DockerReviewerFactAttribute : DockerCategoryFactAttribute
{
    public DockerReviewerFactAttribute()
        : base("TENNINETY_RUN_DOCKER_REVIEWER_TESTS",
            "TENNINETY_CODER_TEST_IMAGE + TENNINETY_REVIEWER_TEST_IMAGE + " +
            "TENNINETY_TESTER_TEST_IMAGE (exact sha256:<64 hex> local image IDs, numeric " +
            "non-root USER, no ENTRYPOINT), TENNINETY_TEST_MODEL_NETWORK and " +
            "TENNINETY_CODER_TEST_MODEL_ENDPOINT (the gate preflight probes every role). " +
            "Images are never pulled.")
    {
    }
}

/// <summary>Live Tester gate: real offline Docker build/test against a materialized fixture;
/// implicit dependency restore rejected by the offline network; cleanup proven.</summary>
public sealed class DockerTesterFactAttribute : DockerCategoryFactAttribute
{
    public DockerTesterFactAttribute()
        : base("TENNINETY_RUN_DOCKER_TESTER_TESTS",
            "TENNINETY_CODER_TEST_IMAGE + TENNINETY_REVIEWER_TEST_IMAGE + " +
            "TENNINETY_TESTER_TEST_IMAGE (exact sha256:<64 hex> local image IDs; the tester " +
            "image must additionally contain the .NET SDK for offline build/test), " +
            "TENNINETY_TEST_MODEL_NETWORK and TENNINETY_CODER_TEST_MODEL_ENDPOINT. Images " +
            "are never pulled.")
    {
    }
}

/// <summary>Live Restore gate: the complete versioned operator contract must be present in
/// the environment before any Docker use. Without it the category stays discovered/skipped
/// and Restore stays default-disabled.</summary>
public sealed class DockerRestoreFactAttribute : DockerCategoryFactAttribute
{
    public DockerRestoreFactAttribute()
        : base("TENNINETY_RUN_DOCKER_RESTORE_TESTS",
            "TENNINETY_CODER_TEST_IMAGE + TENNINETY_REVIEWER_TEST_IMAGE + " +
            "TENNINETY_TESTER_TEST_IMAGE (exact sha256:<64 hex> local image IDs) plus the " +
            "complete operator contract: TENNINETY_RESTORE_TEST_NETWORK (pre-existing " +
            "restricted network), TENNINETY_RESTORE_TEST_PROXY_URL, " +
            "TENNINETY_RESTORE_TEST_FEEDS (comma-separated https feeds), " +
            "TENNINETY_RESTORE_TEST_QUOTA_BYTES, TENNINETY_RESTORE_TEST_QUOTA_ID, " +
            "TENNINETY_RESTORE_TEST_FIREWALL_PROFILE, TENNINETY_RESTORE_TEST_EXPIRES_UTC " +
            "(round-trip UTC), TENNINETY_RESTORE_TEST_OPERATOR_ACK=1, and " +
            "TENNINETY_TEST_MODEL_NETWORK + TENNINETY_CODER_TEST_MODEL_ENDPOINT. Images are " +
            "never pulled.")
    {
    }
}

/// <summary>Live end-to-end: deterministic Coder (fixture command) → trusted promotion →
/// fresh Reviewer (scripted chat) → fresh Tester (offline fixture) with exact candidate SHA
/// propagation and complete cleanup.</summary>
public sealed class DockerEndToEndFactAttribute : DockerCategoryFactAttribute
{
    public DockerEndToEndFactAttribute()
        : base("TENNINETY_RUN_DOCKER_E2E_TESTS",
            "TENNINETY_CODER_TEST_IMAGE + TENNINETY_REVIEWER_TEST_IMAGE + " +
            "TENNINETY_TESTER_TEST_IMAGE (exact sha256:<64 hex> local image IDs; the tester " +
            "image must contain the .NET SDK), TENNINETY_TEST_MODEL_NETWORK and " +
            "TENNINETY_CODER_TEST_MODEL_ENDPOINT. Images are never pulled.")
    {
    }
}
