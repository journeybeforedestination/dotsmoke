namespace SmartOnFhirDemo.UnitTests;

public class SecurityHeadersTests
{
    private static string Value(bool secure, string name) =>
        SecurityHeaders.For(secure).Single(header => header.Key == name).Value;

    [Theory]
    [InlineData("Content-Security-Policy")]
    [InlineData("X-Content-Type-Options")]
    [InlineData("Referrer-Policy")]
    public void The_headers_that_do_not_depend_on_the_scheme_are_sent_either_way(string name)
    {
        Assert.Equal(Value(secure: true, name), Value(secure: false, name));
    }

    [Fact]
    public void The_policy_allows_nothing_this_app_does_not_actually_use()
    {
        var policy = Value(secure: false, "Content-Security-Policy");

        // The app has no script at all, so nothing needs relaxing for one; the single
        // style block in the layout is what the one relaxation is for.
        Assert.Contains("default-src 'none'", policy);
        Assert.DoesNotContain("script-src", policy);
        Assert.Contains("style-src 'unsafe-inline'", policy);
    }

    [Fact]
    public void A_page_of_patient_data_may_not_be_framed_or_post_elsewhere()
    {
        var policy = Value(secure: false, "Content-Security-Policy");

        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("form-action 'self'", policy);
    }

    [Fact]
    public void An_app_reached_over_tls_says_so_whatever_the_request_looked_like()
    {
        // The point of taking a bool rather than reading the request: behind a proxy that
        // terminates TLS, the request this app sees is plain HTTP on every hop.
        Assert.StartsWith("max-age=", Value(secure: true, "Strict-Transport-Security"));
    }

    [Fact]
    public void An_app_not_reached_over_tls_does_not_promise_a_browser_it_is()
    {
        // Sent from localhost, this header would strand a reader on a scheme nothing
        // answers on, for a year.
        Assert.DoesNotContain(
            SecurityHeaders.For(secure: false),
            header => header.Key == "Strict-Transport-Security"
        );
    }
}
