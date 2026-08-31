using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages;

/// <summary>
/// Where a launch lands, and — unlike the callback it replaced — a page you can come back
/// to. It renders nothing it fetches: the launch was established at the callback, and this
/// resolves it from the two values that name it.
/// </summary>
public class SummaryModel(IMemoryCache cache, TimeProvider clock) : PageModel
{
    public PatientSummary Summary { get; private set; } = default!;

    public string RawJson { get; private set; } = "";

    /// <summary>Who started this launch, or why the app cannot say.</summary>
    public string WhoLaunchedIt { get; private set; } = "";

    public IActionResult OnGet(string? id)
    {
        // The cookie says which browser; the id says which of that browser's launches.
        // What comes back is a LaunchFacts and the account to render — never the token.
        if (cache.View(BrowserSession.Current(HttpContext), id, clock) is not { } view)
            return RedirectToPage("/Error", new { message = LaunchMessages.UnknownLaunch });

        Summary = view.Rendered.Summary;
        RawJson = view.Rendered.RawJson;
        WhoLaunchedIt = LaunchMessages.WhoLaunchedIt(view.Rendered);
        return Page();
    }

    /// <summary>
    /// This URL is stable and revisitable now, which the one-shot callback was not. That
    /// makes what a browser or a proxy keeps of it worth saying out loud: nothing.
    /// </summary>
    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        base.OnPageHandlerExecuting(context);
    }
}
