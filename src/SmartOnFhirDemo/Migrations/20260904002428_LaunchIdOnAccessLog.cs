using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartOnFhirDemo.Migrations
{
    /// <inheritdoc />
    public partial class LaunchIdOnAccessLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LaunchId",
                table: "AccessLog",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.CreateIndex(
                name: "IX_AccessLog_LaunchId_Id",
                table: "AccessLog",
                columns: new[] { "LaunchId", "Id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_AccessLog_LaunchId_Id", table: "AccessLog");

            migrationBuilder.DropColumn(name: "LaunchId", table: "AccessLog");
        }
    }
}
