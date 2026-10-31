using System.Data;
using Jvd.Application.Comum;
using Jvd.Application.Equipamentos;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jvd.Infrastructure.Cadastros;

public sealed class EquipamentoService(JvdDbContext db, TimeProvider relogio, ILogger<EquipamentoService> log) : IEquipamentoService
{
    private const string Entidade = "equipamento";
    private const int TamanhoMaximoDaPagina = 100;
    private const int MaximoDeIdsNoAviso = 5;

    // ---------- consulta ----------

    public async Task<PaginaDeResultados<EquipamentoResumo>> ListarAsync(
        FiltroDeEquipamentos filtro, bool incluirInativos, CancellationToken ct = default)
    {
        var pagina = Math.Max(1, filtro.Pagina);
        var tamanho = Math.Clamp(filtro.Tamanho, 1, TamanhoMaximoDaPagina);

        var consulta = Juntar(db.Equipamentos.AsNoTracking());

        // Quem não pode ver inativos nunca os recebe: nem equipamento inativo, nem de cliente inativo.
        if (!incluirInativos) consulta = consulta.Where(x => x.Equipamento.Ativo && x.Cliente.Ativo);
        else if (filtro.Ativo is { } ativo) consulta = consulta.Where(x => x.Equipamento.Ativo == ativo);

        if (filtro.ClienteId is { } clienteId) consulta = consulta.Where(x => x.Equipamento.ClienteId == clienteId);

        if (Campos.Normalizar(filtro.Busca) is { } busca)
        {
            consulta = consulta.Where(x =>
                x.Equipamento.Descricao.Contains(busca)
                || (x.Equipamento.NumeroSerie != null && x.Equipamento.NumeroSerie.Contains(busca))
                || (x.Equipamento.NumeroInmetro != null && x.Equipamento.NumeroInmetro.Contains(busca))
                || x.Cliente.NomeRazaoSocial.Contains(busca));
        }

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderBy(x => x.Cliente.NomeRazaoSocial).ThenBy(x => x.Equipamento.Descricao).ThenBy(x => x.Equipamento.Id)
            .Skip((pagina - 1) * tamanho).Take(tamanho)
            .ToListAsync(ct);

        return new PaginaDeResultados<EquipamentoResumo>(itens.Select(x => Resumo(x.Equipamento, x.Cliente.NomeRazaoSocial)).ToList(), pagina, tamanho, total);
    }

    public async Task<Resultado<EquipamentoResumo>> ObterAsync(Guid id, bool incluirInativos, CancellationToken ct = default)
    {
        var item = await Juntar(db.Equipamentos.AsNoTracking()).SingleOrDefaultAsync(x => x.Equipamento.Id == id, ct);
        return item is null || (!incluirInativos && !(item.Equipamento.Ativo && item.Cliente.Ativo))
            ? NaoEncontrado<EquipamentoResumo>()
            : Resultado<EquipamentoResumo>.Ok(Resumo(item.Equipamento, item.Cliente.NomeRazaoSocial));
    }

    // ---------- escrita ----------

    public async Task<Resultado<EquipamentoSalvo>> CriarAsync(DadosDoEquipamento dados, Guid atorId, CancellationToken ct = default)
    {
        var (erros, d) = Validar(dados);
        if (erros.Count > 0) return Resultado<EquipamentoSalvo>.Falha(erros);

        try
        {
            // Serializable: "o cliente está ativo" e a gravação do equipamento valem como uma unidade; sem isso, um
            // gestor desativando o cliente no meio do caminho deixaria o equipamento preso a um cliente inativo.
            await using var transacao = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var cliente = await db.Clientes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == d.ClienteId, ct);
            if (cliente is null || !cliente.Ativo) return Resultado<EquipamentoSalvo>.Falha(ClienteInvalido());

            var agora = relogio.GetUtcNow().UtcDateTime;
            var equipamento = new Equipamento { Id = Guid.NewGuid(), Ativo = true, CriadoEm = agora, AtualizadoEm = agora };
            Aplicar(equipamento, d);

            db.Equipamentos.Add(equipamento);
            RegistrarEvento(TipoEventoCadastro.Criado, equipamento.Id, atorId, agora, string.Join(',', CamposPreenchidos(equipamento)));
            await db.SaveChangesAsync(ct);
            await transacao.CommitAsync(ct);

            log.LogInformation("Equipamento {EquipamentoId} criado por {AtorId}.", equipamento.Id, atorId);
            return Resultado<EquipamentoSalvo>.Ok(new EquipamentoSalvo(
                Resumo(equipamento, cliente.NomeRazaoSocial), await AvisosAsync(equipamento, ct)));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado<EquipamentoSalvo>.Falha(Concorrencia.Conflito());
        }
    }

    public async Task<Resultado<EquipamentoSalvo>> AtualizarAsync(
        Guid id, DadosDoEquipamento dados, string versao, Guid atorId, bool podeEditarInativo, CancellationToken ct = default)
    {
        if (!Versao.TentarDecodificar(versao, out var versaoRecebida))
        {
            return Resultado<EquipamentoSalvo>.Falha(Validacao("Versão do cadastro inválida."));
        }

        var (erros, d) = Validar(dados);
        if (erros.Count > 0) return Resultado<EquipamentoSalvo>.Falha(erros);

        try
        {
            // Serializable: a checagem do cliente (atual e destino) e a gravação são uma unidade (ver CriarAsync).
            await using var transacao = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var equipamento = await db.Equipamentos.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (equipamento is null) return NaoEncontrado<EquipamentoSalvo>();

            var clienteAtual = await db.Clientes.AsNoTracking().SingleAsync(c => c.Id == equipamento.ClienteId, ct);

            // Quem não vê inativos também não edita: equipamento inativo OU de cliente inativo é "não encontrado".
            if (!podeEditarInativo && !(equipamento.Ativo && clienteAtual.Ativo)) return NaoEncontrado<EquipamentoSalvo>();

            var transferencia = equipamento.ClienteId != d.ClienteId;
            var cliente = transferencia
                ? await db.Clientes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == d.ClienteId, ct)
                : clienteAtual;

            // O cliente novo (transferência) precisa existir e estar ativo; manter o cliente atual não exige isso,
            // para que o gestor ainda consiga editar a balança de um cliente que foi desativado.
            if (cliente is null || (transferencia && !cliente.Ativo)) return Resultado<EquipamentoSalvo>.Falha(ClienteInvalido());

            var alterados = CamposAlterados(equipamento, d);
            if (alterados.Count == 0)
            {
                return Resultado<EquipamentoSalvo>.Ok(new EquipamentoSalvo(Resumo(equipamento, cliente.NomeRazaoSocial), []));
            }

            Aplicar(equipamento, d);
            var agora = relogio.GetUtcNow().UtcDateTime;
            equipamento.AtualizadoEm = agora;

            db.Entry(equipamento).Property(x => x.Versao).OriginalValue = versaoRecebida;
            RegistrarEvento(TipoEventoCadastro.Editado, equipamento.Id, atorId, agora, string.Join(',', alterados));

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Resultado<EquipamentoSalvo>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("cadastro")));
            }

            await transacao.CommitAsync(ct);

            var avisos = alterados.Any(c => c is "numero_serie" or "numero_inmetro")
                ? await AvisosAsync(equipamento, ct)
                : [];
            return Resultado<EquipamentoSalvo>.Ok(new EquipamentoSalvo(Resumo(equipamento, cliente.NomeRazaoSocial), avisos));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado<EquipamentoSalvo>.Falha(Concorrencia.Conflito());
        }
    }

    public Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default) =>
        AlterarAtivoAsync(id, atorId, ativo: false, ct);

    public Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default) =>
        AlterarAtivoAsync(id, atorId, ativo: true, ct);

    private async Task<Resultado> AlterarAtivoAsync(Guid id, Guid atorId, bool ativo, CancellationToken ct)
    {
        var equipamento = await db.Equipamentos.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (equipamento is null) return Resultado.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Equipamento não encontrado."));
        if (equipamento.Ativo == ativo) return Resultado.Ok(); // idempotente

        var agora = relogio.GetUtcNow().UtcDateTime;
        equipamento.Ativo = ativo;
        equipamento.AtualizadoEm = agora;
        RegistrarEvento(ativo ? TipoEventoCadastro.Reativado : TipoEventoCadastro.Desativado, id, atorId, agora, "ativo");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("cadastro")));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado.Falha(Concorrencia.Conflito());
        }

        log.LogInformation("Equipamento {EquipamentoId} {Acao} por {AtorId}.", id, ativo ? "reativado" : "desativado", atorId);
        return Resultado.Ok();
    }

    // ---------- validação e normalização ----------

    private static (List<ErroNegocio> Erros, DadosDoEquipamento Dados) Validar(DadosDoEquipamento dados)
    {
        var erros = new List<ErroNegocio>();

        var descricao = Campos.Normalizar(dados.Descricao);
        if (descricao is null || Campos.Excede(descricao, 200)) erros.Add(Validacao("A descrição é obrigatória (até 200 caracteres)."));

        string? Texto(string? valor, int maximo, string rotulo)
        {
            var normalizado = Campos.Normalizar(valor);
            if (Campos.Excede(normalizado, maximo)) erros.Add(Validacao($"{rotulo}: máximo de {maximo} caracteres."));
            return normalizado;
        }

        var serie = Texto(dados.NumeroSerie, 60, "Número de série");
        var inmetro = Texto(dados.NumeroInmetro, 40, "Número INMETRO");
        var observacoes = Texto(dados.Observacoes, 1000, "Observações");

        if (dados.ClienteId == Guid.Empty) erros.Add(Validacao("O cliente é obrigatório."));

        return (erros, new DadosDoEquipamento(dados.ClienteId, descricao ?? "", serie, inmetro, observacoes));
    }

    private static void Aplicar(Equipamento e, DadosDoEquipamento d)
    {
        e.ClienteId = d.ClienteId;
        e.Descricao = d.Descricao;
        e.NumeroSerie = d.NumeroSerie;
        e.NumeroInmetro = d.NumeroInmetro;
        e.Observacoes = d.Observacoes;
    }

    /// <summary>Nomes (nunca valores) dos campos que mudariam com os dados informados.</summary>
    private static List<string> CamposAlterados(Equipamento e, DadosDoEquipamento d)
    {
        var alterados = new List<string>();
        if (e.ClienteId != d.ClienteId) alterados.Add("cliente");
        if (!string.Equals(e.Descricao, d.Descricao, StringComparison.Ordinal)) alterados.Add("descricao");
        if (!string.Equals(e.NumeroSerie, d.NumeroSerie, StringComparison.Ordinal)) alterados.Add("numero_serie");
        if (!string.Equals(e.NumeroInmetro, d.NumeroInmetro, StringComparison.Ordinal)) alterados.Add("numero_inmetro");
        if (!string.Equals(e.Observacoes, d.Observacoes, StringComparison.Ordinal)) alterados.Add("observacoes");
        return alterados;
    }

    private static IEnumerable<string> CamposPreenchidos(Equipamento e)
    {
        yield return "cliente";
        yield return "descricao";
        if (e.NumeroSerie is not null) yield return "numero_serie";
        if (e.NumeroInmetro is not null) yield return "numero_inmetro";
        if (e.Observacoes is not null) yield return "observacoes";
    }

    // ---------- apoio ----------

    private async Task<IReadOnlyList<Aviso>> AvisosAsync(Equipamento e, CancellationToken ct)
    {
        var avisos = new List<Aviso>();

        if (e.NumeroSerie is not null)
        {
            var ids = await db.Equipamentos.AsNoTracking()
                .Where(x => x.NumeroSerie == e.NumeroSerie && x.Id != e.Id)
                .OrderBy(x => x.CriadoEm).Select(x => x.Id).Take(MaximoDeIdsNoAviso).ToListAsync(ct);
            if (ids.Count > 0) avisos.Add(new Aviso("equipamento.serie_repetida", "Já existe outro equipamento com este número de série.", ids));
        }

        if (e.NumeroInmetro is not null)
        {
            var ids = await db.Equipamentos.AsNoTracking()
                .Where(x => x.NumeroInmetro == e.NumeroInmetro && x.Id != e.Id)
                .OrderBy(x => x.CriadoEm).Select(x => x.Id).Take(MaximoDeIdsNoAviso).ToListAsync(ct);
            if (ids.Count > 0) avisos.Add(new Aviso("equipamento.inmetro_repetido", "Já existe outro equipamento com este número INMETRO.", ids));
        }

        return avisos;
    }

    // Tipo anônimo (e não um record): o EF Core só traduz com segurança filtros sobre a projeção do join assim.
    private IQueryable<EquipamentoComCliente> Juntar(IQueryable<Equipamento> equipamentos) =>
        equipamentos.Join(db.Clientes.AsNoTracking(), e => e.ClienteId, c => c.Id,
            (e, c) => new EquipamentoComCliente { Equipamento = e, Cliente = c });

    private void RegistrarEvento(TipoEventoCadastro tipo, Guid entidadeId, Guid atorId, DateTime quando, string? campos) =>
        db.EventosDeCadastro.Add(new EventoDeCadastro
        {
            Id = Guid.NewGuid(), Entidade = Entidade, EntidadeId = entidadeId,
            Tipo = tipo, AtorId = atorId, OcorridoEm = quando, Campos = campos
        });

    private static ErroNegocio Validacao(string mensagem) => new(CodigosDeErro.Validacao, mensagem);

    private static ErroNegocio ClienteInvalido() => Validacao("Cliente inexistente ou inativo.");

    private static Resultado<T> NaoEncontrado<T>() =>
        Resultado<T>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Equipamento não encontrado."));

    private static EquipamentoResumo Resumo(Equipamento e, string clienteNome) => new(
        e.Id, e.ClienteId, clienteNome, e.Descricao, e.NumeroSerie, e.NumeroInmetro, e.Observacoes,
        e.Ativo, e.CriadoEm, e.AtualizadoEm, Versao.Codificar(e.Versao));

    private sealed class EquipamentoComCliente
    {
        public required Equipamento Equipamento { get; init; }
        public required Cliente Cliente { get; init; }
    }
}
