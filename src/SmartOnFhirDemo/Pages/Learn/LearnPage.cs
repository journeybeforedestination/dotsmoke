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
public abstract class LearnPage(IMemoryCache cache) : PageModel
{
    /// <summary>Where a launch in flight, and a finished walkthrough, are kept.</summary>
    protected IMemoryCache Cache { get; } = cache;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        base.OnPageHandlerExecuting(context);
    }

    /// <summary>The finished launch this page is reading, or null once the walkthrough has expired.</summary>
    protected CallbackOutcome.Completed? Transcript(string? state) => Cache.Transcript(state);

    protected IActionResult Fail(string message) => RedirectToPage("/Error", new { message });
}
