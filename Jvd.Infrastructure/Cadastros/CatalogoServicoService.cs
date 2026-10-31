using Jvd.Application.Catalogo;
using Jvd.Application.Comum;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jvd.Infrastructure.Cadastros;

public sealed class CatalogoServicoService(JvdDbContext db, TimeProvider relogio, ILogger<CatalogoServicoService> log)
    : ICatalogoServicoService
{
    private const string Entidade = "catalogo_servico";
    private const int TamanhoMaximoDaPagina = 100;
    private const int MaximoDeIdsNoAviso = 5;
    private const decimal PrecoMaximo = 9_999_999_999.99m;

    // ---------- consulta ----------

    public async Task<PaginaDeResultados<ServicoResumo>> ListarAsync(
        FiltroDeServicos filtro, bool incluirInativos, CancellationToken ct = default)
    {
        var pagina = Math.Max(1, filtro.Pagina);
        var tamanho = Math.Clamp(filtro.Tamanho, 1, TamanhoMaximoDaPagina);

        var consulta = db.Catalogo.AsNoTracking();

        // Quem não pode ver inativos NUNCA os recebe, qualquer que seja o filtro pedido.
        if (!incluirInativos) consulta = consulta.Where(s => s.Ativo);
        else if (filtro.Ativo is { } ativo) consulta = consulta.Where(s => s.Ativo == ativo);

        if (Campos.Normalizar(filtro.Busca) is { } busca)
        {
            consulta = consulta.Where(s => s.Descricao.Contains(busca) || (s.CodigoOrigem != null && s.CodigoOrigem.Contains(busca)));
        }

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderBy(s => s.Descricao).ThenBy(s => s.Id)
            .Skip((pagina - 1) * tamanho).Take(tamanho)
            .ToListAsync(ct);

        return new PaginaDeResultados<ServicoResumo>(itens.Select(Resumo).ToList(), pagina, tamanho, total);
    }

    public async Task<Resultado<ServicoResumo>> ObterAsync(Guid id, bool incluirInativos, CancellationToken ct = default)
    {
        var servico = await db.Catalogo.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        return servico is null || (!servico.Ativo && !incluirInativos)
            ? NaoEncontrado<ServicoResumo>()
            : Resultado<ServicoResumo>.Ok(Resumo(servico));
    }

    // ---------- escrita ----------

    public async Task<Resultado<ServicoSalvo>> CriarAsync(DadosDoServico dados, Guid atorId, CancellationToken ct = default)
    {
        var (erros, d) = Validar(dados);
        if (erros.Count > 0) return Resultado<ServicoSalvo>.Falha(erros);

        var agora = relogio.GetUtcNow().UtcDateTime;
        var servico = new ServicoCatalogo { Id = Guid.NewGuid(), Ativo = true, CriadoEm = agora, AtualizadoEm = agora };
        Aplicar(servico, d);

        db.Catalogo.Add(servico);
        RegistrarEvento(TipoEventoCadastro.Criado, servico.Id, atorId, agora, string.Join(',', CamposPreenchidos(servico)));
        await db.SaveChangesAsync(ct);

        log.LogInformation("Serviço do catálogo {ServicoId} criado por {AtorId}.", servico.Id, atorId);
        return Resultado<ServicoSalvo>.Ok(new ServicoSalvo(Resumo(servico), await AvisosAsync(servico, ct)));
    }

    public async Task<Resultado<ServicoSalvo>> AtualizarAsync(
        Guid id, DadosDoServico dados, string versao, Guid atorId, CancellationToken ct = default)
    {
        if (!Versao.TentarDecodificar(versao, out var versaoRecebida))
        {
            return Resultado<ServicoSalvo>.Falha(Validacao("Versão do cadastro inválida."));
        }

        var (erros, d) = Validar(dados);
        if (erros.Count > 0) return Resultado<ServicoSalvo>.Falha(erros);

        // Só o gestor edita (política do endpoint) e o gestor enxerga inativos: não há filtro de visibilidade aqui.
        var servico = await db.Catalogo.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (servico is null) return NaoEncontrado<ServicoSalvo>();

        var alterados = CamposAlterados(servico, d);
        if (alterados.Count == 0) return Resultado<ServicoSalvo>.Ok(new ServicoSalvo(Resumo(servico), []));

        Aplicar(servico, d);
        var agora = relogio.GetUtcNow().UtcDateTime;
        servico.AtualizadoEm = agora;

        db.Entry(servico).Property(s => s.Versao).OriginalValue = versaoRecebida;
        RegistrarEvento(TipoEventoCadastro.Editado, servico.Id, atorId, agora, string.Join(',', alterados));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado<ServicoSalvo>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("cadastro")));
        }

        var avisos = alterados.Contains("descricao") ? await AvisosAsync(servico, ct) : [];
        return Resultado<ServicoSalvo>.Ok(new ServicoSalvo(Resumo(servico), avisos));
    }

    public Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default) =>
        AlterarAtivoAsync(id, atorId, ativo: false, ct);

    public Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default) =>
        AlterarAtivoAsync(id, atorId, ativo: true, ct);

    private async Task<Resultado> AlterarAtivoAsync(Guid id, Guid atorId, bool ativo, CancellationToken ct)
    {
        var servico = await db.Catalogo.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (servico is null) return Resultado.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Serviço não encontrado."));
        if (servico.Ativo == ativo) return Resultado.Ok(); // idempotente

        var agora = relogio.GetUtcNow().UtcDateTime;
        servico.Ativo = ativo;
        servico.AtualizadoEm = agora;
        RegistrarEvento(ativo ? TipoEventoCadastro.Reativado : TipoEventoCadastro.Desativado, id, atorId, agora, "ativo");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("cadastro")));
        }

        log.LogInformation("Serviço do catálogo {ServicoId} {Acao} por {AtorId}.", id, ativo ? "reativado" : "desativado", atorId);
        return Resultado.Ok();
    }

    // ---------- validação e normalização ----------

    private static (List<ErroNegocio> Erros, DadosDoServico Dados) Validar(DadosDoServico dados)
    {
        var erros = new List<ErroNegocio>();

        var descricao = Campos.Normalizar(dados.Descricao);
        if (descricao is null || Campos.Excede(descricao, 200)) erros.Add(Validacao("A descrição é obrigatória (até 200 caracteres)."));

        var codigo = Campos.Normalizar(dados.CodigoOrigem);
        if (Campos.Excede(codigo, 40)) erros.Add(Validacao("Código de origem: máximo de 40 caracteres."));

        var preco = dados.PrecoSugerido;
        if (preco is { } p && (p < 0 || p > PrecoMaximo || decimal.Round(p, 2) != p))
        {
            erros.Add(Validacao("Preço sugerido inválido (zero ou mais, até 2 casas decimais)."));
        }

        return (erros, new DadosDoServico(descricao ?? "", codigo, preco));
    }

    private static void Aplicar(ServicoCatalogo s, DadosDoServico d)
    {
        s.Descricao = d.Descricao;
        s.CodigoOrigem = d.CodigoOrigem;
        s.PrecoSugerido = d.PrecoSugerido;
    }

    /// <summary>Nomes (nunca valores) dos campos que mudariam com os dados informados.</summary>
    private static List<string> CamposAlterados(ServicoCatalogo s, DadosDoServico d)
    {
        var alterados = new List<string>();
        if (!string.Equals(s.Descricao, d.Descricao, StringComparison.Ordinal)) alterados.Add("descricao");
        if (!string.Equals(s.CodigoOrigem, d.CodigoOrigem, StringComparison.Ordinal)) alterados.Add("codigo_origem");
        if (s.PrecoSugerido != d.PrecoSugerido) alterados.Add("preco_sugerido");
        return alterados;
    }

    private static IEnumerable<string> CamposPreenchidos(ServicoCatalogo s)
    {
        yield return "descricao";
        if (s.CodigoOrigem is not null) yield return "codigo_origem";
        if (s.PrecoSugerido is not null) yield return "preco_sugerido";
    }

    // ---------- apoio ----------

    private async Task<IReadOnlyList<Aviso>> AvisosAsync(ServicoCatalogo servico, CancellationToken ct)
    {
        var outros = await db.Catalogo.AsNoTracking()
            .Where(s => s.Descricao == servico.Descricao && s.Id != servico.Id)
            .OrderBy(s => s.CriadoEm).Select(s => s.Id)
            .Take(MaximoDeIdsNoAviso).ToListAsync(ct);

        return outros.Count == 0
            ? []
            : [new Aviso("catalogo.descricao_repetida", "Já existe outro serviço com esta descrição.", outros)];
    }

    private void RegistrarEvento(TipoEventoCadastro tipo, Guid entidadeId, Guid atorId, DateTime quando, string? campos) =>
        db.EventosDeCadastro.Add(new EventoDeCadastro
        {
            Id = Guid.NewGuid(), Entidade = Entidade, EntidadeId = entidadeId,
            Tipo = tipo, AtorId = atorId, OcorridoEm = quando, Campos = campos
        });

    private static ErroNegocio Validacao(string mensagem) => new(CodigosDeErro.Validacao, mensagem);

    private static Resultado<T> NaoEncontrado<T>() =>
        Resultado<T>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Serviço não encontrado."));

    private static ServicoResumo Resumo(ServicoCatalogo s) => new(
        s.Id, s.CodigoOrigem, s.Descricao, s.PrecoSugerido, s.Ativo, s.CriadoEm, s.AtualizadoEm, Versao.Codificar(s.Versao));
}
