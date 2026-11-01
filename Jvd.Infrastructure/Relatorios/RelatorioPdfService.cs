using System.Globalization;
using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Jvd.Infrastructure.Relatorios;

/// <summary>
/// PDF do Relatório de Serviço. Lê o relatório pelo <see cref="IRelatorioService"/> (mesmo controle de acesso), usa só
/// os snapshots gravados na emissão e nunca registra o conteúdo em log. Rascunho não tem PDF (não tem número).
/// </summary>
public sealed class RelatorioPdfService(IRelatorioService relatorios) : IRelatorioPdfService
{
    private static readonly CultureInfo Br = new("pt-BR");

    static RelatorioPdfService() => QuestPDF.Settings.License = LicenseType.Community;

    public async Task<Resultado<ArquivoPdf>> GerarAsync(Guid id, ViaDoRelatorio via, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        var obtido = await relatorios.ObterAsync(id, ator, ct);
        if (!obtido.Sucesso) return Resultado<ArquivoPdf>.Falha(obtido.Erros);

        var r = obtido.Valor!;
        if (r.Status == StatusRelatorio.Rascunho || r.Emissor is null || r.Cliente is null || r.Equipamento is null)
            return Resultado<ArquivoPdf>.Falha(new ErroNegocio(CodigosDeErro.RelatorioNaoEmitido, "O relatório ainda é um rascunho e não tem PDF."));

        var bytes = Documento(r, via).GeneratePdf();
        return Resultado<ArquivoPdf>.Ok(new ArquivoPdf(bytes, $"relatorio-{r.NumeroFormatado}.pdf"));
    }

    private static Document Documento(RelatorioDetalhe r, ViaDoRelatorio via) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(t => t.FontSize(10));
        page.Content().Column(col =>
        {
            col.Spacing(6);
            col.Item().Element(c => Cabecalho(c, r));
            col.Item().Element(c => Faixa(c, r, via));
            col.Item().Element(c => Cliente(c, r));
            col.Item().Element(c => Equipamento(c, r));
            col.Item().Element(c => Servico(c, r));
            col.Item().Element(c => Valores(c, r));
            col.Item().PaddingTop(4).Text("Autorizo a execução do(s) serviço(s) conforme orçamento acima.").Italic();
            col.Item().PaddingTop(28).Element(c => Assinaturas(c, r));
        });
    }));

    private static void Cabecalho(IContainer c, RelatorioDetalhe r)
    {
        var e = r.Emissor!;
        c.Border(1).Padding(8).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(e.RazaoSocial).Bold().FontSize(14);
                if (!string.IsNullOrWhiteSpace(e.NomeFantasia)) col.Item().Text(e.NomeFantasia);
                col.Item().Text($"CNPJ {e.Cnpj}   {Parte("IE", e.InscricaoEstadual)}");
                col.Item().Text(Endereco(e.Logradouro, e.Numero, e.Bairro, e.Cep, e.Cidade, e.Uf));
                if (!string.IsNullOrWhiteSpace(e.Telefones)) col.Item().Text(e.Telefones);
                if (!string.IsNullOrWhiteSpace(e.NumeroAutorizacaoIpem)) col.Item().Text($"Autorização IPEM/RO: {e.NumeroAutorizacaoIpem}").Bold();
            });
            row.ConstantItem(150).Column(col =>
            {
                col.Item().AlignRight().Text("RELATÓRIO DE SERVIÇO").Bold();
                col.Item().AlignRight().Text($"Nº {r.NumeroFormatado}").Bold().FontSize(18);
            });
        });
    }

    private static void Faixa(IContainer c, RelatorioDetalhe r, ViaDoRelatorio via)
    {
        var aviso = r.Status == StatusRelatorio.Cancelado ? "CANCELADO"
            : r.Revisao > 0 ? $"RETIFICADO — REVISÃO {r.Revisao}"
            : "";

        c.Row(row =>
        {
            row.RelativeItem().Text(aviso).Bold().FontSize(13);
            row.ConstantItem(200).AlignRight().Text($"Via de {Via(via)}").Bold();
        });
    }

    private static void Cliente(IContainer c, RelatorioDetalhe r)
    {
        var cl = r.Cliente!;
        Bloco(c, "Cliente", col =>
        {
            col.Item().Text(cl.Nome).Bold();
            col.Item().Text($"{Parte("CPF/CNPJ", cl.Documento)}   {Parte("IE/RG", cl.InscricaoEstadualRg)}");
            col.Item().Text(Endereco(cl.Logradouro, cl.Numero, cl.Bairro, cl.Cep, cl.Cidade, cl.Uf));
            if (!string.IsNullOrWhiteSpace(cl.Telefone)) col.Item().Text($"Telefone: {cl.Telefone}");
        });
    }

    private static void Equipamento(IContainer c, RelatorioDetalhe r)
    {
        var eq = r.Equipamento!;
        Bloco(c, "Equipamento", col =>
        {
            col.Item().Text(eq.Descricao).Bold();
            col.Item().Text($"{Parte("Nº de série", eq.NumeroSerie)}   {Parte("Nº INMETRO", eq.NumeroInmetro)}");
            var anterior = Juntar(r.VerificacaoAnteriorData is { } d ? $"Verificação anterior: {d.ToString("dd/MM/yyyy", Br)}" : null, Parte("Certificado", r.VerificacaoAnteriorCertificado));
            if (anterior.Length > 0) col.Item().Text(anterior);
        });
    }

    private static void Servico(IContainer c, RelatorioDetalhe r)
    {
        Bloco(c, "Serviço", col =>
        {
            col.Item().Text($"{TipoTexto(r.TipoServico)}  —  {r.DataServico.ToString("dd/MM/yyyy", Br)}").Bold();
            col.Item().Text(r.ServicoExecutado);
            if (!string.IsNullOrWhiteSpace(r.Observacoes)) col.Item().PaddingTop(4).Text($"Observações: {r.Observacoes}");
            if (!string.IsNullOrWhiteSpace(r.NumeroSeloReparador)) col.Item().Text($"Selo do reparador: {r.NumeroSeloReparador}");
            col.Item().PaddingTop(4).Text($"Técnico executor: {r.TecnicoNome}");
        });
    }

    private static void Valores(IContainer c, RelatorioDetalhe r)
    {
        Bloco(c, "Valores", col =>
        {
            col.Item().Text($"Garantia: {r.GarantiaExtenso}");
            col.Item().Text($"Total: {r.Total.ToString("C2", Br)}    Desconto: {r.Desconto.ToString("C2", Br)}");
            col.Item().Text($"Total geral: {r.TotalGeral.ToString("C2", Br)}").Bold().FontSize(12);
        });
    }

    private static void Assinaturas(IContainer c, RelatorioDetalhe r)
    {
        var e = r.Emissor!;
        c.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.8f);
                col.Item().AlignCenter().Text("Ass. do Técnico").FontSize(9);
                if (!string.IsNullOrWhiteSpace(e.ResponsavelTecnicoNome))
                    col.Item().AlignCenter().Text($"{e.ResponsavelTecnicoNome}   {Parte("CPF", e.ResponsavelTecnicoCpf)}").FontSize(9);
            });
            row.ConstantItem(30);
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.8f);
                col.Item().AlignCenter().Text("Ass. do Cliente").FontSize(9);
            });
        });
    }

    private static void Bloco(IContainer c, string titulo, Action<ColumnDescriptor> conteudo) =>
        c.Border(0.7f).Padding(6).Column(col =>
        {
            col.Item().Text(titulo.ToUpperInvariant()).Bold().FontSize(8);
            conteudo(col);
        });

    private static string Juntar(params string?[] partes) => string.Join("   ", partes.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Parte(string rotulo, string? valor) => string.IsNullOrWhiteSpace(valor) ? "" : $"{rotulo}: {valor}";

    private static string Endereco(string? logradouro, string? numero, string? bairro, string? cep, string? cidade, string? uf)
    {
        var rua = string.Join(", ", new[] { logradouro, numero }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var cidadeUf = string.Join("/", new[] { cidade, uf }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.Join(" — ", new[] { rua, bairro, cidadeUf, string.IsNullOrWhiteSpace(cep) ? null : $"CEP {cep}" }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string Via(ViaDoRelatorio via) => via switch
    {
        ViaDoRelatorio.Controle => "controle",
        ViaDoRelatorio.Cliente => "cliente",
        _ => "fiscalização"
    };

    private static string TipoTexto(TipoServico t) => t switch
    {
        TipoServico.Corretiva => "Manutenção corretiva",
        TipoServico.Preventiva => "Manutenção preventiva",
        TipoServico.Instalacao => "Instalação",
        _ => "Inspeção"
    };
}
