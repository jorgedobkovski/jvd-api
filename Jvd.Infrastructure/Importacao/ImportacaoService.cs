using System.Security.Cryptography;
using Jvd.Application.Comum;
using Jvd.Application.Importacao;
using Jvd.Domain;
using Jvd.Infrastructure.Cadastros;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jvd.Infrastructure.Importacao;

public sealed class ImportacaoService(
    JvdDbContext db, ILeitorDePlanilha leitor, TimeProvider relogio, ILogger<ImportacaoService> log) : IImportacaoService
{
    public const int LimiteDoArquivoEmBytes = 10 * 1024 * 1024;
    private const int TamanhoMaximoDaPagina = 100;

    public Task<Resultado<RelatorioDeImportacao>> ImportarClientesAsync(
        Stream arquivo, string nomeArquivo, bool simular, Guid atorId, CancellationToken ct = default) =>
        ImportarAsync(TipoImportacao.Clientes, arquivo, nomeArquivo, simular, atorId, ct);

    public Task<Resultado<RelatorioDeImportacao>> ImportarServicosAsync(
        Stream arquivo, string nomeArquivo, bool simular, Guid atorId, CancellationToken ct = default) =>
        ImportarAsync(TipoImportacao.Servicos, arquivo, nomeArquivo, simular, atorId, ct);

    // ---------- fluxo comum ----------

    private async Task<Resultado<RelatorioDeImportacao>> ImportarAsync(
        TipoImportacao tipo, Stream arquivo, string nomeArquivo, bool simular, Guid atorId, CancellationToken ct)
    {
        var bytes = await LerLimitadoAsync(arquivo, ct);
        if (bytes is null) return Falha(CodigosDeErro.Validacao, "O arquivo excede o tamanho máximo de 10 MB.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (await db.Importacoes.AsNoTracking().AnyAsync(i => i.Tipo == tipo && i.HashArquivo == hash, ct))
        {
            return Falha(CodigosDeErro.ArquivoJaImportado, "Este arquivo já foi importado.");
        }

        IReadOnlyList<LinhaDaPlanilha> linhas;
        try
        {
            using var memoria = new MemoryStream(bytes, writable: false);
            linhas = leitor.Ler(memoria);
        }
        catch (PlanilhaInvalidaException ex)
        {
            return Falha(CodigosDeErro.Validacao, ex.Message);
        }

        var preparo = tipo == TipoImportacao.Clientes
            ? PrepararClientes(linhas)
            : await PrepararServicosAsync(linhas, ct);
        if (preparo.Falha is not null) return Resultado<RelatorioDeImportacao>.Falha(preparo.Falha);

        var ocorrencias = preparo.Ocorrencias.OrderBy(o => o.Linha).ThenBy(o => o.Gravidade).ToList();
        var rejeitados = ocorrencias.Count(o => o.Gravidade == GravidadeOcorrencia.Erro);
        var avisos = ocorrencias.Count - rejeitados;
        var agora = relogio.GetUtcNow().UtcDateTime;

        var relatorio = new RelatorioDeImportacao(
            null, tipo, simular, preparo.Lidos, preparo.Importados, rejeitados, avisos, preparo.Ignoradas, ocorrencias);
        if (simular) return Resultado<RelatorioDeImportacao>.Ok(relatorio);

        var importacao = new Domain.Importacao
        {
            Id = Guid.NewGuid(), Tipo = tipo, NomeArquivo = NomeSeguro(nomeArquivo), HashArquivo = hash, UsuarioId = atorId,
            ExecutadaEm = agora, TotalLidos = preparo.Lidos, TotalImportados = preparo.Importados,
            TotalRejeitados = rejeitados, TotalAvisos = avisos, TotalIgnoradas = preparo.Ignoradas
        };

        try
        {
            await using var transacao = await db.Database.BeginTransactionAsync(ct);
            db.Importacoes.Add(importacao);
            preparo.Gravar(agora);
            db.ImportacoesErros.AddRange(ocorrencias.Select(o => new ImportacaoErro
            {
                Id = Guid.NewGuid(), ImportacaoId = importacao.Id, Linha = o.Linha, Gravidade = o.Gravidade, Motivo = o.Motivo
            }));
            await db.SaveChangesAsync(ct);
            await transacao.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (Concorrencia.EhChaveDuplicada(ex))
        {
            // Dois envios do mesmo arquivo ao mesmo tempo: o outro venceu (índice único tipo + hash).
            return Falha(CodigosDeErro.ArquivoJaImportado, "Este arquivo já foi importado.");
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado<RelatorioDeImportacao>.Falha(Concorrencia.Conflito());
        }

        log.LogInformation(
            "Importação {ImportacaoId} ({Tipo}) por {AtorId}: {Lidos} lidos, {Importados} importados, {Rejeitados} rejeitados, {Avisos} avisos.",
            importacao.Id, tipo, atorId, preparo.Lidos, preparo.Importados, rejeitados, avisos);
        return Resultado<RelatorioDeImportacao>.Ok(relatorio with { ImportacaoId = importacao.Id });
    }

    // ---------- clientes ----------

    private Preparo PrepararClientes(IReadOnlyList<LinhaDaPlanilha> linhas)
    {
        var interpretacao = InterpretadorDeClientes.Interpretar(linhas);
        if (!interpretacao.EstruturaValida)
        {
            return Preparo.Invalido("A planilha não tem o formato esperado de clientes (Razão Social / Nome, CNPJ/CPF e Endereço).");
        }

        if (interpretacao.Lidos == 0) return Preparo.Invalido("Nenhum cliente foi encontrado na planilha.");

        return new Preparo(null, interpretacao.Ocorrencias.ToList(), interpretacao.Lidos, interpretacao.Clientes.Count,
            interpretacao.Ignoradas, agora =>
            {
                foreach (var item in interpretacao.Clientes)
                {
                    var d = item.Dados;
                    db.Clientes.Add(new Cliente
                    {
                        Id = Guid.NewGuid(), NomeRazaoSocial = d.NomeRazaoSocial, NomeFantasia = d.NomeFantasia,
                        Documento = d.Documento, InscricaoEstadualRg = d.InscricaoEstadualRg, Logradouro = d.Logradouro,
                        Numero = d.Numero, Bairro = d.Bairro, Cep = d.Cep, Cidade = d.Cidade, Uf = d.Uf,
                        Telefone = d.Telefone, Email = d.Email, CadastroIncompleto = d.Documento is null,
                        Origem = OrigemCadastro.Importado, Ativo = item.Ativo, CriadoEm = agora, AtualizadoEm = agora
                    });
                }
            });
    }

    // ---------- catálogo de serviços ----------

    private async Task<Preparo> PrepararServicosAsync(IReadOnlyList<LinhaDaPlanilha> linhas, CancellationToken ct)
    {
        var interpretacao = InterpretadorDeServicos.Interpretar(linhas);
        if (!interpretacao.EstruturaValida)
        {
            return Preparo.Invalido("A planilha não tem o formato esperado de mercadorias (Descrição e Preço R$).");
        }

        if (!interpretacao.ViuGrupoDeServicos) return Preparo.Invalido("O grupo SERVIÇOS não foi encontrado na planilha.");

        // O catálogo é pequeno: conferir os códigos já cadastrados é mais simples (e sem limite de parâmetros) do que filtrar no banco.
        var existentes = (await db.Catalogo.AsNoTracking().Where(s => s.CodigoOrigem != null)
            .Select(s => s.CodigoOrigem!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ocorrencias = interpretacao.Ocorrencias.ToList();
        var aceitos = new List<ServicoParaImportar>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in interpretacao.Servicos)
        {
            var codigo = item.Dados.CodigoOrigem;
            if (codigo is not null && existentes.Contains(codigo))
            {
                ocorrencias.Add(new Ocorrencia(item.Linha, GravidadeOcorrencia.Erro, "Código de origem já cadastrado no catálogo: não importado."));
            }
            else if (codigo is not null && !vistos.Add(codigo))
            {
                ocorrencias.Add(new Ocorrencia(item.Linha, GravidadeOcorrencia.Erro, "Código de origem repetido na planilha: não importado."));
            }
            else
            {
                aceitos.Add(item);
            }
        }

        return new Preparo(null, ocorrencias, interpretacao.Lidos, aceitos.Count, interpretacao.Ignoradas, agora =>
        {
            foreach (var item in aceitos)
            {
                db.Catalogo.Add(new ServicoCatalogo
                {
                    Id = Guid.NewGuid(), CodigoOrigem = item.Dados.CodigoOrigem, Descricao = item.Dados.Descricao,
                    PrecoSugerido = item.Dados.PrecoSugerido, Ativo = item.Ativo, CriadoEm = agora, AtualizadoEm = agora
                });
            }
        });
    }

    // ---------- histórico ----------

    public async Task<PaginaDeResultados<ImportacaoResumo>> ListarAsync(int pagina, int tamanho, CancellationToken ct = default)
    {
        pagina = Math.Max(1, pagina);
        tamanho = Math.Clamp(tamanho, 1, TamanhoMaximoDaPagina);

        var consulta = db.Importacoes.AsNoTracking();
        var total = await consulta.CountAsync(ct);
        var itens = await consulta.OrderByDescending(i => i.ExecutadaEm).ThenBy(i => i.Id)
            .Skip((pagina - 1) * tamanho).Take(tamanho).ToListAsync(ct);

        return new PaginaDeResultados<ImportacaoResumo>(itens.Select(Resumo).ToList(), pagina, tamanho, total);
    }

    public async Task<Resultado<ImportacaoDetalhe>> ObterAsync(Guid id, CancellationToken ct = default)
    {
        var importacao = await db.Importacoes.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (importacao is null)
        {
            return Resultado<ImportacaoDetalhe>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Importação não encontrada."));
        }

        var ocorrencias = await db.ImportacoesErros.AsNoTracking().Where(e => e.ImportacaoId == id)
            .OrderBy(e => e.Linha).ThenBy(e => e.Gravidade)
            .Select(e => new Ocorrencia(e.Linha, e.Gravidade, e.Motivo)).ToListAsync(ct);

        return Resultado<ImportacaoDetalhe>.Ok(new ImportacaoDetalhe(Resumo(importacao), ocorrencias));
    }

    // ---------- apoio ----------

    /// <summary>Resultado da leitura: o que falhou, as ocorrências e como gravar. Nada é gravado até <see cref="Gravar"/>.</summary>
    private sealed class Preparo(
        ErroNegocio? falha, List<Ocorrencia> ocorrencias, int lidos, int importados, int ignoradas, Action<DateTime>? gravar)
    {
        public ErroNegocio? Falha { get; } = falha;
        public List<Ocorrencia> Ocorrencias { get; } = ocorrencias;
        public int Lidos { get; } = lidos;
        public int Importados { get; } = importados;
        public int Ignoradas { get; } = ignoradas;
        public void Gravar(DateTime agora) => gravar?.Invoke(agora);

        public static Preparo Invalido(string mensagem) =>
            new(new ErroNegocio(CodigosDeErro.Validacao, mensagem), [], 0, 0, 0, null);
    }

    private static async Task<byte[]?> LerLimitadoAsync(Stream arquivo, CancellationToken ct)
    {
        using var memoria = new MemoryStream();
        var buffer = new byte[81920];
        int lidos;
        while ((lidos = await arquivo.ReadAsync(buffer, ct)) > 0)
        {
            if (memoria.Length + lidos > LimiteDoArquivoEmBytes) return null;
            memoria.Write(buffer, 0, lidos);
        }

        return memoria.ToArray();
    }

    /// <summary>Só o nome do arquivo (sem pasta nem caracteres de controle), limitado a 200 caracteres.</summary>
    internal static string NomeSeguro(string? nome)
    {
        var apenasNome = Path.GetFileName((nome ?? "").Replace('\\', '/'));
        var limpo = new string(apenasNome.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (limpo.Length == 0) limpo = "arquivo.xlsx";
        return limpo.Length > 200 ? limpo[..200] : limpo;
    }

    private static Resultado<RelatorioDeImportacao> Falha(string codigo, string mensagem) =>
        Resultado<RelatorioDeImportacao>.Falha(new ErroNegocio(codigo, mensagem));

    private static ImportacaoResumo Resumo(Domain.Importacao i) => new(
        i.Id, i.Tipo, i.NomeArquivo, i.UsuarioId, i.ExecutadaEm, i.TotalLidos, i.TotalImportados, i.TotalRejeitados,
        i.TotalAvisos, i.TotalIgnoradas);
}
