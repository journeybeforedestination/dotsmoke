namespace SmartOnFhirDemo;

/// <summary>
/// What the app renders once a launch has happened: the patient, the panels its token
/// lets it read, and the resource behind them. Kept apart from the page that renders it
/// so the walkthrough's steps stay a narration of this, rather than the shape of it.
/// </summary>
public sealed record AppView(PatientSummary Summary, ChartView Chart, string RawJson);
