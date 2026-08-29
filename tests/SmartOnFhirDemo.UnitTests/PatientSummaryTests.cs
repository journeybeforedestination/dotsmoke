namespace SmartOnFhirDemo.UnitTests;

public class PatientSummaryTests
{
    private static PatientSummary Summarize(string json) =>
        PatientSummary.From(Fixture.Patient(json));

    // ---- Name -------------------------------------------------------------

    [Fact]
    public void Name_prefers_the_official_use_over_others()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","name":[
              {"use":"nickname","given":["Bill"],"family":"Preston"},
              {"use":"official","given":["William"],"family":"Preston"}
            ]}
            """);

        Assert.Equal("William Preston", summary.Name);
    }

    [Fact]
    public void Name_falls_back_to_the_first_entry_when_none_is_official()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","name":[
              {"use":"nickname","given":["Bill"],"family":"Preston"},
              {"use":"maiden","given":["William"],"family":"Logan"}
            ]}
            """);

        Assert.Equal("Bill Preston", summary.Name);
    }

    [Fact]
    public void Name_uses_text_in_preference_to_the_parts()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","name":[
              {"use":"official","text":"Dr. William S. Preston Esq.","given":["William"],"family":"Preston"}
            ]}
            """);

        Assert.Equal("Dr. William S. Preston Esq.", summary.Name);
    }

    [Fact]
    public void Name_joins_every_given_name_before_the_family_name()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","name":[{"given":["Theodore","Logan"],"family":"Jr"}]}
            """);

        Assert.Equal("Theodore Logan Jr", summary.Name);
    }

    // ---- Gender -----------------------------------------------------------

    [Theory]
    [InlineData("male", "Male")]
    [InlineData("female", "Female")]
    [InlineData("other", "Other")]
    [InlineData("unknown", "Unknown")]
    public void Gender_renders_the_display_text_for_the_code(string code, string expected)
    {
        var summary = Summarize($$"""{"resourceType":"Patient","gender":"{{code}}"}""");

        Assert.Equal(expected, summary.Gender);
    }

    // ---- Birth date -------------------------------------------------------

    [Theory]
    [InlineData("1974")]
    [InlineData("1974-12")]
    public void BirthDate_keeps_a_partial_date_as_written_and_shows_no_age(string partial)
    {
        var summary = Summarize($$"""{"resourceType":"Patient","birthDate":"{{partial}}"}""");

        // The SDK would happily complete these to 1974-01-01 / 1974-12-01. Showing
        // that day, or an age derived from it, would invent precision the record
        // does not have.
        Assert.Equal(partial, summary.BirthDate);
        Assert.DoesNotContain("yrs", summary.BirthDate);
    }

    [Fact]
    public void BirthDate_shows_the_day_and_an_age_once_the_day_is_known()
    {
        var summary = Summarize("""{"resourceType":"Patient","birthDate":"1974-12-25"}""");

        // Asserted as shape, not a fixed number: the age is computed against the
        // current clock, so any specific value would rot.
        Assert.StartsWith("1974-12-25 (", summary.BirthDate);
        Assert.EndsWith(" yrs)", summary.BirthDate);
    }

    [Fact]
    public void BirthDate_is_absent_when_the_patient_has_none()
    {
        var summary = Summarize("""{"resourceType":"Patient","id":"no-data"}""");

        Assert.Null(summary.BirthDate);
    }

    // ---- Identifiers ------------------------------------------------------

    [Fact]
    public void Mrn_selects_the_identifier_typed_MR_and_ignores_the_rest()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","identifier":[
              {"type":{"coding":[{"code":"SS"}]},"value":"999-99-9999"},
              {"type":{"coding":[{"code":"MR"}]},"value":"MRN-12345"},
              {"type":{"coding":[{"code":"DL"}]},"value":"D-987"}
            ]}
            """);

        Assert.Equal("MRN-12345", summary.Mrn);
    }

    [Fact]
    public void Mrn_is_absent_when_no_identifier_is_an_MR()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","identifier":[{"type":{"coding":[{"code":"SS"}]},"value":"999-99-9999"}]}
            """);

        Assert.Null(summary.Mrn);
    }

    // ---- Address ----------------------------------------------------------

    [Fact]
    public void Address_prefers_the_home_use_over_others()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","address":[
              {"use":"work","line":["1 Work Way"],"city":"San Dimas","state":"CA","postalCode":"91773"},
              {"use":"home","line":["42 Home Road"],"city":"San Dimas","state":"CA","postalCode":"91773"}
            ]}
            """);

        Assert.Equal("42 Home Road, San Dimas, CA, 91773", summary.Address);
    }

    [Fact]
    public void Address_uses_text_in_preference_to_the_parts()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","address":[
              {"use":"home","text":"42 Home Road, San Dimas CA","line":["42 Home Road"],"city":"San Dimas"}
            ]}
            """);

        Assert.Equal("42 Home Road, San Dimas CA", summary.Address);
    }

    [Fact]
    public void Address_joins_multiple_lines_and_skips_missing_parts()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","address":[{"line":["Flat 2","42 Home Road"],"state":"CA"}]}
            """);

        Assert.Equal("Flat 2, 42 Home Road, CA", summary.Address);
    }

    // ---- Telecom ----------------------------------------------------------

    [Fact]
    public void Phone_selects_the_phone_system_and_ignores_other_contact_points()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","telecom":[
              {"system":"email","value":"bill@wyldstallyns.example"},
              {"system":"phone","value":"555-0100"}
            ]}
            """);

        Assert.Equal("555-0100", summary.Phone);
    }

    // ---- Marital status ---------------------------------------------------

    [Fact]
    public void MaritalStatus_prefers_text_over_the_coding_display()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","maritalStatus":{"text":"Married","coding":[{"display":"M"}]}}
            """);

        Assert.Equal("Married", summary.MaritalStatus);
    }

    [Fact]
    public void MaritalStatus_falls_back_to_the_coding_display_when_there_is_no_text()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","maritalStatus":{"coding":[{"display":"Never Married"}]}}
            """);

        Assert.Equal("Never Married", summary.MaritalStatus);
    }

    // ---- Fields projection ------------------------------------------------

    [Fact]
    public void Fields_renders_every_label_in_order()
    {
        var summary = Summarize("""{"resourceType":"Patient","id":"no-data"}""");

        Assert.Equal(
            ["Name", "Gender", "Birth date", "MRN", "Address", "Phone", "Marital status"],
            summary.Fields.Select(f => f.Label));
    }

    [Fact]
    public void Fields_renders_an_em_dash_for_every_absent_value()
    {
        var summary = Summarize("""{"resourceType":"Patient","id":"no-data"}""");

        Assert.All(summary.Fields, field => Assert.Equal("—", field.Value));
    }

    [Fact]
    public void Fields_carries_the_values_through_when_they_are_present()
    {
        var summary = Summarize("""
            {"resourceType":"Patient","gender":"female","name":[{"given":["Joanna"],"family":"Preston"}]}
            """);

        Assert.Contains(("Name", "Joanna Preston"), summary.Fields);
        Assert.Contains(("Gender", "Female"), summary.Fields);
        Assert.Contains(("Phone", "—"), summary.Fields);
    }
}
