using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using DateTimePrecision = Hl7.Fhir.ElementModel.Types.DateTimePrecision;
using FhirAddress = Hl7.Fhir.Model.Address;

namespace SmartOnFhirDemo;

/// <summary>
/// A flat, display-ready projection of a FHIR Patient. Every field is optional in
/// FHIR, so every field here is nullable and the page renders an em dash for null.
/// FHIRPath does the selecting; the typed POCOs do the formatting.
/// </summary>
public sealed record PatientSummary(
    string? Name,
    string? Gender,
    string? BirthDate,
    string? Mrn,
    string? Address,
    string? Phone,
    string? MaritalStatus
)
{
    public static PatientSummary From(Patient patient) =>
        new(
            Name: Format(
                Pick<HumanName>(
                    patient,
                    "Patient.name.where(use='official').first()",
                    "Patient.name.first()"
                )
            ),
            Gender: patient.Gender?.GetDocumentation(),
            BirthDate: FormatBirthDate(patient.BirthDateElement),
            Mrn: Text(patient, "Patient.identifier.where(type.coding.code='MR').value.first()"),
            Address: Format(
                Pick<FhirAddress>(
                    patient,
                    "Patient.address.where(use='home').first()",
                    "Patient.address.first()"
                )
            ),
            Phone: Text(patient, "Patient.telecom.where(system='phone').value.first()"),
            MaritalStatus: Text(patient, "Patient.maritalStatus.text")
                ?? Text(patient, "Patient.maritalStatus.coding.display.first()")
        );

    /// <summary>Label/value pairs in display order, with a placeholder for absent data.</summary>
    public IEnumerable<(string Label, string Value)> Fields =>
        [
            ("Name", Name ?? Absent),
            ("Gender", Gender ?? Absent),
            ("Birth date", BirthDate ?? Absent),
            ("MRN", Mrn ?? Absent),
            ("Address", Address ?? Absent),
            ("Phone", Phone ?? Absent),
            ("Marital status", MaritalStatus ?? Absent),
        ];

    private const string Absent = "—";

    /// <summary>First element matching any expression, in preference order.</summary>
    private static T? Pick<T>(Base root, params string[] expressions)
        where T : Base =>
        expressions
            .Select(e => root.Select(e).OfType<T>().FirstOrDefault())
            .FirstOrDefault(v => v is not null);

    /// <summary>The string value of the first primitive an expression selects.</summary>
    private static string? Text(Base root, string expression) =>
        NullIfBlank(root.Select(expression).OfType<PrimitiveType>().FirstOrDefault()?.ToString());

    private static string? Format(HumanName? name) =>
        name is null
            ? null
            : NullIfBlank(name.Text) ?? Join(" ", (name.Given ?? []).Append(name.Family));

    private static string? Format(FhirAddress? address) =>
        address is null
            ? null
            : NullIfBlank(address.Text)
                ?? Join(
                    ", ",
                    (address.Line ?? []).Concat([address.City, address.State, address.PostalCode])
                );

    /// <summary>
    /// FHIR dates may be partial ("1974", "1974-12"). The SDK completes those to the
    /// first of the year or month, so precision has to come from the value itself —
    /// otherwise the summary shows a day the record never held, and an age derived
    /// from it. A partial date is rendered as written, with no age.
    /// </summary>
    private static string? FormatBirthDate(Date? birthDate)
    {
        if (birthDate is null)
            return null;

        var toTheDay =
            birthDate.TryToSystemDate(out var parsed) && parsed.Precision == DateTimePrecision.Day;

        if (!toTheDay || !birthDate.TryToDateTimeOffset(out var born))
            return NullIfBlank(birthDate.Value);

        var today = DateTimeOffset.UtcNow;
        var age = today.Year - born.Year;
        if (today < born.AddYears(age))
            age--;
        return $"{born:yyyy-MM-dd} ({age} yrs)";
    }

    private static string? Join(string separator, IEnumerable<string?> parts) =>
        NullIfBlank(string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p))));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
