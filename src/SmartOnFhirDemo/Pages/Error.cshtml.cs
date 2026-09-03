using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SmartOnFhirDemo.Pages;

/// <summary>
/// Where every launch failure lands. The sentence arrives in TempData rather than the query
/// string, which is what makes it this app's own: a page on this domain saying whatever a
/// stranger put in a URL is a phishing page whether or not a script ever runs. Nothing here
/// is bound from the request.
/// </summary>
public class ErrorModel : PageModel
{
    /// <summary>What a failing launch files its sentence under.</summary>
    public const string Key = "message";

    public string? Message { get; private set; }

    public void OnGet() => Message = TempData[Key] as string;
}
