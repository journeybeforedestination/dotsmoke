using Microsoft.EntityFrameworkCore;

namespace SmartOnFhirDemo;

/// <summary>
/// The access log's database: one table, no navigation properties, nothing to join to.
/// The schema is thin on purpose — the question it answers is narrow, and a wider one
/// would be a schema invented to justify a database rather than to answer anything.
/// </summary>
public sealed class AccessLogContext(DbContextOptions<AccessLogContext> options)
    : DbContext(options)
{
    public DbSet<AccessLogEntry> Entries => Set<AccessLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entries = modelBuilder.Entity<AccessLogEntry>();

        // Named for what it holds rather than for the DbSet, because the audience for a
        // table name is whoever opens app.db with sqlite3.
        entries.ToTable("AccessLog");
        entries.HasKey(entry => entry.Id);

        // "Who has been in this chart, lately" — the question an access log is classically
        // asked, and one this app deliberately answers to nobody: it needs an audience that
        // is not "whoever just launched". Kept for whoever opens app.db with sqlite3.
        entries.HasIndex(entry => new
        {
            entry.IssuerOrigin,
            entry.PatientId,
            entry.OccurredAt,
        });

        // What the app itself asks: the rows one launch caused, newest first. Ordered by the
        // id rather than by the time because SQLite cannot ORDER BY a DateTimeOffset at all
        // — and the id is better anyway: rows within a launch share a timestamp routinely,
        // and insertion order is the order the requests actually left.
        entries.HasIndex(entry => new { entry.LaunchId, entry.Id });
    }
}
