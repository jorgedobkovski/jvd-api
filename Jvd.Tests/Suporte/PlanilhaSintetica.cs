using System.IO.Compression;
using System.Xml.Linq;

namespace Jvd.Tests.Suporte;

/// <summary>
/// Monta arquivos .xlsx SINTÉTICOS (nunca dados reais) com o mesmo layout das exportações do ERP descrito em
/// Specs/IMPORTACAO.md: texto em células <c>inlineStr</c> (ou em strings compartilhadas, para testar os dois caminhos).
/// </summary>
public static class PlanilhaSintetica
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pacote = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Cada item é uma linha (coluna → texto); dicionário vazio = linha em branco.</summary>
    public static byte[] Xlsx(IEnumerable<IReadOnlyDictionary<string, string>> linhas, bool textoCompartilhado = false)
    {
        var compartilhadas = new List<string>();

        string Indice(string texto)
        {
            var i = compartilhadas.IndexOf(texto);
            if (i < 0) { compartilhadas.Add(texto); i = compartilhadas.Count - 1; }
            return i.ToString();
        }

        var dados = new XElement(Main + "sheetData");
        var numero = 0;
        foreach (var linha in linhas)
        {
            numero++;
            var elemento = new XElement(Main + "row", new XAttribute("r", numero));
            foreach (var (coluna, texto) in linha.OrderBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                var celula = new XElement(Main + "c", new XAttribute("r", coluna + numero));
                if (textoCompartilhado)
                {
                    celula.Add(new XAttribute("t", "s"), new XElement(Main + "v", Indice(texto)));
                }
                else
                {
                    celula.Add(new XAttribute("t", "inlineStr"),
                        new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), texto)));
                }

                elemento.Add(celula);
            }

            dados.Add(elemento);
        }

        var aba = new XDocument(new XElement(Main + "worksheet", dados));
        return Empacotar(aba, compartilhadas);
    }

    public static byte[] Empacotar(XDocument aba, IReadOnlyList<string>? compartilhadas = null, string nomeDaParte = "xl/worksheets/sheet1.xml")
    {
        var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            Escrever(zip, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Escrever(zip, "xl/workbook.xml", new XDocument(new XElement(Main + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
                new XElement(Main + "sheets", new XElement(Main + "sheet", new XAttribute("name", "Planilha"),
                    new XAttribute("sheetId", 1), new XAttribute(Rel + "id", "rId1"))))).ToString());
            Escrever(zip, "xl/_rels/workbook.xml.rels", new XDocument(new XElement(Pacote + "Relationships",
                new XElement(Pacote + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Target", nomeDaParte.Replace("xl/", "")))))
                .ToString());
            Escrever(zip, nomeDaParte, aba.ToString());

            if (compartilhadas is { Count: > 0 })
            {
                Escrever(zip, "xl/sharedStrings.xml", new XDocument(new XElement(Main + "sst",
                    compartilhadas.Select(t => new XElement(Main + "si", new XElement(Main + "t", t))))).ToString());
            }
        }

        return memoria.ToArray();
    }

    public static void Escrever(ZipArchive zip, string nome, string conteudo)
    {
        var entrada = zip.CreateEntry(nome);
        using var escritor = new StreamWriter(entrada.Open(), new System.Text.UTF8Encoding(false));
        escritor.Write(conteudo);
    }

    // ---------- clientes (layout do Relatorio222) ----------

    public static Dictionary<string, string> CabecalhoDeClientes() => new()
    {
        ["A"] = "Razão Social / Nome", ["C"] = "Fantasia", ["F"] = "CNPJ/CPF", ["G"] = "IE/RG", ["H"] = "Endereço",
        ["I"] = "Fone", ["J"] = "Comercial", ["K"] = "Celular", ["L"] = "Email", ["N"] = "Inativo"
    };

    /// <summary>Endereço padrão do ERP: logradouro - número - bairro - complemento - cidade - UF.</summary>
    public static string Endereco(string logradouro = "RUA SINTETICA", string numero = "100", string bairro = "CENTRO",
        string complemento = "", string cidade = "CIDADE TESTE", string uf = "RO") =>
        string.Join(" - ", logradouro, numero, bairro, complemento, cidade, uf);

    public static Dictionary<string, string> Cliente(
        string nome, string? documento, string? endereco = null, string? fantasia = null, string? inscricao = "123",
        string? fone = null, string? email = null, string inativo = "NÃO")
    {
        var linha = new Dictionary<string, string> { ["A"] = nome, ["H"] = endereco ?? Endereco(), ["N"] = inativo };
        if (documento is not null) linha["F"] = documento;
        if (fantasia is not null) linha["C"] = fantasia;
        if (inscricao is not null) linha["G"] = inscricao;
        if (fone is not null) linha["I"] = fone;
        if (email is not null) linha["L"] = email;
        return linha;
    }

    // ---------- mercadorias (layout do Relatorio211) ----------

    public static Dictionary<string, string> CabecalhoDeMercadorias() => new()
    {
        ["A"] = "Descrição", ["E"] = "Cod. Barras", ["F"] = "Marca", ["G"] = "Emb.", ["H"] = "Balança", ["I"] = "Preço R$", ["O"] = "Inativo"
    };

    public static Dictionary<string, string> Grupo(string nome) => new() { ["A"] = "GRUPO:" + nome };

    public static Dictionary<string, string> Subgrupo(string nome = "GERAL") => new() { ["A"] = "SUBGRUPO:" + nome, ["J"] = "Cadastro", ["L"] = "Alteração" };

    public static Dictionary<string, string> Item(string codigo, string descricao, string? preco, string inativo = "NÃO")
    {
        var linha = new Dictionary<string, string> { ["A"] = $"{codigo} - {descricao}", ["E"] = codigo, ["G"] = "PC", ["H"] = "NÃO", ["O"] = inativo };
        if (preco is not null) linha["I"] = preco;
        return linha;
    }

    public static IReadOnlyDictionary<string, string> Vazia() => new Dictionary<string, string>();

    public static byte[] PlanilhaDeClientes(params IReadOnlyDictionary<string, string>[] clientes) =>
        Xlsx(new IReadOnlyDictionary<string, string>[] { new Dictionary<string, string> { ["A"] = "Relação de Clientes (título sintético)" }, Vazia(), CabecalhoDeClientes() }
            .Concat(clientes));

    public static byte[] PlanilhaDeMercadorias(params IReadOnlyDictionary<string, string>[] linhasDoCorpo) =>
        Xlsx(new IReadOnlyDictionary<string, string>[] { Vazia(), CabecalhoDeMercadorias() }.Concat(linhasDoCorpo));
}
