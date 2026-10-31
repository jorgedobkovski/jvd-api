using Jvd.Application.Importacao;
using Jvd.Domain;
using Jvd.Tests.Suporte;

namespace Jvd.Tests.Importacao;

/// <summary>Regras de leitura da planilha de clientes (Specs/IMPORTACAO.md). Sem banco; dados 100% sintéticos.</summary>
public class InterpretadorDeClientesTests
{
    private const string Cnpj = "11222333000181";

    private static LinhaDaPlanilha Linha(int numero, IReadOnlyDictionary<string, string> celulas) => new(numero, celulas);

    private static InterpretacaoDeClientes Interpretar(params IReadOnlyDictionary<string, string>[] clientes)
    {
        var todas = new IReadOnlyDictionary<string, string>[]
            { new Dictionary<string, string> { ["A"] = "Título do relatório" }, PlanilhaSintetica.Vazia(), PlanilhaSintetica.CabecalhoDeClientes() }
            .Concat(clientes).Select((c, i) => Linha(i + 1, c));
        return InterpretadorDeClientes.Interpretar(todas);
    }

    private static string[] Motivos(InterpretacaoDeClientes r, int linha) =>
        r.Ocorrencias.Where(o => o.Linha == linha).Select(o => o.Motivo).ToArray();

    // linha 4 = primeiro cliente (título, vazia, cabeçalho)
    private const int PrimeiraLinhaDeCliente = 4;

    [Fact]
    public void ClientePadrao_MapeiaTodosOsCampos_EEstaAtivo()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("Mercado Sintético Ltda", Cnpj,
            PlanilhaSintetica.Endereco("RUA DAS FLORES", "123", "CENTRO", "SALA 2", "PORTO VELHO", "ro"),
            fantasia: "Mercadinho", inscricao: "000123", fone: "6933334444", email: "contato@exemplo.invalid"));

        Assert.True(r.EstruturaValida);
        Assert.Equal(1, r.Lidos);
        var item = Assert.Single(r.Clientes);
        var d = item.Dados;
        Assert.Equal("Mercado Sintético Ltda", d.NomeRazaoSocial);
        Assert.Equal("Mercadinho", d.NomeFantasia);
        Assert.Equal(Cnpj, d.Documento);
        Assert.Equal("000123", d.InscricaoEstadualRg);
        Assert.Equal("RUA DAS FLORES, SALA 2", d.Logradouro);   // complemento anexado ao logradouro
        Assert.Equal("123", d.Numero);
        Assert.Equal("CENTRO", d.Bairro);
        Assert.Equal("PORTO VELHO", d.Cidade);
        Assert.Equal("RO", d.Uf);
        Assert.Null(d.Cep);
        Assert.Equal("6933334444", d.Telefone);
        Assert.Equal("contato@exemplo.invalid", d.Email);
        Assert.True(item.Ativo);
        Assert.Empty(r.Ocorrencias);
    }

    [Fact]
    public void Complemento_VazioNaoAnexaNada_ESeForCepViraCep()
    {
        var r = Interpretar(
            PlanilhaSintetica.Cliente("A", Cnpj, PlanilhaSintetica.Endereco("RUA X", "1", "B", "", "CIDADE", "RO")),
            PlanilhaSintetica.Cliente("B", Cnpj, PlanilhaSintetica.Endereco("RUA Y", "2", "B", "76.800-000", "CIDADE", "RO")),
            PlanilhaSintetica.Cliente("C", Cnpj, PlanilhaSintetica.Endereco("RUA Z", "3", "B", "76800 123", "CIDADE", "RO")));

        Assert.Equal("RUA X", r.Clientes[0].Dados.Logradouro);
        Assert.Null(r.Clientes[0].Dados.Cep);
        Assert.Equal("RUA Y", r.Clientes[1].Dados.Logradouro);
        Assert.Equal("76800000", r.Clientes[1].Dados.Cep);
        // "76800 123" tem 8 dígitos e espaço: é CEP com pontuação irregular; "APTO 12" nunca é CEP
        Assert.Equal("76800123", r.Clientes[2].Dados.Cep);
    }

    [Fact]
    public void ComplementoComLetrasENumeros_NaoEConfundidoComCep()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("A", Cnpj, PlanilhaSintetica.Endereco("RUA X", "1", "B", "APTO 12345678", "CIDADE", "RO")));

        Assert.Null(r.Clientes[0].Dados.Cep);
        Assert.Equal("RUA X, APTO 12345678", r.Clientes[0].Dados.Logradouro);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(1)]
    public void EnderecoForaDoPadrao_VaiInteiroNoLogradouro_ComAviso(int segmentos)
    {
        var endereco = string.Join(" - ", Enumerable.Range(1, segmentos).Select(i => "SEG" + i));

        var r = Interpretar(PlanilhaSintetica.Cliente("A", Cnpj, endereco));

        var d = Assert.Single(r.Clientes).Dados;
        Assert.Equal(endereco, d.Logradouro);
        Assert.Null(d.Numero);
        Assert.Null(d.Bairro);
        Assert.Null(d.Cidade);
        Assert.Null(d.Uf);
        Assert.Contains("Endereço fora do padrão: importado inteiro no logradouro; revisar.", Motivos(r, PrimeiraLinhaDeCliente));
    }

    [Fact]
    public void EnderecoForaDoPadraoMuitoLongo_ECortadoEm150_ComAviso()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("A", Cnpj, new string('x', 400)));

        Assert.Equal(150, r.Clientes[0].Dados.Logradouro!.Length);
        Assert.Contains("Endereço cortado em 150 caracteres.", Motivos(r, PrimeiraLinhaDeCliente));
    }

    [Fact]
    public void SemDocumento_ImportaComoIncompleto_ComAviso()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("Sem Documento", null));

        Assert.Null(r.Clientes[0].Dados.Documento);
        Assert.Contains("Sem CPF/CNPJ: importado como cadastro incompleto.", Motivos(r, PrimeiraLinhaDeCliente));
    }

    [Fact]
    public void DocumentoInvalido_ImportaSemDocumento_ComAviso_SemEcoarOValor()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("Invalido", "12345678000199"));

        Assert.Null(r.Clientes[0].Dados.Documento);
        var motivos = Motivos(r, PrimeiraLinhaDeCliente);
        Assert.Contains("CPF/CNPJ inválido: importado sem documento, como cadastro incompleto.", motivos);
        Assert.All(motivos, m => Assert.DoesNotContain("12345678000199", m));
    }

    [Fact]
    public void DocumentoComMascara_EhNormalizado()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("Mascara", "11.222.333/0001-81"));

        Assert.Equal(Cnpj, r.Clientes[0].Dados.Documento);
    }

    [Fact]
    public void Inativo_SIM_ImportaInativo_NAOeVazioAtivo_OutroValorAtivoComAviso()
    {
        var r = Interpretar(
            PlanilhaSintetica.Cliente("A", Cnpj, inativo: "SIM"),
            PlanilhaSintetica.Cliente("B", Cnpj, inativo: "NÃO"),
            PlanilhaSintetica.Cliente("C", Cnpj, inativo: "nao"),
            PlanilhaSintetica.Cliente("D", Cnpj, inativo: "TALVEZ"));

        Assert.Equal([false, true, true, true], r.Clientes.Select(c => c.Ativo).ToArray());
        Assert.Contains("Valor de 'Inativo' não reconhecido: importado como ativo.", Motivos(r, PrimeiraLinhaDeCliente + 3));
        Assert.Empty(Motivos(r, PrimeiraLinhaDeCliente));
    }

    [Fact]
    public void EmailInvalido_EIgnorado_ComAviso_SemEcoar()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("A", Cnpj, email: "isto-nao-e-email"));

        Assert.Null(r.Clientes[0].Dados.Email);
        var motivos = Motivos(r, PrimeiraLinhaDeCliente);
        Assert.Contains("E-mail inválido: ignorado.", motivos);
        Assert.All(motivos, m => Assert.DoesNotContain("isto-nao-e-email", m));
    }

    [Fact]
    public void UfInvalida_EIgnorada_ComAviso()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("A", Cnpj, PlanilhaSintetica.Endereco(uf: "ZZ")));

        Assert.Null(r.Clientes[0].Dados.Uf);
        Assert.Contains("UF inválida: ignorada.", Motivos(r, PrimeiraLinhaDeCliente));
    }

    [Fact]
    public void Telefones_DistintosSaoUnidos_IguaisNao_EAcimaDe30Corta()
    {
        var comTres = PlanilhaSintetica.Cliente("A", Cnpj, fone: "111");
        comTres["J"] = "222";
        comTres["K"] = "111";
        var longo = PlanilhaSintetica.Cliente("B", Cnpj, fone: new string('9', 20));
        longo["K"] = new string('8', 20);

        var r = Interpretar(comTres, longo);

        Assert.Equal("111 / 222", r.Clientes[0].Dados.Telefone);
        Assert.Equal(30, r.Clientes[1].Dados.Telefone!.Length);
        Assert.Contains("Telefone cortado em 30 caracteres.", Motivos(r, PrimeiraLinhaDeCliente + 1));
    }

    [Fact]
    public void TextosAcimaDoLimite_SaoCortadosComAviso_NuncaDescartamOCliente()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente(new string('n', 250), Cnpj, fantasia: new string('f', 250), inscricao: new string('i', 40)));

        var d = Assert.Single(r.Clientes).Dados;
        Assert.Equal(200, d.NomeRazaoSocial.Length);
        Assert.Equal(200, d.NomeFantasia!.Length);
        Assert.Equal(30, d.InscricaoEstadualRg!.Length);
        Assert.Equal(3, Motivos(r, PrimeiraLinhaDeCliente).Length);
    }

    [Fact]
    public void CabecalhosRepetidos_TitulosRodapeELinhasEmBranco_SaoIgnorados_ESoOsClientesSaoLidos()
    {
        var ruido = new Dictionary<string, string> { ["E"] = "Página 2 de 9" };
        var r = Interpretar(
            PlanilhaSintetica.Cliente("Um", Cnpj),
            PlanilhaSintetica.CabecalhoDeClientes(),
            PlanilhaSintetica.Vazia(),
            PlanilhaSintetica.Cliente("Dois", Cnpj),
            ruido,
            new Dictionary<string, string> { ["A"] = "Total de registros: 2" });

        Assert.Equal(2, r.Lidos);
        Assert.Equal(["Um", "Dois"], r.Clientes.Select(c => c.Dados.NomeRazaoSocial).ToArray());
        // título + cabeçalho repetido + cabeçalho inicial + ruído + rodapé (a linha em branco não conta)
        Assert.Equal(5, r.Ignoradas);
    }

    [Fact]
    public void LinhasAntesDoPrimeiroCabecalho_NuncaViramCliente()
    {
        var linhas = new[]
        {
            Linha(1, new Dictionary<string, string> { ["A"] = "Empresa X", ["H"] = "endereço do relatório" }),
            Linha(2, PlanilhaSintetica.CabecalhoDeClientes()),
            Linha(3, PlanilhaSintetica.Cliente("Real", Cnpj))
        };

        var r = InterpretadorDeClientes.Interpretar(linhas);

        Assert.Equal(["Real"], r.Clientes.Select(c => c.Dados.NomeRazaoSocial).ToArray());
    }

    [Fact]
    public void ArquivoSemCabecalhoDeClientes_TemEstruturaInvalida()
    {
        var r = InterpretadorDeClientes.Interpretar([Linha(1, PlanilhaSintetica.CabecalhoDeMercadorias())]);

        Assert.False(r.EstruturaValida);
        Assert.Empty(r.Clientes);
    }

    [Fact]
    public void CabecalhoComOutraCaixaESemAcento_AindaEReconhecido()
    {
        var cabecalho = new Dictionary<string, string> { ["A"] = "RAZAO SOCIAL / NOME", ["F"] = "cnpj/cpf", ["H"] = "endereco" };

        var r = InterpretadorDeClientes.Interpretar([Linha(1, cabecalho), Linha(2, PlanilhaSintetica.Cliente("X", Cnpj))]);

        Assert.True(r.EstruturaValida);
        Assert.Single(r.Clientes);
    }

    [Fact]
    public void OsMotivosSaoSempreTextoFixo_NuncaTrazemConteudoDaLinha()
    {
        var r = Interpretar(PlanilhaSintetica.Cliente("NOME-SECRETO", "00000000000", "ENDERECO-SECRETO-1 - 2 - 3", email: "segredo@invalido"));

        Assert.NotEmpty(r.Ocorrencias);
        Assert.All(r.Ocorrencias, o =>
        {
            Assert.DoesNotContain("SECRETO", o.Motivo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("segredo", o.Motivo, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(GravidadeOcorrencia.Aviso, o.Gravidade);
        });
    }
}
