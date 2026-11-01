using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jvd.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class RelatorioRetificacaoCancelamento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_relatorio_garantia",
                table: "relatorio");

            migrationBuilder.CreateTable(
                name: "relatorio_revisao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    relatorio_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    revisao = table.Column<int>(type: "int", nullable: false),
                    retificado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    retificado_por = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    motivo = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    dados_anteriores = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    dados_novos = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relatorio_revisao", x => x.id);
                    table.CheckConstraint("ck_relatorio_revisao_numero", "[revisao] >= 1");
                    table.ForeignKey(
                        name: "FK_relatorio_revisao_relatorio_relatorio_id",
                        column: x => x.relatorio_id,
                        principalTable: "relatorio",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relatorio_revisao_usuario_retificado_por",
                        column: x => x.retificado_por,
                        principalTable: "usuario",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_relatorio_cancelamento",
                table: "relatorio",
                sql: "([status] = 'Cancelado' AND [cancelado_em] IS NOT NULL AND [cancelado_por] IS NOT NULL AND [motivo_cancelamento] IS NOT NULL) OR ([status] <> 'Cancelado' AND [cancelado_em] IS NULL AND [cancelado_por] IS NULL AND [motivo_cancelamento] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_relatorio_garantia",
                table: "relatorio",
                sql: "[garantia_dias] >= 0 AND [garantia_dias] <= 9999");

            migrationBuilder.AddCheckConstraint(
                name: "ck_relatorio_revisao",
                table: "relatorio",
                sql: "[revisao] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_revisao_relatorio_id_revisao",
                table: "relatorio_revisao",
                columns: new[] { "relatorio_id", "revisao" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_revisao_retificado_por",
                table: "relatorio_revisao",
                column: "retificado_por");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relatorio_revisao");

            migrationBuilder.DropCheckConstraint(
                name: "ck_relatorio_cancelamento",
                table: "relatorio");

            migrationBuilder.DropCheckConstraint(
                name: "ck_relatorio_garantia",
                table: "relatorio");

            migrationBuilder.DropCheckConstraint(
                name: "ck_relatorio_revisao",
                table: "relatorio");

            migrationBuilder.AddCheckConstraint(
                name: "ck_relatorio_garantia",
                table: "relatorio",
                sql: "[garantia_dias] >= 0");
        }
    }
}
