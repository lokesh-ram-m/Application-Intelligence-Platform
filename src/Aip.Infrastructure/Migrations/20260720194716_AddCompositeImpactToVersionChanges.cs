using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeImpactToVersionChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddedIntegrationNamesJson",
                table: "DocumentVersionChanges",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "PerApplicationImpactJson",
                table: "DocumentVersionChanges",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "RemovedIntegrationNamesJson",
                table: "DocumentVersionChanges",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedIntegrationNamesJson",
                table: "DocumentVersionChanges");

            migrationBuilder.DropColumn(
                name: "PerApplicationImpactJson",
                table: "DocumentVersionChanges");

            migrationBuilder.DropColumn(
                name: "RemovedIntegrationNamesJson",
                table: "DocumentVersionChanges");
        }
    }
}
