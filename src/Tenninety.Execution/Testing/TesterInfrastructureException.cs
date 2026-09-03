namespace Tenninety.Execution.Testing;

/// <summary>Provenance of a <see cref="TesterInfrastructureException"/> message. The
/// exception's CLR type alone NEVER establishes that its message is safe to publish: any
/// lower layer or injected component can construct the type with arbitrary text through the
/// public constructor. Publication decisions therefore inspect the explicit provenance
/// marker, never the type and never message content.</summary>
public enum TesterInfrastructureProvenance
{
    /// <summary>The message text is arbitrary: the instance was created through the public
    /// constructor (or by any code without access to the internal factory). Its message is
    /// never copied into public Tester diagnostics — the failure is reduced to controlled
    /// categories like any other unknown exception.</summary>
    Untrusted = 0,

    /// <summary>The message was composed by the Tester itself from controlled failure
    /// categories, stages and bounded non-secret identifiers, and was length-bound at
    /// composition time. Trusted for publication (still re-sanitized as defense in depth).</summary>
    Controlled,
}

/// <summary>
/// Typed infrastructure/refusal failure of the offline Docker Tester. It distinguishes
/// gate-level infrastructure and refusal outcomes (configuration refusal, failed preflight,
/// failed materialization, failed container creation, session infrastructure exceptions,
/// a synthetic negative exit with no operational flag, an indeterminate infrastructure-layer
/// command cancellation, authoritative host-state mismatch, unproven cleanup/retention) from
/// ordinary candidate build/test failures, which are reported as regular failed
/// <see cref="TestRunResult"/> values and may trigger the normal Coder retry path.
///
/// The engine's existing infrastructure-exception handling treats any tester exception as a
/// run-aborting infrastructure fault: no feedback is fed back into a new coding attempt, no
/// promotion happens and no Frontier escalation runs.
///
/// Message provenance: instances created by the Tester itself are built through the internal
/// <see cref="Controlled"/> factory from controlled, length-bounded compositions and carry
/// <see cref="TesterInfrastructureProvenance.Controlled"/>. The PUBLIC constructor accepts
/// arbitrary text and yields <see cref="TesterInfrastructureProvenance.Untrusted"/> — a
/// lower layer or injected session throwing this type can never bypass the controlled-
/// diagnostics boundary, because the gate publishes only provenance-marked messages and
/// reduces everything else to controlled categories plus the exception type name.
/// No raw host path, credential or arbitrary inner exception chain is attached.
/// </summary>
public sealed class TesterInfrastructureException : InvalidOperationException
{
    /// <summary>Explicit provenance of <see cref="Exception.Message"/>. Only instances with
    /// <see cref="TesterInfrastructureProvenance.Controlled"/> may be published verbatim.</summary>
    public TesterInfrastructureProvenance Provenance { get; }

    private TesterInfrastructureException(string message, TesterInfrastructureProvenance provenance)
        : base(message) => Provenance = provenance;

    /// <summary>Public constructor: the message text is ARBITRARY and carries no provenance.
    /// Instances built here are never copied into public Tester diagnostics; the gate reduces
    /// them to the controlled stage/category plus the exception type name.</summary>
    public TesterInfrastructureException(string message) : base(message)
    {
        Provenance = TesterInfrastructureProvenance.Untrusted;
    }

    /// <summary>Internal factory for the Tester's OWN controlled compositions. The caller
    /// must have composed the message entirely from controlled categories and bounded
    /// non-secret identifiers (and applied the complete-message bound). This is the only way
    /// to obtain a <see cref="TesterInfrastructureProvenance.Controlled"/> instance.</summary>
    internal static TesterInfrastructureException Controlled(string controlledMessage) =>
        new(controlledMessage, TesterInfrastructureProvenance.Controlled);
}
