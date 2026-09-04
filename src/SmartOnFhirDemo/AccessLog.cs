namespace SmartOnFhirDemo;

/// <summary>
/// One request this app made to an EHR on a launch's behalf, and what became of it.
/// Rows are the app's own data — not the EHR's — which is why they outlive a request
/// when nothing else here does.
/// </summary>
/// <param name="IssuerOrigin">
/// Which EHR, as scheme, host and port rather than as the raw <c>iss</c>. A SMART issuer
/// legitimately carries a path, and the SMART App Launcher encodes the selected patient
/// and provider into it — so keyed on the raw string, every patient would look like a
/// different EHR. <see cref="Smart.Origin"/> is the same collapse the trust check makes.
/// </param>
/// <param name="LaunchId">
/// Which launch caused the request. The narrow scope rather than the obvious one: a launch
/// asked to show its own rows must not be handed another clinician's, and issuer and patient
/// together would do exactly that for two people in one chart.
/// </param>
/// <param name="Outcome">One of <see cref="AccessOutcome"/>.</param>
public sealed record AccessLogEntry(
    DateTimeOffset OccurredAt,
    string LaunchId,
    string IssuerOrigin,
    string? PatientId,
    string? FhirUser,
    string ResourceType,
    string RequestPath,
    string Outcome,
    int? Status
)
{
    /// <summary>Assigned by the database on insert; a caller leaves it alone.</summary>
    public long Id { get; init; }
}

/// <summary>
/// What became of a read. Kept apart from the HTTP status because the two answer
/// different questions: a 403 and a launch the app itself refused are both "no", and
/// only one of them is a safety violation.
/// </summary>
public static class AccessOutcome
{
    public const string Ok = "ok";

    /// <summary>The EHR would not authorize the read.</summary>
    public const string Denied = "denied";

    public const string NotFound = "not-found";

    /// <summary>The EHR answered, and the answer was neither a read nor a refusal.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The app refused before asking: the page and the launch it resolved to disagreed
    /// about which patient was on screen.
    /// </summary>
    public const string LaunchMismatch = "launch-mismatch";
}

/// <summary>
/// Where an <see cref="AccessLogEntry"/> is written. A seam rather than a wrapper: what
/// records a read is handed this and not a whole <see cref="AccessLogContext"/>, so
/// nothing on the read path can query, track or delete.
/// </summary>
public sealed class AccessLog(AccessLogContext db)
{
    public async Task RecordAsync(AccessLogEntry entry, CancellationToken ct = default)
    {
        db.Entries.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
