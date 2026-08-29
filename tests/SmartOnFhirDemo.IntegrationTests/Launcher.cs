namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// Where the SMART App Launcher is running. It is started outside the tests, because
/// reaching the Docker socket from the test process would mean putting this user in
/// the root-equivalent docker group. See the README for the command.
/// </summary>
internal static class Launcher
{
    /// <summary>Names the environment variable holding the launcher's base URL.</summary>
    public const string UrlVariable = "SMART_LAUNCHER_URL";

    /// <summary>The launcher's base URL, or null when it has not been started.</summary>
    public static string? Url =>
        Environment.GetEnvironmentVariable(UrlVariable)?.TrimEnd('/') is { Length: > 0 } url
            ? url
            : null;

    public static bool IsRunning => Url is not null;
}
