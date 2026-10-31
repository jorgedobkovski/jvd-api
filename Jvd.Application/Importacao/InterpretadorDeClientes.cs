using Jvd.Application.Clientes;
using Jvd.Domain;

namespace Jvd.Application.Importacao;

public sealed record ClienteParaImportar(int Linha, DadosDoCliente Dados, bool Ativo);

public sealed record InterpretacaoDeClientes(
    bool EstruturaValida,
    IReadOnlyList<ClienteParaImportar> Clientes,
    IReadOnlyList<Ocorrencia> Ocorrencias,
    int Lidos,
    int Ignoradas);

/// <summary>
/// Transforma as linhas da planilha de clientes do ERP (Relatorio222) em cadastros. Regra pura, sem banco: as
/// decisões estão em Specs/IMPORTACAO.md. As mensagens são fixas — jamais repetem o conteúdo da linha (LGPD).
/// </summary>
public static class InterpretadorDeClientes
{
    private const string ColNome = "A", ColFantasia = "C", ColDocumento = "F", ColInscricao = "G", ColEndereco = "H";
    private const string ColFone = "I", ColComercial = "J", ColCelular = "K", ColEmail = "L", ColInativo = "N";
    private const string SeparadorDeEndereco = " - ";
    private const int SegmentosDoPadrao = 6;

    public static InterpretacaoDeClientes Interpretar(IEnumerable<LinhaDaPlanilha> linhas)
    {
        var clientes = new List<ClienteParaImportar>();
        var ocorrencias = new List<Ocorrencia>();
        var lidos = 0;
        var ignoradas = 0;
        var viuCabecalho = false;

        foreach (var linha in linhas)
        {
            if (EhCabecalho(linha))
            {
                viuCabecalho = true;
                ignoradas++;
                continue;
            }

            if (!viuCabecalho || linha.Valor(ColNome) is null || linha.Valor(ColEndereco) is null)
            {
                if (linha.Celulas.Count > 0) ignoradas++; // linha em branco não conta; título/rodapé/ruído sim
                continue;
            }

            lidos++;
            var aviso = (string motivo) => ocorrencias.Add(new Ocorrencia(linha.Numero, GravidadeOcorrencia.Aviso, motivo));
            clientes.Add(new ClienteParaImportar(linha.Numero, MontarDados(linha, aviso), EstaAtivo(linha, aviso)));
        }

        return new InterpretacaoDeClientes(viuCabecalho, clientes, ocorrencias, lidos, ignoradas);
    }

    private static bool EhCabecalho(LinhaDaPlanilha l) =>
        Normalizar(l.Valor(ColNome)) == "RAZAO SOCIAL / NOME"
        && Normalizar(l.Valor(ColDocumento)) == "CNPJ/CPF"
        && Normalizar(l.Valor(ColEndereco)) == "ENDERECO";

    private static DadosDoCliente MontarDados(LinhaDaPlanilha l, Action<string> aviso)
    {
        var nome = Cortar(l.Valor(ColNome), 200, "Nome/razão social cortado em 200 caracteres.", aviso) ?? "";
        var fantasia = Cortar(l.Valor(ColFantasia), 200, "Nome fantasia cortado em 200 caracteres.", aviso);
        var inscricao = Cortar(l.Valor(ColInscricao), 30, "Inscrição estadual/RG cortada em 30 caracteres.", aviso);

        var documento = Documento(l.Valor(ColDocumento), aviso);
        var endereco = Endereco(l.Valor(ColEndereco)!, aviso);

        return new DadosDoCliente(
            nome, fantasia, documento, inscricao, endereco.Logradouro, endereco.Numero, endereco.Bairro, endereco.Cep,
            endereco.Cidade, endereco.Uf, Telefone(l, aviso), Email(l.Valor(ColEmail), aviso));
    }

    private static string? Documento(string? bruto, Action<string> aviso)
    {
        var digitos = Documentos.SomenteDigitos(bruto);
        if (digitos is null)
        {
            aviso("Sem CPF/CNPJ: importado como cadastro incompleto.");
            return null;
        }

        if (!Documentos.CpfOuCnpjValido(digitos))
        {
            aviso("CPF/CNPJ inválido: importado sem documento, como cadastro incompleto.");
            return null;
        }

        return digitos;
    }

    private sealed record EnderecoLido(string? Logradouro, string? Numero, string? Bairro, string? Cep, string? Cidade, string? Uf);

    private static EnderecoLido Endereco(string texto, Action<string> aviso)
    {
        var segmentos = texto.Split(SeparadorDeEndereco).Select(s => s.Trim()).ToArray();

        if (segmentos.Length != SegmentosDoPadrao)
        {
            aviso("Endereço fora do padrão: importado inteiro no logradouro; revisar.");
            return new EnderecoLido(Cortar(texto, 150, "Endereço cortado em 150 caracteres.", aviso), null, null, null, null, null);
        }

        // LOGRADOURO - NÚMERO - BAIRRO - COMPLEMENTO - CIDADE - UF
        string? Vazio(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
        var logradouro = Vazio(segmentos[0]);
        var complemento = Vazio(segmentos[3]);
        string? cep = null;

        if (complemento is not null && ParecidoComCep(complemento))
        {
            cep = Campos.Cep(complemento);
            complemento = null;
        }

        // O cadastro não tem campo de complemento: ele vai junto do logradouro.
        if (complemento is not null) logradouro = logradouro is null ? complemento : $"{logradouro}, {complemento}";

        var uf = Campos.Uf(Vazio(segmentos[5]));
        if (uf is not null && !Campos.UfValida(uf))
        {
            aviso("UF inválida: ignorada.");
            uf = null;
        }

        return new EnderecoLido(
            Cortar(logradouro, 150, "Logradouro cortado em 150 caracteres.", aviso),
            Cortar(Vazio(segmentos[1]), 20, "Número cortado em 20 caracteres.", aviso),
            Cortar(Vazio(segmentos[2]), 80, "Bairro cortado em 80 caracteres.", aviso),
            cep,
            Cortar(Vazio(segmentos[4]), 80, "Cidade cortada em 80 caracteres.", aviso),
            uf);
    }

    /// <summary>Só dígitos (com pontuação de CEP) e exatamente 8 deles; um complemento como "SALA 12" não passa.</summary>
    private static bool ParecidoComCep(string texto) =>
        texto.All(c => char.IsAsciiDigit(c) || c is '-' or '.' or ' ') && Campos.CepValido(Campos.Cep(texto));

    private static string? Telefone(LinhaDaPlanilha l, Action<string> aviso)
    {
        var distintos = new[] { l.Valor(ColFone), l.Valor(ColComercial), l.Valor(ColCelular) }
            .Where(t => t is not null).Select(t => t!).Distinct().ToArray();
        return distintos.Length == 0
            ? null
            : Cortar(string.Join(" / ", distintos), 30, "Telefone cortado em 30 caracteres.", aviso);
    }

    private static string? Email(string? email, Action<string> aviso)
    {
        if (email is null) return null;
        if (Campos.EmailValido(email)) return email;

        aviso("E-mail inválido: ignorado.");
        return null;
    }

    private static bool EstaAtivo(LinhaDaPlanilha l, Action<string> aviso)
    {
        var valor = Normalizar(l.Valor(ColInativo));
        switch (valor)
        {
            case "SIM":
                return false;
            case null:
            case "NAO":
                return true;
            default:
                aviso("Valor de 'Inativo' não reconhecido: importado como ativo.");
                return true;
        }
    }

    private static string? Cortar(string? valor, int maximo, string motivo, Action<string> aviso)
    {
        if (valor is null || valor.Length <= maximo) return valor;

        aviso(motivo);
        return valor[..maximo].TrimEnd();
    }

    /// <summary>Maiúsculas e sem acentos, para comparar rótulos.</summary>
    internal static string? Normalizar(string? texto)
    {
        if (texto is null) return null;

        var decomposto = texto.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var semAcento = new string(decomposto.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
            != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return semAcento.ToUpperInvariant();
    }
}
