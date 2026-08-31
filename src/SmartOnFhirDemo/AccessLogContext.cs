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

        // The question an access log is actually asked is "who has been in this chart,
        // lately", so that is the order it is indexed in.
        entries.HasIndex(entry => new
        {
            entry.IssuerOrigin,
            entry.PatientId,
            entry.OccurredAt,
        });
    }
}
