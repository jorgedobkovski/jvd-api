using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jvd.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Importacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "importacao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tipo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    nome_arquivo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    hash_arquivo = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    usuario_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    executada_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    total_lidos = table.Column<int>(type: "int", nullable: false),
                    total_importados = table.Column<int>(type: "int", nullable: false),
                    total_rejeitados = table.Column<int>(type: "int", nullable: false),
                    total_avisos = table.Column<int>(type: "int", nullable: false),
                    total_ignoradas = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_importacao", x => x.id);
                    table.ForeignKey(
                        name: "FK_importacao_usuario_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuario",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "importacao_erro",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    importacao_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    linha = table.Column<int>(type: "int", nullable: false),
                    gravidade = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    motivo = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_importacao_erro", x => x.id);
                    table.ForeignKey(
                        name: "FK_importacao_erro_importacao_importacao_id",
                        column: x => x.importacao_id,
                        principalTable: "importacao",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_importacao_executada_em",
                table: "importacao",
                column: "executada_em");

            migrationBuilder.CreateIndex(
                name: "IX_importacao_tipo_hash_arquivo",
                table: "importacao",
                columns: new[] { "tipo", "hash_arquivo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_importacao_usuario_id",
                table: "importacao",
                column: "usuario_id");

            migrationBuilder.CreateIndex(
                name: "IX_importacao_erro_importacao_id_linha",
                table: "importacao_erro",
                columns: new[] { "importacao_id", "linha" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "importacao_erro");

            migrationBuilder.DropTable(
                name: "importacao");
        }
    }
}
