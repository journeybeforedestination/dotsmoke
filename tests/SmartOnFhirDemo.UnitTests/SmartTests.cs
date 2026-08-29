using System.Security.Cryptography;
using System.Text;
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
