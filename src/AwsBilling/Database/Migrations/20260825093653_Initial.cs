using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwsBilling.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    collected_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    period_start = table.Column<string>(type: "TEXT", nullable: false),
                    period_end = table.Column<string>(type: "TEXT", nullable: false),
                    service_names = table.Column<string>(type: "TEXT", nullable: false),
                    page_count = table.Column<int>(type: "INTEGER", nullable: false),
                    byte_size = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_contents",
                columns: table => new
                {
                    report_id = table.Column<long>(type: "INTEGER", nullable: false),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_contents", x => x.report_id);
                    table.ForeignKey(
                        name: "FK_report_contents_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_contents");

            migrationBuilder.DropTable(
                name: "reports");
        }
    }
}
