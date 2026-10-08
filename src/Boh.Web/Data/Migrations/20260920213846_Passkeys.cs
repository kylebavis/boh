using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boh.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Passkeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Passkeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SignCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    Transports = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsUserVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBackupEligible = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBackedUp = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttestationObject = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ClientDataJson = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Passkeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Passkeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_CredentialId",
                table: "Passkeys",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_UserId",
                table: "Passkeys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Passkeys");
        }
    }
}
