using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Application = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AiProvider = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiModel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: false),
                    CompletionTokens = table.Column<int>(type: "int", nullable: false),
                    PagesGenerated = table.Column<int>(type: "int", nullable: false),
                    KnowledgeNodeCount = table.Column<int>(type: "int", nullable: false),
                    RelationshipCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Application = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RepositoryName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Branch = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceKind = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CommitSha = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MaterializedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryRuns_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRuns_Application",
                table: "RepositoryRuns",
                column: "Application");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRuns_Location",
                table: "RepositoryRuns",
                column: "Location");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRuns_RunId",
                table: "RepositoryRuns",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Application",
                table: "Runs",
                column: "Application");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryRuns");

            migrationBuilder.DropTable(
                name: "Runs");
        }
    }
}
