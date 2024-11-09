using Microsoft.AspNetCore.Mvc;

namespace dotsmoke.Controllers;

public class LaunchController : Controller
{
    [HttpGet]
    public IActionResult Initiate([FromQuery] string iss, [FromQuery] string launch)
    {
        ViewData["iss"] = iss;
        ViewData["launch"] = launch;
        return View();
    }
}