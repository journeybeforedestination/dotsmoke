using System.Diagnostics;
using System.Globalization;

namespace SmartOnFhirDemo;

/// <summary>
/// One logged request as a reader of the page sees it: a sentence for what was asked and a
/// sentence for what came back, either side of the FHIR request itself. Both, because prose
/// alone would hide the request, and the request is what this app exists to teach.
/// </summary>
public sealed record AccessLine(string At, string Asked, string Request, string Answer);

/// <summary>
/// One launch's access log, ready to render. A projection and nothing else — it reads
/// nothing and reaches nothing — so what the section says can be pinned by a unit test.
/// </summary>
/// <param name="Truncated">Whether there were more rows than <see cref="Rows"/>.</param>
public sealed record AccessView(
    string IssuerOrigin,
    string? FhirUser,
    IReadOnlyList<AccessLine> Lines,
    bool Truncated
)
{
    /// <summary>
    /// How many rows the section shows. It is built on every page load whether or not
    /// anyone expands it, and an established launch can go on reading panels for as long as
    /// its token lasts, so the list has a top.
    /// </summary>
    public const int Rows = 50;

    /// <summary>
    /// The caller reads one row past <see cref="Rows"/>, so a list that was cut can say so
    /// rather than simply ending.
    /// </summary>
    public static AccessView Of(LaunchFacts facts, IReadOnlyList<AccessLogEntry> newestFirst) =>
        new(
            facts.IssuerOrigin,
            facts.FhirUser,
            [.. newestFirst.Take(Rows).Select(Line)],
            newestFirst.Count > Rows
        );

    private static AccessLine Line(AccessLogEntry entry) =>
        new(
            entry.OccurredAt.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Asked(entry),
            entry.RequestPath,
            Answer(entry)
        );

    /// <summary>
    /// What the request was for, read off the path rather than recorded at the time: a
    /// query string is a search and a bare path is a read, which is all the words have to
    /// add — the request beside them says the rest.
    /// </summary>
    private static string Asked(AccessLogEntry entry) =>
        entry.RequestPath switch
        {
            "metadata" => "The server's capability statement",
            var path when path.Contains('?', StringComparison.Ordinal) =>
                $"A search for {entry.ResourceType}",
            _ => $"A read of {entry.ResourceType}",
        };

    /// <summary>
    /// What became of it, in the same words the outcome was kept apart from the status for:
    /// a reader asks whether the read happened, not which 4xx an implementation chose.
    /// </summary>
    private static string Answer(AccessLogEntry entry) =>
        entry.Outcome switch
        {
            AccessOutcome.Ok => With("the EHR answered", entry.Status),
            AccessOutcome.Denied => With("the EHR would not authorize it", entry.Status),
            AccessOutcome.NotFound => With("the EHR had no such record", entry.Status),
            AccessOutcome.Failed => With("the EHR could not answer", entry.Status),

            // Worth a sentence rather than a label: this is the one row that is about the
            // app refusing itself, and it is the failure the session design exists to stop.
            AccessOutcome.LaunchMismatch =>
                "nothing was sent — the page claimed a patient this launch does not name",

            _ => throw new UnreachableException($"'{entry.Outcome}' is not an outcome."),
        };

    /// <summary>
    /// The status in brackets, or no brackets at all. It is null exactly when nothing left
    /// the app, and a rendered 0 would read as something the EHR said.
    /// </summary>
    private static string With(string answer, int? status) =>
        status is { } code ? $"{answer} ({code})" : answer;
}
