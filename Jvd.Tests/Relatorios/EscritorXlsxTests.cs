using System.IO.Compression;
using System.Xml.Linq;
using Jvd.Infrastructure.Importacao;
using Jvd.Infrastructure.Relatorios;

namespace Jvd.Tests.Relatorios;

/// <summary>Gerador de .xlsx da exportação (Fase 8B): estrutura do pacote, tipos de célula e higiene do texto. Sem banco.</summary>
public class EscritorXlsxTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static byte[] Gerar(IReadOnlyList<string>? cabecalho = null, params IReadOnlyList<object?>[] linhas) =>
        EscritorXlsx.Escrever("Relatórios emitidos", cabecalho ?? ["Nº", "Data", "Cliente", "Total"], linhas);

    private static XDocument Parte(byte[] xlsx, string caminho)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var entrada = zip.GetEntry(caminho) ?? throw new InvalidOperationException($"Faltou a parte {caminho}");
        using var fluxo = entrada.Open();
        return XDocument.Load(fluxo);
    }

    private static List<XElement> Celulas(byte[] xlsx) => Parte(xlsx, "xl/worksheets/sheet1.xml").Descendants(Ns + "c").ToList();

    private static XElement Celula(byte[] xlsx, string referencia) => Celulas(xlsx).Single(c => (string?)c.Attribute("r") == referencia);

    [Fact]
    public void GeraUmPacoteOpenXmlComAsPartesObrigatorias_TodasXmlValido()
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 10, 30), "Cliente", 10.5m]);

        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var nomes = zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml"], nomes);
        foreach (var nome in nomes) Assert.NotNull(Parte(xlsx, nome).Root);
    }

    [Fact]
    public void CabecalhoVaiEmNegrito_NaPrimeiraLinha_ECongelado()
    {
        var xlsx = Gerar();

        Assert.All(new[] { "A1", "B1", "C1", "D1" }, r => Assert.Equal("1", (string?)Celula(xlsx, r).Attribute("s")));
        var pane = Parte(xlsx, "xl/worksheets/sheet1.xml").Descendants(Ns + "pane").Single();
        Assert.Equal("frozen", (string?)pane.Attribute("state"));
        Assert.Equal("1", (string?)pane.Attribute("ySplit"));
    }

    [Fact]
    public void Datas_ViramNumeroDeSerieDoExcel_ComEstiloDeData()
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 10, 30), "x", 1m]);

        var data = Celula(xlsx, "B2");
        Assert.Equal("46325", (string?)data.Element(Ns + "v")); // 30/10/2026
        Assert.Equal("2", (string?)data.Attribute("s"));
        Assert.Null(data.Attribute("t"));
    }

    [Fact]
    public void Valores_UsamPontoDecimalIndependenteDaCulturaDoServidor_ComEstiloDeDinheiro()
    {
        var anterior = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
        try
        {
            var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), "x", 1234.5m]);

            var dinheiro = Celula(xlsx, "D2");
            Assert.Equal("1234.5", (string?)dinheiro.Element(Ns + "v"));
            Assert.Equal("3", (string?)dinheiro.Attribute("s"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = anterior;
        }
    }

    [Theory]
    [InlineData("22.00", "22")]
    [InlineData("120.50", "120.5")]
    [InlineData("0.00", "0")]
    [InlineData("9999999999.99", "9999999999.99")]
    public void Valores_ComEscalaDoBanco_SaemNormalizados(string valor, string esperado)
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), "x", decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture)]);

        Assert.Equal(esperado, (string?)Celula(xlsx, "D2").Element(Ns + "v"));
    }

    [Fact]
    public void Inteiros_SaemComoNumero()
    {
        var xlsx = Gerar(null, [42, new DateOnly(2026, 1, 1), "x", 1m]);

        var numero = Celula(xlsx, "A2");
        Assert.Equal("42", (string?)numero.Element(Ns + "v"));
        Assert.Null(numero.Attribute("t"));
    }

    [Theory]
    [InlineData("=SOMA(A1:A9)")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    public void TextoQueParecerFormula_SaiComoTextoLiteral_NuncaComoFormula(string texto)
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), texto, 1m]);

        var celula = Celula(xlsx, "C2");
        Assert.Equal("inlineStr", (string?)celula.Attribute("t"));
        Assert.Equal(texto, celula.Descendants(Ns + "t").Single().Value);
        Assert.Empty(celula.Elements(Ns + "f"));
        Assert.Empty(Parte(xlsx, "xl/worksheets/sheet1.xml").Descendants(Ns + "f"));
    }

    [Fact]
    public void CaracteresEspeciaisDoXml_SaoEscapados_EAcentosPassamIntactos()
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), "Mercado <A&B> \"São João\" ção", 1m]);

        Assert.Equal("Mercado <A&B> \"São João\" ção", Celula(xlsx, "C2").Descendants(Ns + "t").Single().Value);
    }

    [Fact]
    public void CaracteresDeControleQueOXmlNaoAceita_ViramEspaco_SemQuebrarOArquivo()
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), "a\u0000b\u0001c\u000bd\u001fe\tf", 1m]);

        var texto = Celula(xlsx, "C2").Descendants(Ns + "t").Single().Value;
        Assert.Equal("a b c d e\tf", texto);
    }

    [Fact]
    public void EmojiEOutrosCaracteresForaDoPlanoBasico_PassamIntactos()
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), "Balança 😀", 1m]);

        Assert.Equal("Balança 😀", Celula(xlsx, "C2").Descendants(Ns + "t").Single().Value);
    }

    [Fact]
    public void TextoGigante_ECortadoNoLimiteDoExcel()
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), new string('x', 40_000), 1m]);

        Assert.Equal(32_000, Celula(xlsx, "C2").Descendants(Ns + "t").Single().Value.Length);
    }

    [Fact]
    public void CelulaNula_NaoGeraNada_EAsOutrasMantemAPosicao()
    {
        var xlsx = Gerar(null, [1, null, "só cliente", 5m]);

        Assert.DoesNotContain(Celulas(xlsx), c => (string?)c.Attribute("r") == "B2");
        Assert.Equal("só cliente", Celula(xlsx, "C2").Descendants(Ns + "t").Single().Value);
        Assert.Equal("5", (string?)Celula(xlsx, "D2").Element(Ns + "v"));
    }

    [Fact]
    public void SemLinhas_GeraSoOCabecalho_ComFiltroValido()
    {
        var xlsx = Gerar();

        var filtro = Parte(xlsx, "xl/worksheets/sheet1.xml").Descendants(Ns + "autoFilter").Single();
        Assert.Equal("A1:D1", (string?)filtro.Attribute("ref"));
        Assert.Equal(4, Celulas(xlsx).Count);
    }

    [Fact]
    public void FiltroCobreTodasAsLinhas()
    {
        var xlsx = Gerar(null, [1, new DateOnly(2026, 1, 1), "a", 1m], [2, new DateOnly(2026, 1, 2), "b", 2m], [3, new DateOnly(2026, 1, 3), "c", 3m]);

        Assert.Equal("A1:D4", (string?)Parte(xlsx, "xl/worksheets/sheet1.xml").Descendants(Ns + "autoFilter").Single().Attribute("ref"));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void NomeDaColuna_SegueAConvencaoDoExcel(int indice, string esperado) => Assert.Equal(esperado, EscritorXlsx.Coluna(indice));

    [Fact]
    public void NomeDaAba_SemCaracteresProibidos_ATe31Caracteres()
    {
        var xlsx = EscritorXlsx.Escrever("Relatórios: [2026]/\\?*" + new string('z', 40), ["A"], []);

        var nome = (string?)Parte(xlsx, "xl/workbook.xml").Descendants(Ns + "sheet").Single().Attribute("name");
        Assert.NotNull(nome);
        Assert.True(nome!.Length <= 31);
        Assert.DoesNotContain(nome, c => "[]:*?/\\".Contains(c));
        Assert.StartsWith("Relatórios", nome);
    }

    [Fact]
    public void AMesmaPlanilha_PodeSerLidaPeloLeitorDoSistema_SemErroDeEstrutura()
    {
        // Prova de que o pacote é um .xlsx legível (o leitor é o mesmo usado na importação, com limites e DTD proibido).
        var xlsx = Gerar(null, [1, new DateOnly(2026, 10, 30), "Cliente Teste", 10.5m]);

        var leitor = new LeitorXlsx();
        var linhas = leitor.Ler(new MemoryStream(xlsx));

        Assert.Equal(2, linhas.Count);
        Assert.Equal("Cliente", linhas[0].Valor("C"));
        Assert.Equal("Cliente Teste", linhas[1].Valor("C"));
    }
}
