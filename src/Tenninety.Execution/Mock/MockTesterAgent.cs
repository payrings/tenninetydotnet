using Tenninety.Core.Models;
using Tenninety.Execution.Testing;

namespace Tenninety.Execution.Mock;

/// <summary>
/// Deterministic in-process Tester for mock/rehearsal mode. It preserves the configured
/// simulated failure window (and the existing advice-related escape from it), otherwise
/// returns a deterministic simulated pass carrying the requested candidate SHA. It never
/// discovers or executes repository tests, never creates Docker clients, containers or
/// candidate workspaces, and never starts a host shell — a mock run stays Docker-independent
/// even when its configuration contains empty image fields or an enabled restore setting.
/// </summary>
public sealed class MockTesterAgent : ITesterAgent
{
    private readonly int _simulatedFailAttempts;

    public MockTesterAgent(int simulatedFailAttempts = 0) => _simulatedFailAttempts = simulatedFailAttempts;

    public Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
    {
        ctx.Validate();
        var candidateSha = ctx.Candidate.CommitSha;

        // Simulated failure window for exercising retry/escalation paths without a real suite.
        if (_simulatedFailAttempts > 0 && ctx.Attempt <= _simulatedFailAttempts && ctx.Advice.Count == 0)
            return Task.FromResult(new TestRunResult
            {
                Passed = false,
                ExitCode = 1,
                Command = "(simulated)",
                OutputTail = $"Simulated mechanical failure on attempt {ctx.Attempt}.",
                CandidateSha = candidateSha,
            });

        return Task.FromResult(new TestRunResult
        {
            Passed = true,
            ExitCode = 0,
            Command = "(mock – simulated pass)",
            OutputTail = "mock tester: deterministic simulated pass (no repository tests executed)",
            CandidateSha = candidateSha,
        });
    }
}
