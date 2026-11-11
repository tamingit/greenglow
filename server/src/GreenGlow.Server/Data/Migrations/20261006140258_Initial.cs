using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenGlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Secure = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxSessionsPerUser = table.Column<int>(type: "INTEGER", nullable: false),
                    IsTest = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    TrustedCaPem = table.Column<string>(type: "TEXT", nullable: true),
                    TrustedName = table.Column<string>(type: "TEXT", nullable: true),
                    TrustedBy = table.Column<string>(type: "TEXT", nullable: true),
                    TrustedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalState",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalState", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "LogChanges",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogChanges", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "Logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Node = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Upn = table.Column<string>(type: "TEXT", nullable: true),
                    ClientIp = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    IbmUser = table.Column<string>(type: "TEXT", nullable: true),
                    FilesOpened = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Macros",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUpn = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Macros", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerCursors",
                columns: table => new
                {
                    Node = table.Column<int>(type: "INTEGER", nullable: false),
                    JournalId = table.Column<string>(type: "TEXT", nullable: false),
                    Changes = table.Column<long>(type: "INTEGER", nullable: false),
                    LogsJournalId = table.Column<string>(type: "TEXT", nullable: false),
                    Logs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerCursors", x => x.Node);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shares", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SigningKeys",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Xml = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningKeys", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "SyncChanges",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Table = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncChanges", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "SyncVersions",
                columns: table => new
                {
                    Table = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncVersions", x => new { x.Table, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Upn = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsGuest = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalType = table.Column<string>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Grants_Connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserColors",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserColors", x => new { x.UserId, x.ConnectionId });
                    table.ForeignKey(
                        name: "FK_UserColors_Connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MacroKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    MacroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Keys = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacroKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MacroKeys_Macros_MacroId",
                        column: x => x.MacroId,
                        principalTable: "Macros",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MacroShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MacroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalType = table.Column<string>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacroShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MacroShares_Macros_MacroId",
                        column: x => x.MacroId,
                        principalTable: "Macros",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShareRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShareId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Upn = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Access = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareRecipients_Shares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "Shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Name",
                table: "Connections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Grants_ConnectionId_PrincipalType_PrincipalId",
                table: "Grants",
                columns: new[] { "ConnectionId", "PrincipalType", "PrincipalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogChanges_At",
                table: "LogChanges",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_Logs_Node_EndedAt",
                table: "Logs",
                columns: new[] { "Node", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Logs_SessionId",
                table: "Logs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Logs_StartedAt",
                table: "Logs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MacroKeys_MacroId",
                table: "MacroKeys",
                column: "MacroId");

            migrationBuilder.CreateIndex(
                name: "IX_MacroKeys_UserId_Keys",
                table: "MacroKeys",
                columns: new[] { "UserId", "Keys" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MacroShares_MacroId_PrincipalType_PrincipalId",
                table: "MacroShares",
                columns: new[] { "MacroId", "PrincipalType", "PrincipalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MacroShares_PrincipalId",
                table: "MacroShares",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_Macros_OwnerUserId_Name",
                table: "Macros",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShareRecipients_ShareId",
                table: "ShareRecipients",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_Shares_SessionId",
                table: "Shares",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Shares_Token",
                table: "Shares",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncChanges_At",
                table: "SyncChanges",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_UserColors_ConnectionId",
                table: "UserColors",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Upn",
                table: "Users",
                column: "Upn",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Grants");

            migrationBuilder.DropTable(
                name: "LocalState");

            migrationBuilder.DropTable(
                name: "LogChanges");

            migrationBuilder.DropTable(
                name: "Logs");

            migrationBuilder.DropTable(
                name: "MacroKeys");

            migrationBuilder.DropTable(
                name: "MacroShares");

            migrationBuilder.DropTable(
                name: "PeerCursors");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "ShareRecipients");

            migrationBuilder.DropTable(
                name: "SigningKeys");

            migrationBuilder.DropTable(
                name: "SyncChanges");

            migrationBuilder.DropTable(
                name: "SyncVersions");

            migrationBuilder.DropTable(
                name: "UserColors");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Macros");

            migrationBuilder.DropTable(
                name: "Shares");

            migrationBuilder.DropTable(
                name: "Connections");
        }
    }
}
