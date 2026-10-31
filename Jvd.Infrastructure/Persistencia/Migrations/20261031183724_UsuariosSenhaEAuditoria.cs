using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jvd.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class UsuariosSenhaEAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "deve_trocar_senha",
                table: "usuario",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "evento_cadastro",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entidade = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    entidade_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tipo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ator_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ocorrido_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    campos = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evento_cadastro", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evento_cadastro_entidade_entidade_id_ocorrido_em",
                table: "evento_cadastro",
                columns: new[] { "entidade", "entidade_id", "ocorrido_em" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evento_cadastro");

            migrationBuilder.DropColumn(
                name: "deve_trocar_senha",
                table: "usuario");
        }
    }
}
