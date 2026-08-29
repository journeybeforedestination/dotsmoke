using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// Fixtures are FHIR JSON parsed by Firely, so tests exercise the same
/// deserialization path the app uses rather than hand-built POCOs.
/// </summary>
internal static class Fixture
{
    private static readonly FhirJsonDeserializer Deserializer = new();

    public static Patient Patient(string json) => Deserializer.Deserialize<Patient>(json);
}
