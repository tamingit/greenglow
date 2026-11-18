using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenGlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignInMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Disabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Users",
                type: "TEXT",
                nullable: false,
                // Everyone already in the table signed in with Entra ID (or stood in for it).
                defaultValue: "Entra");

            migrationBuilder.AddColumn<bool>(
                name: "UsePortMapper",
                table: "Connections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "IbmLogins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IbmUser = table.Column<string>(type: "TEXT", nullable: false),
                    SealedPassword = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IbmLogins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IbmLogins_Connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IbmLogins_ConnectionId",
                table: "IbmLogins",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_IbmLogins_UserId_ConnectionId_IbmUser",
                table: "IbmLogins",
                columns: new[] { "UserId", "ConnectionId", "IbmUser" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IbmLogins");

            migrationBuilder.DropColumn(
                name: "Disabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "UsePortMapper",
                table: "Connections");
        }
    }
}
