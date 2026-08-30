using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace SmartOnFhirDemo.UnitTests;

public class SmartTests
{
    private const string Iss = "https://ehr.example/r4/fhir";

    // ---- PKCE (RFC 7636) --------------------------------------------------

    [Fact]
    public void Pkce_challenge_is_the_S256_hash_of_the_verifier()
    {
        var (verifier, challenge) = Smart.NewPkce();

        var expected = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void Pkce_verifier_is_url_safe_and_within_the_length_the_rfc_allows()
    {
        var (verifier, challenge) = Smart.NewPkce();

        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
        Assert.DoesNotContain('=', challenge);
    }

    [Fact]
    public void Pkce_produces_a_fresh_verifier_every_time()
    {
        var verifiers = Enumerable.Range(0, 50).Select(_ => Smart.NewPkce().Verifier).ToList();

        Assert.Equal(verifiers.Count, verifiers.Distinct().Count());
    }

    // ---- State ------------------------------------------------------------

    [Fact]
    public void State_is_url_safe_and_unpredictable()
    {
        var states = Enumerable.Range(0, 50).Select(_ => Smart.NewState()).ToList();

        Assert.Equal(states.Count, states.Distinct().Count());
        Assert.All(states, s =>
        {
            Assert.DoesNotContain('+', s);
            Assert.DoesNotContain('/', s);
            Assert.DoesNotContain('=', s);
        });
    }

    [Fact]
    public void CacheKey_is_namespaced_by_state()
    {
        Assert.Equal("launch:abc", Smart.CacheKey("abc"));
        Assert.NotEqual(Smart.CacheKey("a"), Smart.CacheKey("b"));
    }

    // ---- Redaction --------------------------------------------------------
    //
    // The narrated launch renders the token response, so this is what stands between a
    // reader and a live bearer credential. It is the reason SmartLaunch can hand the
    // response body onwards at all.

    [Fact]
    public void Redact_replaces_the_named_values_and_leaves_the_rest()
    {
        var redacted = Smart.Redact(
            """{"access_token":"the-token","token_type":"Bearer","expires_in":3600,"patient":"pat-1"}""",
            "access_token");

        Assert.DoesNotContain("the-token", redacted);
        Assert.Contains(Smart.Withheld, redacted);
        Assert.Contains("Bearer", redacted);
        Assert.Contains("3600", redacted);
        Assert.Contains("pat-1", redacted);
    }

    [Fact]
    public void Redact_removes_every_credential_the_token_endpoint_can_return()
    {
        var redacted = Smart.Redact(
            """{"access_token":"a","refresh_token":"r","id_token":"i","scope":"launch"}""",
            "access_token", "refresh_token", "id_token");

        Assert.Equal(3, Regex.Matches(redacted, Regex.Escape(Smart.Withheld)).Count);
        Assert.Contains("launch", redacted);
    }

    [Fact]
    public void Redact_is_a_no_op_for_a_key_that_is_not_there()
    {
        var redacted = Smart.Redact("""{"scope":"launch"}""", "refresh_token");

        Assert.DoesNotContain(Smart.Withheld, redacted);
        Assert.Contains("launch", redacted);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("""["access_token","the-token"]""")]
    public void Redact_discards_anything_it_cannot_take_apart(string body)
    {
        // Passing the body through unchanged would defeat the point: the reason to call
        // this is that the document is believed to hold a credential.
        Assert.Equal(Smart.Withheld, Smart.Redact(body, "access_token"));
    }

    // ---- Authorize URL ----------------------------------------------------

    private static Dictionary<string, string> AuthorizeQuery(
        string authorizationEndpoint = "https://ehr.example/authorize",
        string scopes = "launch patient/Patient.read")
    {
        var url = Smart.BuildAuthorizeUrl(
            new SmartConfiguration(authorizationEndpoint, "https://ehr.example/token"),
            new SmartOptions { ClientId = "smart-on-fhir-demo", Scopes = scopes },
            redirectUri: "http://localhost:5000/callback",
            iss: Iss,
            launch: "launch-123",
            state: "state-456",
            codeChallenge: "challenge-789");

        return QueryHelpers.ParseQuery(new Uri(url).Query)
            .ToDictionary(p => p.Key, p => p.Value.ToString());
    }

    [Fact]
    public void AuthorizeUrl_carries_every_parameter_the_smart_launch_requires()
    {
        var query = AuthorizeQuery();

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("smart-on-fhir-demo", query["client_id"]);
        Assert.Equal("http://localhost:5000/callback", query["redirect_uri"]);
        Assert.Equal("launch-123", query["launch"]);
        Assert.Equal("launch patient/Patient.read", query["scope"]);
        Assert.Equal("state-456", query["state"]);
        Assert.Equal("challenge-789", query["code_challenge"]);
    }

    [Fact]
    public void AuthorizeUrl_always_requests_S256_never_plain()
    {
        Assert.Equal("S256", AuthorizeQuery()["code_challenge_method"]);
    }

    [Fact]
    public void AuthorizeUrl_sets_aud_to_the_issuer_so_the_token_cannot_be_replayed_elsewhere()
    {
        Assert.Equal(Iss, AuthorizeQuery()["aud"]);
    }

    [Fact]
    public void AuthorizeUrl_preserves_a_query_string_already_on_the_authorize_endpoint()
    {
        var query = AuthorizeQuery("https://ehr.example/authorize?tenant=acme");

        Assert.Equal("acme", query["tenant"]);
        Assert.Equal("code", query["response_type"]);
    }

    [Fact]
    public void AuthorizeUrl_escapes_values_that_need_it()
    {
        var url = Smart.BuildAuthorizeUrl(
            new SmartConfiguration("https://ehr.example/authorize", "https://ehr.example/token"),
            new SmartOptions { ClientId = "id", Scopes = "launch patient/Patient.read" },
            redirectUri: "http://localhost:5000/callback",
            iss: Iss, launch: "l", state: "s", codeChallenge: "c");

        Assert.DoesNotContain("scope=launch patient", url);
        Assert.Equal("launch patient/Patient.read", AuthorizeQuery()["scope"]);
    }
}
