using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boh.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TagNamespaceAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TagNamespaceAliases",
                columns: table => new
                {
                    Alias = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Canonical = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagNamespaceAliases", x => x.Alias);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TagNamespaceAliases_Canonical",
                table: "TagNamespaceAliases",
                column: "Canonical");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TagNamespaceAliases");
        }
    }
}
