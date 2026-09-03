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

        // Everything is denied and then three things are named back: the style block in
        // the layout, the one script file, and the fetch that script makes for a pane.
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("style-src 'unsafe-inline'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("connect-src 'self'", policy);
    }

    [Fact]
    public void No_script_may_be_written_into_a_page_of_patient_data()
    {
        var policy = Value(secure: false, "Content-Security-Policy");

        // The script is a file, so 'self' is the whole of what it needs. These two are
        // what an injected <script> or an onclick= would need instead, and the difference
        // between allowing this app's own script and allowing anybody's is exactly here.
        Assert.DoesNotContain("script-src 'unsafe-inline'", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
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
