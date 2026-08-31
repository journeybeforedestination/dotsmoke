namespace SmartOnFhirDemo.UnitTests;

/// <summary>A clock that does not move, so "expired" means the same on every machine and day.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
