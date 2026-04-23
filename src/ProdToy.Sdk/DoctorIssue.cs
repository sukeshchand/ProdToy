namespace ProdToy.Sdk;

public enum DoctorSeverity
{
    /// <summary>Nice-to-fix but not breaking anything.</summary>
    Info,
    /// <summary>Likely to cause problems soon; fix recommended.</summary>
    Warning,
    /// <summary>Something is broken; fix is required.</summary>
    Error,
}

/// <summary>
/// One individual check performed by the doctor — covers both passing
/// checks (reported so the user can see what was verified) and failing
/// ones (which carry the optional <see cref="Fix"/> action).
///
/// <para>If <see cref="Passed"/> is true, <see cref="Severity"/> and
/// <see cref="Fix"/> are ignored by the UI.</para>
/// </summary>
public sealed record DoctorCheck
{
    /// <summary>Origin — plugin display name, or "ProdToy" for host checks.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Short one-line title describing the check
    /// (e.g. "Root directory exists", "alarms.json is valid JSON").
    /// Phrase as an assertion; if it holds, Passed=true.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>True if the check was satisfied; false means it's an issue to fix.</summary>
    public required bool Passed { get; init; }

    /// <summary>Only meaningful when <see cref="Passed"/> is false.</summary>
    public DoctorSeverity Severity { get; init; } = DoctorSeverity.Error;

    /// <summary>
    /// Optional longer explanation. May include the path or setting involved.
    /// Shown for both passing and failing checks.
    /// </summary>
    public string Details { get; init; } = "";

    /// <summary>
    /// Repair action. Invoked when the user confirms the fix. Should be
    /// idempotent — running twice should be safe. Only meaningful when
    /// <see cref="Passed"/> is false.
    /// </summary>
    public Action? Fix { get; init; }

    /// <summary>
    /// True if applying the fix requires the host to restart for the change
    /// to take effect. The host will prompt the user to restart after fixes.
    /// </summary>
    public bool RequiresRestart { get; init; }
}
