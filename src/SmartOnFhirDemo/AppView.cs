namespace SmartOnFhirDemo;

/// <summary>
/// What the app renders once a launch has happened: the patient, the panels its token
/// lets it read, and the resource behind them. The walkthrough's last stop and where a
/// plain launch lands are handed this same view, because they are the same app — a claim
/// two page-shaped copies of that markup would quietly stop honouring.
/// </summary>
public sealed record AppView(PatientSummary Summary, ChartView Chart, string RawJson);
