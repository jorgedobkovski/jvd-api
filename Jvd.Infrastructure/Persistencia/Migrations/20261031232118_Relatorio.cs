using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jvd.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Relatorio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contador_numeracao",
                columns: table => new
                {
                    chave = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ultimo = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contador_numeracao", x => x.chave);
                });

            migrationBuilder.CreateTable(
                name: "relatorio",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    numero = table.Column<int>(type: "int", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    tipo_servico = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    data_servico = table.Column<DateOnly>(type: "date", nullable: false),
                    cliente_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    equipamento_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tecnico_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    criado_por = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    servico_executado = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    observacoes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    verificacao_anterior_data = table.Column<DateOnly>(type: "date", nullable: true),
                    verificacao_anterior_certificado = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    numero_selo_reparador = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    garantia_dias = table.Column<int>(type: "int", nullable: false),
                    total = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    desconto = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    total_geral = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    catalogo_servico_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    revisao = table.Column<int>(type: "int", nullable: false),
                    cliente_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    equipamento_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    emissor_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    emitido_em = table.Column<DateTime>(type: "datetime2", nullable: true),
                    emitido_por = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    cancelado_em = table.Column<DateTime>(type: "datetime2", nullable: true),
                    cancelado_por = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    motivo_cancelamento = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    criado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relatorio", x => x.id);
                    table.CheckConstraint("ck_relatorio_desconto", "[desconto] >= 0 AND [desconto] <= [total]");
                    table.CheckConstraint("ck_relatorio_garantia", "[garantia_dias] >= 0");
                    table.CheckConstraint("ck_relatorio_numero", "([status] = 'Rascunho' AND [numero] IS NULL) OR ([status] <> 'Rascunho' AND [numero] IS NOT NULL)");
                    table.CheckConstraint("ck_relatorio_total_geral", "[total_geral] = [total] - [desconto]");
                    table.ForeignKey(
                        name: "FK_relatorio_catalogo_servico_catalogo_servico_id",
                        column: x => x.catalogo_servico_id,
                        principalTable: "catalogo_servico",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relatorio_cliente_cliente_id",
                        column: x => x.cliente_id,
                        principalTable: "cliente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relatorio_equipamento_equipamento_id",
                        column: x => x.equipamento_id,
                        principalTable: "equipamento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relatorio_usuario_criado_por",
                        column: x => x.criado_por,
                        principalTable: "usuario",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relatorio_usuario_tecnico_id",
                        column: x => x.tecnico_id,
                        principalTable: "usuario",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "relatorio_evento",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    relatorio_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tipo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    usuario_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ocorrido_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    detalhe = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relatorio_evento", x => x.id);
                    table.ForeignKey(
                        name: "FK_relatorio_evento_relatorio_relatorio_id",
                        column: x => x.relatorio_id,
                        principalTable: "relatorio",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relatorio_evento_usuario_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuario",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "contador_numeracao",
                columns: new[] { "chave", "ultimo" },
                values: new object[] { "relatorio", 0 });

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_catalogo_servico_id",
                table: "relatorio",
                column: "catalogo_servico_id");

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_cliente_id_data_servico",
                table: "relatorio",
                columns: new[] { "cliente_id", "data_servico" });

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_criado_por",
                table: "relatorio",
                column: "criado_por");

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_data_servico",
                table: "relatorio",
                column: "data_servico");

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_equipamento_id_data_servico",
                table: "relatorio",
                columns: new[] { "equipamento_id", "data_servico" });

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_numero",
                table: "relatorio",
                column: "numero",
                unique: true,
                filter: "[numero] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_status",
                table: "relatorio",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_tecnico_id_data_servico",
                table: "relatorio",
                columns: new[] { "tecnico_id", "data_servico" });

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_evento_relatorio_id_ocorrido_em",
                table: "relatorio_evento",
                columns: new[] { "relatorio_id", "ocorrido_em" });

            migrationBuilder.CreateIndex(
                name: "IX_relatorio_evento_usuario_id",
                table: "relatorio_evento",
                column: "usuario_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contador_numeracao");

            migrationBuilder.DropTable(
                name: "relatorio_evento");

            migrationBuilder.DropTable(
                name: "relatorio");
        }
    }
}
