using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boh.Web.Data.Migrations
{
    /// <summary>
    /// Moves a post's origin URL out of a single column and into a child table, so the same
    /// file found at several addresses keeps all of them.
    /// </summary>
    /// <remarks>
    /// The table is created and filled before the column is dropped, which is the whole point
    /// of hand-ordering these operations: the scaffolded version dropped first and would have
    /// thrown away every existing source.
    /// </remarks>
    public partial class PostSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostSources_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostSources_PostId_Url",
                table: "PostSources",
                columns: new[] { "PostId", "Url" },
                unique: true);

            // One row per post that had a source. Ordering by post id gives the new rows ids
            // in the same order as the posts, so the display order is stable from the start.
            migrationBuilder.Sql(
                """
                INSERT INTO PostSources (PostId, Url)
                SELECT Id, TRIM(SourceUrl) FROM Posts
                WHERE SourceUrl IS NOT NULL AND TRIM(SourceUrl) <> ''
                ORDER BY Id
                """);

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "Posts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "Posts",
                type: "TEXT",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            // A single column cannot hold what the table could, so this keeps the first source
            // recorded for each post and drops the rest. Going back loses information; there is
            // nowhere else for it to go.
            migrationBuilder.Sql(
                """
                UPDATE Posts SET SourceUrl = COALESCE(
                    (SELECT Url FROM PostSources WHERE PostId = Posts.Id ORDER BY Id LIMIT 1), '')
                """);

            migrationBuilder.DropTable(
                name: "PostSources");
        }
    }
}
