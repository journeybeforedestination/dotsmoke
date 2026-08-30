using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The projection of whoever <c>fhirUser</c> named. It reads off the base resource rather
/// than a Practitioner, so these tests use more than one resource type on purpose.
/// </summary>
public class LaunchUserTests
{
    private static readonly FhirJsonDeserializer Deserializer = new();

    private static LaunchUser Summarize(string json) =>
        LaunchUser.From(Deserializer.Deserialize<Resource>(json));

    [Fact]
    public void The_prefix_is_kept_so_a_clinician_reads_as_a_doctor()
    {
        var user = Summarize(
            """
            {"resourceType":"Practitioner","id":"p1",
             "name":[{"family":"Orn","given":["Albertine"],"prefix":["Dr."]}]}
            """
        );

        Assert.Equal("Dr. Albertine Orn", user.Name);
    }

    [Fact]
    public void The_official_name_is_preferred_over_the_others()
    {
        var user = Summarize(
            """
            {"resourceType":"Practitioner","id":"p1","name":[
              {"use":"nickname","text":"Bertie"},
              {"use":"official","family":"Orn","given":["Albertine"]}]}
            """
        );

        Assert.Equal("Albertine Orn", user.Name);
    }

    [Fact]
    public void The_npi_identifier_is_selected_from_among_the_others()
    {
        var user = Summarize(
            """
            {"resourceType":"Practitioner","id":"p1","identifier":[
              {"system":"http://example.org/staff-id","value":"XYZ"},
              {"system":"http://hl7.org/fhir/sid/us-npi","value":"93370"}]}
            """
        );

        Assert.Equal("93370", user.Identifier);
    }

    [Fact]
    public void An_identifier_that_is_not_an_npi_is_still_better_than_none()
    {
        var user = Summarize(
            """
            {"resourceType":"Practitioner","id":"p1",
             "identifier":[{"system":"http://example.org/staff-id","value":"XYZ"}]}
            """
        );

        Assert.Equal("XYZ", user.Identifier);
    }

    [Fact]
    public void A_resource_type_other_than_practitioner_is_projected_just_the_same()
    {
        // fhirUser may name a Patient, RelatedPerson or Person, which is the whole reason
        // this reads off the base resource rather than a Practitioner.
        var user = Summarize(
            """
            {"resourceType":"RelatedPerson","id":"r1","patient":{"reference":"Patient/pat-1"},
             "name":[{"family":"Diaz","given":["Marta"]}]}
            """
        );

        Assert.Equal("RelatedPerson", user.ResourceType);
        Assert.Equal("Marta Diaz", user.Name);
    }

    [Fact]
    public void A_resource_carrying_no_name_at_all_is_still_projected()
    {
        var user = Summarize("""{"resourceType":"Practitioner","id":"p1"}""");

        Assert.Null(user.Name);
        Assert.Equal("Practitioner", user.ResourceType);
    }

    [Fact]
    public void Fields_renders_an_em_dash_for_every_absent_value()
    {
        var fields = Summarize("""{"resourceType":"Person","id":"x"}""").Fields.ToDictionary();

        Assert.Equal("—", fields["Name"]);
        Assert.Equal("—", fields["Identifier"]);
        Assert.Equal("Person", fields["Resource"]);
    }
}
