using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// An access log on a real SQLite database that never reaches a disk. The connection is
/// held open for the fixture's lifetime because an in-memory database lives exactly as
/// long as the last connection to it does — close it and the schema goes with it.
///
/// The schema comes from the migration rather than from <c>EnsureCreated</c>, so what
/// these tests run against is what a deployment would get.
/// </summary>
internal sealed class AccessLogFixture : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public AccessLogContext Db { get; }

    public AccessLog Log { get; }

    public AccessLogFixture()
    {
        _connection.Open();

        Db = new AccessLogContext(
            new DbContextOptionsBuilder<AccessLogContext>().UseSqlite(_connection).Options
        );
        Db.Database.Migrate();

        Log = new AccessLog(Db);
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
