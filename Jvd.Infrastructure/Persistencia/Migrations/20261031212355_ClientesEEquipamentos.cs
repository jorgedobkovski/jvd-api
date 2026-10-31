using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jvd.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ClientesEEquipamentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cliente",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    nome_razao_social = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    nome_fantasia = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    documento = table.Column<string>(type: "nvarchar(14)", maxLength: 14, nullable: true),
                    inscricao_estadual_rg = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    logradouro = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    numero = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    bairro = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    cep = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    cidade = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    uf = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    telefone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    cadastro_incompleto = table.Column<bool>(type: "bit", nullable: false),
                    origem = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ativo = table.Column<bool>(type: "bit", nullable: false),
                    criado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cliente", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "equipamento",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    cliente_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    descricao = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    numero_serie = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    numero_inmetro = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    observacoes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ativo = table.Column<bool>(type: "bit", nullable: false),
                    criado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipamento", x => x.id);
                    table.ForeignKey(
                        name: "FK_equipamento_cliente_cliente_id",
                        column: x => x.cliente_id,
                        principalTable: "cliente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cliente_ativo",
                table: "cliente",
                column: "ativo");

            migrationBuilder.CreateIndex(
                name: "IX_cliente_documento",
                table: "cliente",
                column: "documento");

            migrationBuilder.CreateIndex(
                name: "IX_cliente_nome_razao_social",
                table: "cliente",
                column: "nome_razao_social");

            migrationBuilder.CreateIndex(
                name: "IX_equipamento_cliente_id",
                table: "equipamento",
                column: "cliente_id");

            migrationBuilder.CreateIndex(
                name: "IX_equipamento_numero_inmetro",
                table: "equipamento",
                column: "numero_inmetro");

            migrationBuilder.CreateIndex(
                name: "IX_equipamento_numero_serie",
                table: "equipamento",
                column: "numero_serie");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipamento");

            migrationBuilder.DropTable(
                name: "cliente");
        }
    }
}
