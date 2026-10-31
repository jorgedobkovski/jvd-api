using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jvd.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class UnicidadeEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "usuario");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "usuario",
                column: "normalized_email",
                unique: true,
                filter: "[normalized_email] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "usuario");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "usuario",
                column: "normalized_email");
        }
    }
}
