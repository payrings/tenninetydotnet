namespace Tenninety.Execution.Coding;

/// <summary>
/// A controlled operational failure of the Docker Coder boundary. The engine aborts the run
/// instead of spending a candidate retry or escalating this failure as coding feedback.
/// The inner exception chain is carried for diagnostics; the PUBLIC message stays bounded.
/// </summary>
public sealed class CoderInfrastructureException : Exception
{
    public CoderInfrastructureException(string message) : base(message) { }

    public CoderInfrastructureException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Cooperative cancellation after the Docker Coder was removed and any safe partial
/// candidate was promoted as a trusted checkpoint commit.</summary>
public sealed class CoderCheckpointedCancellationException : OperationCanceledException
{
    public CoderCheckpointedCancellationException(string? checkpointSha, CancellationToken token)
        : base("the coder was cancelled after sandbox cleanup and partial checkpoint processing.", token) =>
        CheckpointSha = checkpointSha;

    public string? CheckpointSha { get; }
}
