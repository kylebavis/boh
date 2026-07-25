using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boh.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PostCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TagAliases",
                columns: table => new
                {
                    AliasTagId = table.Column<int>(type: "INTEGER", nullable: false),
                    CanonicalTagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagAliases", x => x.AliasTagId);
                    table.ForeignKey(
                        name: "FK_TagAliases_Tags_AliasTagId",
                        column: x => x.AliasTagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagAliases_Tags_CanonicalTagId",
                        column: x => x.CanonicalTagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagImplications",
                columns: table => new
                {
                    ChildTagId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentTagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagImplications", x => new { x.ChildTagId, x.ParentTagId });
                    table.ForeignKey(
                        name: "FK_TagImplications_Tags_ChildTagId",
                        column: x => x.ChildTagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagImplications_Tags_ParentTagId",
                        column: x => x.ParentTagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileExtension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSec = table.Column<double>(type: "REAL", nullable: true),
                    IsVideo = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    UploadedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedById = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Posts_Users_UploadedById",
                        column: x => x.UploadedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PostTags",
                columns: table => new
                {
                    PostId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostTags", x => new { x.PostId, x.TagId });
                    table.ForeignKey(
                        name: "FK_PostTags_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PostTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostTags_TagId",
                table: "PostTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Sha256",
                table: "Posts",
                column: "Sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_UploadedAt",
                table: "Posts",
                column: "UploadedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_UploadedById",
                table: "Posts",
                column: "UploadedById");

            migrationBuilder.CreateIndex(
                name: "IX_TagAliases_CanonicalTagId",
                table: "TagAliases",
                column: "CanonicalTagId");

            migrationBuilder.CreateIndex(
                name: "IX_TagImplications_ParentTagId",
                table: "TagImplications",
                column: "ParentTagId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Namespace_Name",
                table: "Tags",
                columns: new[] { "Namespace", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_PostCount",
                table: "Tags",
                column: "PostCount",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostTags");

            migrationBuilder.DropTable(
                name: "TagAliases");

            migrationBuilder.DropTable(
                name: "TagImplications");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
