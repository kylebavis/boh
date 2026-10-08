using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boh.Web.Data.Migrations
{
    /// <summary>
    /// Adds the perceptual hash that makes a resized or re-encoded repost recognizable, which
    /// the SHA-256 alone cannot see.
    /// </summary>
    /// <remarks>
    /// Existing posts arrive with no hash and <c>PerceptualHashTried</c> false, so the
    /// backfill under Maintenance finds them. Nothing is computed here: hashing means decoding
    /// every original in the archive, which is not something a migration should do while the
    /// application waits to start.
    /// </remarks>
    public partial class PerceptualHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PerceptualHash",
                table: "Posts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PerceptualHashTried",
                table: "Posts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_PerceptualHash",
                table: "Posts",
                column: "PerceptualHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_PerceptualHash",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PerceptualHash",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PerceptualHashTried",
                table: "Posts");
        }
    }
}
