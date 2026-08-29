using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SmartOnFhirDemo.Pages;

public class ErrorModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Message { get; set; }
}
