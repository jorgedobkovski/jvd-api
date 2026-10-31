using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jvd.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class EmissorECatalogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalogo_servico",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    codigo_origem = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    descricao = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    preco_sugerido = table.Column<decimal>(type: "decimal(12,2)", nullable: true),
                    ativo = table.Column<bool>(type: "bit", nullable: false),
                    criado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    atualizado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalogo_servico", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "emissor",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    razao_social = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    nome_fantasia = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    cnpj = table.Column<string>(type: "nvarchar(14)", maxLength: 14, nullable: false),
                    inscricao_estadual = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    logradouro = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    numero = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    bairro = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    cep = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    cidade = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    uf = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    telefones = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    numero_autorizacao_ipem = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    responsavel_tecnico_nome = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    responsavel_tecnico_cpf = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    atualizado_em = table.Column<DateTime>(type: "datetime2", nullable: false),
                    versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_emissor", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_catalogo_servico_ativo",
                table: "catalogo_servico",
                column: "ativo");

            migrationBuilder.CreateIndex(
                name: "IX_catalogo_servico_descricao",
                table: "catalogo_servico",
                column: "descricao");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalogo_servico");

            migrationBuilder.DropTable(
                name: "emissor");
        }
    }
}
