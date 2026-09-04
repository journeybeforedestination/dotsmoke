using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// Shared by the narrated launch pages. They put a live authorization code, patient
/// demographics and a whole FHIR resource on screen, so none of it may be kept by a
/// browser, a proxy, or anything else between here and the reader.
/// </summary>
public abstract class LearnPage(IMemoryCache cache, AccessLog log, TimeProvider clock) : PageModel
{
    protected IMemoryCache Cache { get; } = cache;

    protected TimeProvider Clock { get; } = clock;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        base.OnPageHandlerExecuting(context);
    }

    /// <summary>
    /// The launch this page is narrating, or null once the refusal has been recorded.
    /// Before the exchange the walkthrough is keyed by the OAuth state, because there is
    /// no launch yet; after it, by the two values that name a launch that happened.
    /// Keeping those lifetimes apart is step 6's whole point.
    ///
    /// Every page that resolves a launch comes through here, because the mismatch below
    /// is the failure the session design exists to prevent and a page that checked it
    /// its own way is a page that can stop checking.
    /// </summary>
    protected async Task<LaunchView?> LaunchAsync(string? id, string? patient, CancellationToken ct)
    {
        var resolution = Cache.Resolve(BrowserSession.Current(HttpContext), id, patient, Clock);

        switch (resolution)
        {
            case LaunchResolution.Resolved(var view):
                return view;

            case LaunchResolution.PatientMismatch(var facts, var claimed):
                // Unlike an expiry, this is worth knowing happened at all.
                await log.RecordAsync(Refused(facts, claimed), ct);
                return null;

            case LaunchResolution.Unknown
            or LaunchResolution.Expired:
                return null;

            default:
                throw new UnreachableException($"{resolution.GetType().Name} is not a resolution.");
        }
    }

    /// <summary>
    /// A read that was refused before anything was asked of the EHR. The patient recorded
    /// is the one the page claimed rather than the one the launch holds, because the
    /// question this row answers is whose chart someone was about to be shown.
    /// </summary>
    private AccessLogEntry Refused(LaunchFacts facts, string claimed) =>
        new(
            Clock.GetUtcNow(),
            facts.IssuerOrigin,
            claimed,
            facts.FhirUser,
            "Patient",
            $"Patient/{claimed}",
            AccessOutcome.LaunchMismatch,
            // Nothing was sent, so there is no status to report.
            Status: null
        );

    protected IActionResult Relaunch(string? patient) => Fail(LaunchMessages.Relaunch(patient));

    /// <summary>
    /// The sentence goes in TempData, not in the redirect's query string. See
    /// <see cref="ErrorModel"/> for why, and Razor Pages saves it across the redirect.
    /// </summary>
    protected IActionResult Fail(string message)
    {
        TempData[ErrorModel.Key] = message;
        return RedirectToPage("/Error");
    }
}
