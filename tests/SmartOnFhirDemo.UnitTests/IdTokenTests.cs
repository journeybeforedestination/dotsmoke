using Microsoft.IdentityModel.Tokens;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The rules that decide whether an id_token is believed. Every one of them is a pure
/// function of the token, the published keys and the clock, so none of these needs a
/// network, a launcher, or a wait.
/// </summary>
public class IdTokenTests
{
    private static Task<IdTokenOutcome> Validate(
        string token,
        IList<SecurityKey>? keys = null,
        string issuer = TestIdTokens.Issuer,
        string audience = TestIdTokens.Audience
    ) =>
        IdToken.ValidateAsync(
            token,
            keys ?? TestIdTokens.Published(TestIdTokens.Ehr),
            issuer,
            audience,
            TestIdTokens.Clock
        );

    [Fact]
    public async Task A_token_signed_by_the_issuers_key_is_accepted()
    {
        var outcome = await Validate(TestIdTokens.Token());

        var facts = Assert.IsType<IdTokenOutcome.Valid>(outcome).Facts;
        Assert.Equal(TestIdTokens.Issuer, facts.Issuer);
        Assert.Equal(TestIdTokens.Audience, facts.Audience);
        Assert.Equal(TestIdTokens.FhirUser, facts.FhirUser);
        Assert.Equal("user-1", facts.Subject);
    }

    [Fact]
    public async Task A_token_signed_by_another_key_is_refused()
    {
        var outcome = await Validate(TestIdTokens.Token(signedWith: TestIdTokens.Impostor));

        Assert.IsType<IdTokenOutcome.Invalid>(outcome);
    }

    [Fact]
    public async Task A_token_from_a_different_issuer_is_refused()
    {
        var outcome = await Validate(TestIdTokens.Token(issuer: "https://elsewhere.example/fhir"));

        Assert.Contains("issuer", Assert.IsType<IdTokenOutcome.Invalid>(outcome).Reason);
    }

    [Fact]
    public async Task A_token_for_a_different_audience_is_refused()
    {
        var outcome = await Validate(TestIdTokens.Token(audience: "some-other-app"));

        Assert.Contains("different app", Assert.IsType<IdTokenOutcome.Invalid>(outcome).Reason);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var outcome = await Validate(TestIdTokens.Token(expires: TestIdTokens.Now.AddMinutes(-1)));

        Assert.Contains("expired", Assert.IsType<IdTokenOutcome.Invalid>(outcome).Reason);
    }

    [Fact]
    public async Task A_token_with_no_kid_is_still_matched_against_the_published_keys()
    {
        // The launcher's id_tokens carry no 'kid', so the only way to verify one is to try
        // every key the EHR publishes. Here the usable key is second, behind one that fails.
        var outcome = await Validate(
            TestIdTokens.Token(),
            TestIdTokens.Published(TestIdTokens.Impostor, TestIdTokens.Ehr)
        );

        Assert.IsType<IdTokenOutcome.Valid>(outcome);
    }

    [Fact]
    public async Task A_token_carrying_no_fhirUser_is_still_valid_but_names_nobody()
    {
        var outcome = await Validate(TestIdTokens.Token(fhirUser: null));

        Assert.Null(Assert.IsType<IdTokenOutcome.Valid>(outcome).Facts.FhirUser);
    }

    [Fact]
    public async Task The_raw_token_reaches_no_field_of_the_result()
    {
        var token = TestIdTokens.Token();

        var facts = Assert.IsType<IdTokenOutcome.Valid>(await Validate(token)).Facts;

        // The id_token is a credential. Like the access token, it must not survive the
        // projection that the pages are handed.
        foreach (var property in facts.GetType().GetProperties())
            Assert.DoesNotContain(token, property.GetValue(facts)?.ToString() ?? "");
    }
}
