using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartOnFhirDemo.Migrations
{
    /// <inheritdoc />
    public partial class InitialAccessLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessLog",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IssuerOrigin = table.Column<string>(type: "TEXT", nullable: false),
                    PatientId = table.Column<string>(type: "TEXT", nullable: true),
                    FhirUser = table.Column<string>(type: "TEXT", nullable: true),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    RequestPath = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessLog", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AccessLog_IssuerOrigin_PatientId_OccurredAt",
                table: "AccessLog",
                columns: new[] { "IssuerOrigin", "PatientId", "OccurredAt" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AccessLog");
        }
    }
}
