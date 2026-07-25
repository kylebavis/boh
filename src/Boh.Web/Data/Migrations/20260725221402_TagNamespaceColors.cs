using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boh.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TagNamespaceColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TagNamespaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagNamespaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TagNamespaces_Name",
                table: "TagNamespaces",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TagNamespaces");
        }
    }
}
