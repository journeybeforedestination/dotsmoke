using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SmartOnFhirDemo.Pages;

/// <summary>
/// Where every launch failure lands, and the one page whose whole content came from
/// somewhere else in the app.
///
/// The sentence arrives in TempData rather than in the query string, which is what makes
/// it this app's own: a message read off the URL is a message anyone can write, and a page
/// on this domain saying whatever a stranger chose is a phishing page whether or not a
/// script ever runs. Nothing here is bound from the request.
/// </summary>
public class ErrorModel : PageModel
{
    /// <summary>What a failing launch files its sentence under.</summary>
    public const string Key = "message";

    public string? Message { get; private set; }

    public void OnGet() => Message = TempData[Key] as string;
}
