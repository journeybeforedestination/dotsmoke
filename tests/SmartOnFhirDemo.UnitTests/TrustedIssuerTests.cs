namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The allowlist is the app's only defence against a hostile <c>iss</c>, so it is
/// tested against the ways an attacker would try to look like a trusted EHR.
/// </summary>
public class TrustedIssuerTests
{
    private const string Trusted = "https://launch.smarthealthit.org";

    private static bool IsTrusted(string? iss) => Smart.IsTrustedIssuer(iss, [Trusted]);

    // ---- What must be accepted --------------------------------------------

    [Theory]
    [InlineData("https://launch.smarthealthit.org")]
    [InlineData("https://launch.smarthealthit.org/")]
    [InlineData("https://launch.smarthealthit.org/v/r4/fhir")]
    [InlineData("https://launch.smarthealthit.org/v/r4/sim/eyJhIjoiMSJ9/fhir")]
    [InlineData("https://launch.smarthealthit.org:443/v/r4/fhir")]
    [InlineData("HTTPS://LAUNCH.SMARTHEALTHIT.ORG/v/r4/fhir")]
    public void A_trusted_origin_is_accepted_whatever_path_it_carries(string iss)
    {
        // The launcher encodes launch settings into the path, so paths must not matter.
        Assert.True(IsTrusted(iss));
    }

    [Fact]
    public void Any_entry_in_the_list_may_match()
    {
        Assert.True(Smart.IsTrustedIssuer(
            "https://ehr.example/fhir",
            ["https://other.example", "https://ehr.example", "https://third.example"]));
    }

    // ---- Impersonation ----------------------------------------------------

    [Theory]
    [InlineData("https://launch.smarthealthit.org@evil.example/fhir")]
    [InlineData("https://launch.smarthealthit.org:ignored@evil.example/fhir")]
    public void Userinfo_that_looks_like_the_trusted_host_is_refused(string iss)
    {
        // Everything before '@' is credentials; the real host is evil.example.
        Assert.False(IsTrusted(iss));
    }

    [Theory]
    [InlineData("https://evil.launch.smarthealthit.org/fhir")]
    [InlineData("https://launch.smarthealthit.org.evil.example/fhir")]
    [InlineData("https://launch-smarthealthit.org/fhir")]
    [InlineData("https://launch.smarthealthit.org.")]
    public void A_host_that_merely_resembles_the_trusted_one_is_refused(string iss)
    {
        Assert.False(IsTrusted(iss));
    }

    [Fact]
    public void A_different_port_is_a_different_origin()
    {
        Assert.False(IsTrusted("https://launch.smarthealthit.org:8443/fhir"));
    }

    [Fact]
    public void Downgrading_the_scheme_is_refused()
    {
        Assert.False(IsTrusted("http://launch.smarthealthit.org/fhir"));
    }

    // ---- Anything that is not an http(s) URL ------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/fhir")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://launch.smarthealthit.org/fhir")]
    [InlineData("gopher://launch.smarthealthit.org")]
    public void Anything_that_is_not_an_absolute_http_url_is_refused(string? iss)
    {
        Assert.False(IsTrusted(iss));
    }

    // ---- Failing closed ---------------------------------------------------

    [Fact]
    public void An_empty_allowlist_trusts_nobody()
    {
        Assert.False(Smart.IsTrustedIssuer(Trusted, []));
    }

    [Fact]
    public void A_malformed_entry_in_the_allowlist_does_not_match_everything()
    {
        Assert.False(Smart.IsTrustedIssuer("https://ehr.example/fhir", ["", "not a url"]));
    }

    [Fact]
    public void The_link_local_metadata_address_is_not_reachable_by_default()
    {
        // The classic SSRF target. Nothing about it is special — it is simply not on
        // the list — but a regression here is worth naming.
        Assert.False(IsTrusted("http://169.254.169.254/latest/meta-data/"));
    }
}
