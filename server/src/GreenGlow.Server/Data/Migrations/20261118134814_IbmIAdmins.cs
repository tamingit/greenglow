using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenGlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class IbmIAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdminFromIbmI",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminFromIbmI",
                table: "Users");
        }
    }
}
