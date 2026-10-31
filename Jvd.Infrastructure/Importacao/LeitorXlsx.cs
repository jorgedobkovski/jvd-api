using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Jvd.Application.Importacao;

namespace Jvd.Infrastructure.Importacao;

/// <summary>
/// Leitor mínimo de .xlsx (só a primeira planilha, só texto das células), sem dependência externa. O arquivo vem de
/// upload e é tratado como NÃO CONFIÁVEL: limites de tamanho descompactado (contra "zip bomb"), de linhas e de texto
/// por célula; XML sem DTD e sem resolvedor externo. Nada do conteúdo vai para mensagens de erro.
/// </summary>
public sealed partial class LeitorXlsx : ILeitorDePlanilha
{
    public const long LimiteDescompactadoPorParte = 64L * 1024 * 1024;
    public const int LimiteDeEntradas = 500;
    public const int LimiteDeLinhas = 50_000;
    public const int LimiteDeStringsCompartilhadas = 500_000;
    public const int LimiteDeTextoPorCelula = 10_000;

    private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsPacote = "http://schemas.openxmlformats.org/package/2006/relationships";

    [GeneratedRegex("^[A-Za-z]+")]
    private static partial Regex Coluna();

    public IReadOnlyList<LinhaDaPlanilha> Ler(Stream arquivo)
    {
        try
        {
            using var zip = new ZipArchive(arquivo, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > LimiteDeEntradas) throw Invalida();

            var caminhoDaAba = ResolverPrimeiraAba(zip);
            var compartilhadas = LerStringsCompartilhadas(zip);
            return LerAba(zip, caminhoDaAba, compartilhadas);
        }
        catch (PlanilhaInvalidaException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or FormatException
                                       or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            throw Invalida();
        }
    }

    private static PlanilhaInvalidaException Invalida() =>
        new("O arquivo não é uma planilha .xlsx válida ou excede os limites aceitos.");

    // ---------- abertura segura ----------

    private static XmlReader AbrirXml(ZipArchive zip, string caminho)
    {
        var entrada = zip.GetEntry(caminho) ?? throw Invalida();
        if (entrada.Length > LimiteDescompactadoPorParte) throw Invalida();

        var configuracao = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = LimiteDescompactadoPorParte,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        // O tamanho declarado no cabeçalho do zip pode mentir: o fluxo limitado conta os bytes realmente descompactados.
        return XmlReader.Create(new FluxoLimitado(entrada.Open(), LimiteDescompactadoPorParte), configuracao);
    }

    private static string ResolverPrimeiraAba(ZipArchive zip)
    {
        string? relacao = null;
        using (var leitor = AbrirXml(zip, "xl/workbook.xml"))
        {
            while (leitor.Read())
            {
                if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == "sheet" && leitor.NamespaceURI == NsMain)
                {
                    relacao = leitor.GetAttribute("id", NsRel);
                    break;
                }
            }
        }

        if (relacao is null) return "xl/worksheets/sheet1.xml";

        using var rels = AbrirXml(zip, "xl/_rels/workbook.xml.rels");
        while (rels.Read())
        {
            if (rels.NodeType == XmlNodeType.Element && rels.LocalName == "Relationship" && rels.NamespaceURI == NsPacote
                && rels.GetAttribute("Id") == relacao)
            {
                var alvo = rels.GetAttribute("Target") ?? throw Invalida();
                return alvo.StartsWith('/') ? alvo.TrimStart('/') : "xl/" + alvo;
            }
        }

        throw Invalida();
    }

    // ---------- conteúdo ----------

    private static List<string> LerStringsCompartilhadas(ZipArchive zip)
    {
        var lista = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is null) return lista;

        using var leitor = AbrirXml(zip, "xl/sharedStrings.xml");
        leitor.MoveToContent();
        while (!leitor.EOF)
        {
            if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == "si")
            {
                if (lista.Count >= LimiteDeStringsCompartilhadas) throw Invalida();
                lista.Add(TextoDe((XElement)XNode.ReadFrom(leitor)));
            }
            else
            {
                leitor.Read();
            }
        }

        return lista;
    }

    private static List<LinhaDaPlanilha> LerAba(ZipArchive zip, string caminho, List<string> compartilhadas)
    {
        var linhas = new List<LinhaDaPlanilha>();
        using var leitor = AbrirXml(zip, caminho);
        leitor.MoveToContent();
        var sequencia = 0;

        while (!leitor.EOF)
        {
            if (leitor.NodeType == XmlNodeType.Element && leitor.LocalName == "row")
            {
                if (linhas.Count >= LimiteDeLinhas) throw Invalida();

                var elemento = (XElement)XNode.ReadFrom(leitor);
                sequencia++;
                var numero = int.TryParse((string?)elemento.Attribute("r"), out var n) && n > 0 ? n : sequencia;

                var celulas = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var c in elemento.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    var referencia = (string?)c.Attribute("r");
                    var coluna = referencia is null ? null : Coluna().Match(referencia).Value.ToUpperInvariant();
                    if (string.IsNullOrEmpty(coluna)) continue;

                    var texto = TextoDaCelula(c, compartilhadas);
                    if (!string.IsNullOrEmpty(texto)) celulas[coluna] = texto;
                }

                linhas.Add(new LinhaDaPlanilha(numero, celulas));
            }
            else
            {
                leitor.Read();
            }
        }

        return linhas;
    }

    private static string? TextoDaCelula(XElement celula, List<string> compartilhadas)
    {
        var tipo = (string?)celula.Attribute("t");
        string? texto;

        switch (tipo)
        {
            case "inlineStr":
                texto = TextoDe(celula.Elements().FirstOrDefault(e => e.Name.LocalName == "is"));
                break;
            case "s":
                var valor = celula.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                texto = int.TryParse(valor, out var indice) && indice >= 0 && indice < compartilhadas.Count
                    ? compartilhadas[indice]
                    : null;
                break;
            default: // número, texto de fórmula ("str") e booleano guardam o valor em <v>
                texto = celula.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                break;
        }

        if (texto is null) return null;
        return texto.Length > LimiteDeTextoPorCelula ? texto[..LimiteDeTextoPorCelula] : texto;
    }

    /// <summary>Concatena os textos (<c>t</c>) de um <c>si</c>/<c>is</c>, inclusive de trechos formatados; ignora a pronúncia fonética.</summary>
    private static string TextoDe(XElement? elemento)
    {
        if (elemento is null) return "";

        var partes = elemento.Descendants()
            .Where(e => e.Name.LocalName == "t" && !e.Ancestors().Any(a => a.Name.LocalName == "rPh"))
            .Select(e => e.Value);
        return string.Concat(partes);
    }

    /// <summary>Fluxo que falha se for lido além do limite, contando os bytes realmente descompactados.</summary>
    private sealed class FluxoLimitado(Stream interno, long limite) : Stream
    {
        private long _lidos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = interno.Read(buffer, offset, count);
            _lidos += n;
            if (_lidos > limite) throw Invalida();
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) interno.Dispose();
            base.Dispose(disposing);
        }
    }
}
