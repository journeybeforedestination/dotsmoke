using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;

namespace SmartOnFhirDemo;

/// <summary>
/// Who started the launch, projected from whatever resource <c>fhirUser</c> pointed at.
///
/// Deliberately not a Practitioner-specific record. SMART allows fhirUser to name a
/// Practitioner, Patient, RelatedPerson or Person, and all four carry <c>name</c> and
/// <c>identifier</c> — so selecting with FHIRPath off the base resource handles every
/// one of them in less code than handling the first would take on its own.
/// </summary>
public sealed record LaunchUser(string ResourceType, string? Name, string? Identifier)
{
    private const string Npi = "http://hl7.org/fhir/sid/us-npi";

    public static LaunchUser From(Resource resource) =>
        new(
            resource.TypeName,
            Format(Pick(resource, "name.where(use='official').first()", "name.first()")),
            // The national provider id in preference to whatever else a directory has
            // hung on the record, the same way PatientSummary prefers an MR.
            Text(resource, $"identifier.where(system='{Npi}').value.first()")
                ?? Text(resource, "identifier.value.first()")
        );

    /// <summary>Label/value pairs in display order, with a placeholder for absent data.</summary>
    public IEnumerable<(string Label, string Value)> Fields =>
        [
            ("Name", Name ?? Absent),
            ("Resource", ResourceType),
            ("Identifier", Identifier ?? Absent),
        ];

    private const string Absent = "—";

    private static HumanName? Pick(Base root, params string[] expressions) =>
        expressions
            .Select(e => root.Select(e).OfType<HumanName>().FirstOrDefault())
            .FirstOrDefault(v => v is not null);

    private static string? Text(Base root, string expression) =>
        NullIfBlank(root.Select(expression).OfType<PrimitiveType>().FirstOrDefault()?.ToString());

    /// <summary>
    /// Unlike a patient's name, a clinician's prefix is most of how they are addressed —
    /// "Dr. Albertine Orn", not "Albertine Orn" — so it is kept here.
    /// </summary>
    private static string? Format(HumanName? name) =>
        name is null
            ? null
            : NullIfBlank(name.Text)
                ?? Join(" ", (name.Prefix ?? []).Concat(name.Given ?? []).Append(name.Family));

    private static string? Join(string separator, IEnumerable<string?> parts) =>
        NullIfBlank(string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p))));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
