using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo;

/// <summary>
/// A panel of the chart the summary can show, and the search behind it. A closed list,
/// because each one is a scope this app asked the EHR for by name.
/// </summary>
/// <param name="Criteria">Search parameters beyond the patient, which every panel has.</param>
public sealed record ChartPanel(
    string Slug,
    string Title,
    string ResourceType,
    IReadOnlyList<string> Criteria
)
{
    public static readonly ChartPanel Conditions = new("conditions", "Conditions", "Condition", []);

    public static readonly ChartPanel Vitals = new(
        "vitals",
        "Vital signs",
        "Observation",
        ["category=vital-signs"]
    );

    public static readonly ChartPanel Medications = new(
        "medications",
        "Medications",
        "MedicationRequest",
        []
    );

    public static IReadOnlyList<ChartPanel> All { get; } = [Conditions, Vitals, Medications];

    /// <summary>
    /// The panel a link asked for, or null. A name that is not one of these shows nothing
    /// rather than failing: the only way to get here is a link this app wrote.
    /// </summary>
    public static ChartPanel? For(string? slug) =>
        All.FirstOrDefault(panel => string.Equals(panel.Slug, slug, StringComparison.Ordinal));
}

/// <summary>What came of asking the EHR for a panel.</summary>
public abstract record ChartOutcome
{
    private ChartOutcome() { }

    /// <summary>
    /// Which panel this is about. Declared here so every case has to name one and the view
    /// can just ask, rather than switching over the hierarchy a second time in markup.
    /// </summary>
    public abstract ChartPanel Panel { get; init; }

    public sealed record Read(ChartPanel Panel, IReadOnlyList<string> Entries) : ChartOutcome;

    /// <summary>The search worked, and this patient has none of these.</summary>
    public sealed record Empty(ChartPanel Panel) : ChartOutcome;

    /// <summary>
    /// The EHR would not authorize the search. Asking for a scope does not oblige an EHR
    /// to grant it, and an app is expected to cope with getting less than it asked for.
    /// </summary>
    public sealed record Denied(ChartPanel Panel, int Status) : ChartOutcome;

    /// <param name="Status">
    /// What the EHR answered with, or null when nothing was sent — a call this app held
    /// back, or one that never reached a server, has no status to report.
    /// </param>
    public sealed record Unavailable(ChartPanel Panel, int? Status, string Reason) : ChartOutcome;

    /// <summary>
    /// The launch went away between the page resolving it and this read. Only a token
    /// expiring in that gap can reach it — the page refuses everything else first.
    /// </summary>
    public sealed record LaunchGone(ChartPanel Panel) : ChartOutcome;
}

/// <summary>
/// The panels a page offers, the links to them, and the one it is showing. Shared by the
/// plain summary and the narrated one so the two cannot drift apart: they are the same
/// launch read the same way, and a reader who took the long route should not arrive with
/// less than one who did not.
/// </summary>
/// <param name="Path">The page the links point back at, which is the only difference between them.</param>
public sealed record ChartView(string Path, string LaunchId, string PatientId, ChartOutcome? Shown)
{
    public string Link(ChartPanel panel) =>
        $"{Path}?id={Uri.EscapeDataString(LaunchId)}"
        + $"&patient={Uri.EscapeDataString(PatientId)}"
        + $"&show={panel.Slug}";
}

/// <summary>
/// The follow-up reads a summary can make once a launch is something you can come back
/// to. It takes the launch by name rather than by context so the credential stays below
/// the page: the caller says which browser, which launch and which patient, and what
/// comes back is a panel or a sentence.
/// </summary>
public sealed partial class Chart(
    IMemoryCache cache,
    FhirClients clients,
    TimeProvider clock,
    ILogger<Chart> log
)
{
    /// <summary>Enough to fill a panel. This is a summary, not a chart review.</summary>
    private const int PageSize = 20;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the {panel} panel failed")]
    private partial void LogPanelFailed(Exception ex, string panel);

    /// <summary>
    /// Everything a page needs to show the panels: the links, and whatever <c>show</c>
    /// asked for. One method rather than a rule each page remembers — that a name which is
    /// not a panel shows nothing rather than failing is decided once, here.
    /// </summary>
    public async Task<ChartView> ViewAsync(
        string path,
        string? sid,
        LaunchFacts facts,
        string? show,
        CancellationToken ct
    ) =>
        new(
            path,
            facts.LaunchId,
            facts.PatientId,
            ChartPanel.For(show) is { } panel
                ? await ReadAsync(sid, facts.LaunchId, facts.PatientId, panel, ct)
                : null
        );

    public async Task<ChartOutcome> ReadAsync(
        string? sid,
        string? launchId,
        string? patientId,
        ChartPanel panel,
        CancellationToken ct
    )
    {
        if (cache.Credential(sid, launchId, patientId, clock) is not { } context)
            return new ChartOutcome.LaunchGone(panel);

        using var client = clients.Open(context);

        try
        {
            var bundle = await client.Fhir.SearchAsync(
                panel.ResourceType,
                [$"patient={context.PatientId}", .. panel.Criteria],
                pageSize: PageSize,
                ct: ct
            );

            var entries = Describe(bundle);

            return entries is []
                ? new ChartOutcome.Empty(panel)
                : new ChartOutcome.Read(panel, entries);
        }
        catch (FhirOperationException ex)
        {
            // Not against the SMART App Launcher, which does not enforce scopes the way a
            // real EHR does — so a launch there proves the search works, and proves rather
            // less about what happens when a scope is refused.
            LogPanelFailed(ex, panel.Slug);

            return (int)ex.Status is 401 or 403
                ? new ChartOutcome.Denied(panel, (int)ex.Status)
                : new ChartOutcome.Unavailable(
                    panel,
                    (int)ex.Status,
                    LaunchMessages.Describe(ex.Outcome) ?? ex.Message
                );
        }
        // Firely does not wrap transport failures: BaseFhirClient hands the request to
        // HttpClient without a try/catch, so these arrive here untouched and would
        // otherwise escape the panel read and lose the whole summary to /error.
        // TaskCanceledException for the same reason the launch names it — the clients
        // carry a timeout, and an EHR that goes quiet is one panel's problem, not the
        // page's.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogPanelFailed(ex, panel.Slug);

            return new ChartOutcome.Unavailable(panel, null, ex.Message);
        }
    }

    private static IReadOnlyList<string> Describe(Bundle? bundle) =>
        [.. (bundle?.Entry ?? []).Select(entry => entry.Resource).OfType<Resource>().Select(Line)];

    /// <summary>
    /// One line per resource. Firely's typed POCOs do the work: the same
    /// <c>CodeableConcept</c> shape names a condition, an observation and a medication,
    /// and each carries the display text the EHR chose rather than a code this app maps.
    /// </summary>
    private static string Line(Resource resource) =>
        resource switch
        {
            Condition condition => Join(
                Text(condition.Code),
                condition.ClinicalStatus is { } status ? Text(status) : null
            ),

            Observation observation => Join(
                Text(observation.Code),
                observation.Value switch
                {
                    Quantity { Value: not null } quantity =>
                        $"{quantity.Value}{(quantity.Unit is { } unit ? $" {unit}" : "")}",
                    CodeableConcept concept => Text(concept),
                    FhirString text => text.Value,
                    _ => null,
                }
            ),

            MedicationRequest request => Join(
                request.Medication switch
                {
                    CodeableConcept concept => Text(concept),
                    ResourceReference reference => reference.Display ?? reference.Reference,
                    _ => null,
                },
                request.Status?.GetLiteral()
            ),

            _ => resource.TypeName,
        };

    /// <summary>
    /// The display text of a coded value: what the EHR called it, then what the code
    /// system calls it, and only then the bare code — a code on screen is a last resort.
    /// </summary>
    private static string? Text(CodeableConcept? concept) =>
        concept?.Text
        ?? concept?.Coding.Select(coding => coding.Display ?? coding.Code).FirstOrDefault();

    private static string Join(string? subject, string? qualifier) =>
        (subject, qualifier) switch
        {
            ({ } name, { } detail) => $"{name} — {detail}",
            ({ } name, null) => name,
            (null, { } detail) => detail,
            _ => "(unnamed)",
        };
}
