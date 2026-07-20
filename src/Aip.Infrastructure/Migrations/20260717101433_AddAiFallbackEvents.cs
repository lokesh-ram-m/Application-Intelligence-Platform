using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aip.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiFallbackEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiFallbackEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Application = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Repositories = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFallbackEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiFallbackEvents_Application",
                table: "AiFallbackEvents",
                column: "Application");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiFallbackEvents");
        }
    }
}
