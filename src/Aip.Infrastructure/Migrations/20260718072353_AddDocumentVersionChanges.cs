using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentVersionChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentVersionChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Application = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    PreviousVersionNumber = table.Column<int>(type: "int", nullable: false),
                    NodesAdded = table.Column<int>(type: "int", nullable: false),
                    NodesRemoved = table.Column<int>(type: "int", nullable: false),
                    RelationshipsAdded = table.Column<int>(type: "int", nullable: false),
                    RelationshipsRemoved = table.Column<int>(type: "int", nullable: false),
                    AddedNodeNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RemovedNodeNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AddedRelationshipNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RemovedRelationshipNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RepositoryCommitsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AiWritten = table.Column<bool>(type: "bit", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentVersionChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersionChanges_Application_VersionNumber",
                table: "DocumentVersionChanges",
                columns: new[] { "Application", "VersionNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentVersionChanges");
        }
    }
}
