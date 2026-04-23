namespace ProdToy.Sdk;

/// <summary>
/// Optional interface a plugin can implement to participate in the host's
/// "Doctor" feature (Settings → About → Run Diagnostics).
///
/// The host runs <see cref="Diagnose"/> to collect issues, then — if the user
/// confirms — applies each issue's <see cref="DoctorIssue.Fix"/> action to
/// repair the problem.
///
/// Plugins that don't implement this interface are silently skipped.
/// </summary>
public interface IDoctor
{
    /// <summary>
    /// Run all checks and return the result of each one (pass and fail).
    /// Should be fast (≲ 1 second total) — avoid network calls. Reporting
    /// every check (not just failures) lets the UI show the user what was
    /// actually verified.
    /// </summary>
    IReadOnlyList<DoctorCheck> Diagnose();
}
