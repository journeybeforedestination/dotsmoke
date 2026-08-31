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
public abstract class LearnPage(IMemoryCache cache, TimeProvider clock) : PageModel
{
    /// <summary>Where a launch in flight, and an established one, are kept.</summary>
    protected IMemoryCache Cache { get; } = cache;

    /// <summary>The clock a launch's expiry is checked against.</summary>
    protected TimeProvider Clock { get; } = clock;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        base.OnPageHandlerExecuting(context);
    }

    /// <summary>
    /// The launch this page is narrating, resolved exactly as the plain summary resolves
    /// its own. Before the exchange the walkthrough is keyed by the OAuth state, because
    /// there is no launch yet; after it, by the two values that name a launch that
    /// happened. Keeping those lifetimes apart is step 6's whole point.
    /// </summary>
    protected LaunchView? Launch(string? id, string? patient) =>
        Cache.Resolve(BrowserSession.Current(HttpContext), id, patient, Clock)
            is LaunchResolution.Resolved(var view)
            ? view
            : null;

    protected IActionResult Relaunch(string? patient) => Fail(LaunchMessages.Relaunch(patient));

    protected IActionResult Fail(string message) => RedirectToPage("/Error", new { message });
}
