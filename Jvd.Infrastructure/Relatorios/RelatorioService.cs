using System.Data;
using System.Globalization;
using System.Text.Json;
using Jvd.Application.Comum;
using Jvd.Application.Relatorios;
using Jvd.Domain;
using Jvd.Infrastructure.Cadastros;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jvd.Infrastructure.Relatorios;

public sealed class RelatorioService(JvdDbContext db, TimeProvider relogio, ILogger<RelatorioService> log) : IRelatorioService
{
    private const int TamanhoMaximoDaPagina = 100;
    private const decimal ValorMaximo = 9_999_999_999.99m;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------- consulta ----------

    public async Task<PaginaDeResultados<RelatorioResumo>> ListarAsync(
        FiltroDeRelatorios filtro, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        var pagina = Math.Max(1, filtro.Pagina);
        var tamanho = Math.Clamp(filtro.Tamanho, 1, TamanhoMaximoDaPagina);

        var consulta = Visiveis(db.Relatorios.AsNoTracking(), ator);

        // O técnico só vê os seus: o filtro por executor só vale para o gestor.
        if (ator.EhGestor && filtro.TecnicoId is { } tecnico) consulta = consulta.Where(r => r.TecnicoId == tecnico);
        if (filtro.ClienteId is { } cliente) consulta = consulta.Where(r => r.ClienteId == cliente);
        if (filtro.EquipamentoId is { } equipamento) consulta = consulta.Where(r => r.EquipamentoId == equipamento);
        if (filtro.Status is { } status) consulta = consulta.Where(r => r.Status == status);
        if (filtro.Tipo is { } tipo) consulta = consulta.Where(r => r.TipoServico == tipo);
        if (filtro.De is { } de) consulta = consulta.Where(r => r.DataServico >= de);
        if (filtro.Ate is { } ate) consulta = consulta.Where(r => r.DataServico <= ate);
        if (filtro.Numero is { } numero) consulta = consulta.Where(r => r.Numero == numero);

        var total = await consulta.CountAsync(ct);
        var linhas = await consulta
            .OrderByDescending(r => r.DataServico).ThenByDescending(r => r.CriadoEm).ThenBy(r => r.Id)
            .Skip((pagina - 1) * tamanho).Take(tamanho)
            .Select(r => new LinhaDeLista
            {
                Relatorio = r,
                ClienteNome = db.Clientes.Where(c => c.Id == r.ClienteId).Select(c => c.NomeRazaoSocial).FirstOrDefault() ?? "",
                EquipamentoDescricao = db.Equipamentos.Where(e => e.Id == r.EquipamentoId).Select(e => e.Descricao).FirstOrDefault() ?? "",
                TecnicoNome = db.Users.Where(u => u.Id == r.TecnicoId).Select(u => u.Nome).FirstOrDefault() ?? ""
            })
            .ToListAsync(ct);

        return new PaginaDeResultados<RelatorioResumo>(linhas.Select(Resumo).ToList(), pagina, tamanho, total);
    }

    public async Task<Resultado<RelatorioDetalhe>> ObterAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        var relatorio = await Visiveis(db.Relatorios.AsNoTracking(), ator).SingleOrDefaultAsync(r => r.Id == id, ct);
        return relatorio is null ? NaoEncontrado<RelatorioDetalhe>() : Resultado<RelatorioDetalhe>.Ok(await DetalharAsync(relatorio, ct));
    }

    // ---------- rascunho ----------

    public async Task<Resultado<RelatorioDetalhe>> CriarRascunhoAsync(
        DadosDoRelatorio dados, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        var erros = Validar(dados);
        if (erros.Count > 0) return Resultado<RelatorioDetalhe>.Falha(erros);

        var tecnicoId = dados.TecnicoId ?? ator.Id;
        var vinculos = await ValidarVinculosAsync(dados, tecnicoId, ator, ct);
        if (vinculos.Count > 0) return Resultado<RelatorioDetalhe>.Falha(vinculos);

        var agora = relogio.GetUtcNow().UtcDateTime;
        var relatorio = new Relatorio
        {
            Id = Guid.NewGuid(), Status = StatusRelatorio.Rascunho, CriadoPor = ator.Id, TecnicoId = tecnicoId,
            Revisao = 0, CriadoEm = agora, AtualizadoEm = agora
        };
        Aplicar(relatorio, dados);

        db.Relatorios.Add(relatorio);
        RegistrarEvento(relatorio.Id, TipoEventoRelatorio.Criado, ator.Id, agora, "Rascunho criado.");
        await db.SaveChangesAsync(ct);

        log.LogInformation("Rascunho de relatório {RelatorioId} criado por {AtorId}.", relatorio.Id, ator.Id);
        return Resultado<RelatorioDetalhe>.Ok(await DetalharAsync(relatorio, ct));
    }

    public async Task<Resultado<RelatorioDetalhe>> AtualizarRascunhoAsync(
        Guid id, DadosDoRelatorio dados, string versao, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        if (!Versao.TentarDecodificar(versao, out var versaoRecebida))
        {
            return Resultado<RelatorioDetalhe>.Falha(Validacao("Versão do relatório inválida."));
        }

        var erros = Validar(dados);
        if (erros.Count > 0) return Resultado<RelatorioDetalhe>.Falha(erros);

        var relatorio = await Visiveis(db.Relatorios, ator).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (relatorio is null) return NaoEncontrado<RelatorioDetalhe>();
        if (relatorio.Status != StatusRelatorio.Rascunho) return NaoERascunho<RelatorioDetalhe>();

        var tecnicoId = dados.TecnicoId ?? relatorio.TecnicoId; // nulo = manter o executor
        var vinculos = await ValidarVinculosAsync(dados, tecnicoId, ator, ct);
        if (vinculos.Count > 0) return Resultado<RelatorioDetalhe>.Falha(vinculos);

        Aplicar(relatorio, dados);
        relatorio.TecnicoId = tecnicoId;
        relatorio.AtualizadoEm = relogio.GetUtcNow().UtcDateTime;
        db.Entry(relatorio).Property(r => r.Versao).OriginalValue = versaoRecebida;
        // Mesmo que nada tenha mudado, a gravação acontece: é ela que confere a versão (senão uma versão velha passaria em silêncio).
        db.Entry(relatorio).Property(r => r.AtualizadoEm).IsModified = true;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("relatório")));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado<RelatorioDetalhe>.Falha(Concorrencia.Conflito());
        }

        return Resultado<RelatorioDetalhe>.Ok(await DetalharAsync(relatorio, ct));
    }

    public async Task<Resultado> DescartarRascunhoAsync(Guid id, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        var relatorio = await Visiveis(db.Relatorios, ator).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (relatorio is null) return NaoEncontrado();
        if (relatorio.Status != StatusRelatorio.Rascunho) return NaoERascunho();

        try
        {
            await using var transacao = await db.Database.BeginTransactionAsync(ct);
            db.Eventos.RemoveRange(await db.Eventos.Where(e => e.RelatorioId == id).ToListAsync(ct));
            db.Relatorios.Remove(relatorio);
            await db.SaveChangesAsync(ct);
            await transacao.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("relatório")));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado.Falha(Concorrencia.Conflito());
        }

        log.LogInformation("Rascunho de relatório {RelatorioId} descartado por {AtorId}.", id, ator.Id);
        return Resultado.Ok();
    }

    // ---------- emissão ----------

    public async Task<Resultado<RelatorioDetalhe>> EmitirAsync(Guid id, string versao, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        if (!Versao.TentarDecodificar(versao, out var versaoRecebida))
        {
            return Resultado<RelatorioDetalhe>.Falha(Validacao("Versão do relatório inválida."));
        }

        try
        {
            // A transação cobre o incremento do contador: se qualquer passo falhar, o número volta (sem buracos).
            await using var transacao = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            var relatorio = await Visiveis(db.Relatorios, ator).SingleOrDefaultAsync(r => r.Id == id, ct);
            if (relatorio is null) return NaoEncontrado<RelatorioDetalhe>();
            if (relatorio.Status != StatusRelatorio.Rascunho) return NaoERascunho<RelatorioDetalhe>();

            if (string.IsNullOrWhiteSpace(relatorio.ServicoExecutado))
            {
                return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(CodigosDeErro.RelatorioIncompleto, "Preencha o serviço executado antes de emitir."));
            }

            var cliente = await db.Clientes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == relatorio.ClienteId, ct);
            if (cliente is null || !cliente.Ativo)
            {
                return Resultado<RelatorioDetalhe>.Falha(Validacao("Cliente inexistente ou inativo."));
            }

            if (string.IsNullOrEmpty(cliente.Documento))
            {
                return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(
                    CodigosDeErro.ClienteSemDocumento, "O cliente não tem CPF/CNPJ: complete o cadastro antes de emitir."));
            }

            var equipamento = await db.Equipamentos.AsNoTracking().SingleOrDefaultAsync(e => e.Id == relatorio.EquipamentoId, ct);
            if (equipamento is null || !equipamento.Ativo || equipamento.ClienteId != cliente.Id)
            {
                return Resultado<RelatorioDetalhe>.Falha(Validacao("Equipamento inexistente, inativo ou de outro cliente."));
            }

            var executorAtivo = await db.Users.AsNoTracking().AnyAsync(u => u.Id == relatorio.TecnicoId && u.Ativo, ct);
            if (!executorAtivo) return Resultado<RelatorioDetalhe>.Falha(Validacao("Executor inexistente ou inativo."));

            var emissor = await db.Emissores.AsNoTracking().SingleOrDefaultAsync(e => e.Id == Emissor.IdUnico, ct);
            if (emissor is null || string.IsNullOrWhiteSpace(emissor.ResponsavelTecnicoNome) || string.IsNullOrWhiteSpace(emissor.ResponsavelTecnicoCpf))
            {
                return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(
                    CodigosDeErro.EmissorIncompleto, "Configure o emissor, com o responsável técnico (nome e CPF), antes de emitir."));
            }

            var agora = relogio.GetUtcNow().UtcDateTime;
            relatorio.Numero = await ProximoNumeroAsync(ct);
            relatorio.Status = StatusRelatorio.Emitido;
            relatorio.ClienteSnapshot = JsonSerializer.Serialize(SnapshotDe(cliente), Json);
            relatorio.EquipamentoSnapshot = JsonSerializer.Serialize(SnapshotDe(equipamento), Json);
            relatorio.EmissorSnapshot = JsonSerializer.Serialize(SnapshotDe(emissor), Json);
            relatorio.EmitidoEm = agora;
            relatorio.EmitidoPor = ator.Id;
            relatorio.AtualizadoEm = agora;
            db.Entry(relatorio).Property(r => r.Versao).OriginalValue = versaoRecebida;

            RegistrarEvento(relatorio.Id, TipoEventoRelatorio.Emitido, ator.Id, agora, "Relatório emitido.");

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("relatório")));
            }

            await transacao.CommitAsync(ct);

            log.LogInformation("Relatório {RelatorioId} emitido por {AtorId} com o número {Numero}.", relatorio.Id, ator.Id, relatorio.Numero);
            return Resultado<RelatorioDetalhe>.Ok(await DetalharAsync(relatorio, ct));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado<RelatorioDetalhe>.Falha(Concorrencia.Conflito());
        }
    }

    /// <summary>
    /// Incrementa o contador e lê o valor na mesma transação. O UPDATE mantém a linha travada até o fim da transação:
    /// outra emissão espera, e se esta falhar o incremento é desfeito (a sequência não ganha buracos).
    /// </summary>
    private async Task<int> ProximoNumeroAsync(CancellationToken ct)
    {
        var chave = ContadorDeNumeracao.ChaveDoRelatorio;
        var linhas = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE contador_numeracao SET ultimo = ultimo + 1 WHERE chave = {chave}", ct);
        if (linhas != 1) throw new InvalidOperationException("O contador de numeração não está configurado.");

        return await db.Contadores.AsNoTracking().Where(c => c.Chave == chave).Select(c => c.Ultimo).SingleAsync(ct);
    }

    // ---------- validação ----------

    private static List<ErroNegocio> Validar(DadosDoRelatorio d)
    {
        var erros = new List<ErroNegocio>();

        if (d.ClienteId == Guid.Empty) erros.Add(Validacao("O cliente é obrigatório."));
        if (d.EquipamentoId == Guid.Empty) erros.Add(Validacao("O equipamento é obrigatório."));
        if (!Enum.IsDefined(d.TipoServico)) erros.Add(Validacao("O tipo de serviço é obrigatório."));
        if (Tamanho(d.ServicoExecutado, 4000)) erros.Add(Validacao("Serviço executado: máximo de 4.000 caracteres."));
        if (Tamanho(d.Observacoes, 2000)) erros.Add(Validacao("Observações: máximo de 2.000 caracteres."));
        if (Tamanho(d.VerificacaoAnteriorCertificado, 60)) erros.Add(Validacao("Certificado da verificação anterior: máximo de 60 caracteres."));
        if (Tamanho(d.NumeroSeloReparador, 60)) erros.Add(Validacao("Número do selo reparador: máximo de 60 caracteres."));
        if (d.GarantiaDias is < 0 or > Extenso.Maximo) erros.Add(Validacao($"Garantia: de 0 a {Extenso.Maximo} dias."));
        if (!ValorValido(d.Total)) erros.Add(Validacao("Total inválido (zero ou mais, até 2 casas decimais)."));
        if (!ValorValido(d.Desconto)) erros.Add(Validacao("Desconto inválido (zero ou mais, até 2 casas decimais)."));
        else if (ValorValido(d.Total) && d.Desconto > d.Total) erros.Add(Validacao("O desconto não pode ser maior que o total."));

        return erros;
    }

    private static bool Tamanho(string? texto, int maximo) => Campos.Excede(Campos.Normalizar(texto), maximo);

    private static bool ValorValido(decimal v) => v >= 0 && v <= ValorMaximo && decimal.Round(v, 2) == v;

    /// <summary>Cliente, equipamento, executor e catálogo precisam existir e estar ativos; o equipamento tem de ser do cliente.</summary>
    private async Task<List<ErroNegocio>> ValidarVinculosAsync(DadosDoRelatorio d, Guid tecnicoId, AtorDoRelatorio ator, CancellationToken ct)
    {
        var erros = new List<ErroNegocio>();

        var cliente = await db.Clientes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == d.ClienteId, ct);
        if (cliente is null || !cliente.Ativo) erros.Add(Validacao("Cliente inexistente ou inativo."));

        var equipamento = await db.Equipamentos.AsNoTracking().SingleOrDefaultAsync(e => e.Id == d.EquipamentoId, ct);
        if (equipamento is null || !equipamento.Ativo) erros.Add(Validacao("Equipamento inexistente ou inativo."));
        else if (cliente is not null && equipamento.ClienteId != cliente.Id) erros.Add(Validacao("O equipamento não pertence ao cliente informado."));

        if (tecnicoId != ator.Id && !ator.EhGestor) erros.Add(Validacao("Só o gestor pode definir outro executor."));
        else if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == tecnicoId && u.Ativo, ct)) erros.Add(Validacao("Executor inexistente ou inativo."));

        if (d.CatalogoServicoId is { } catalogo && !await db.Catalogo.AsNoTracking().AnyAsync(s => s.Id == catalogo, ct))
        {
            erros.Add(Validacao("Serviço do catálogo inexistente."));
        }

        return erros;
    }

    private static void Aplicar(Relatorio r, DadosDoRelatorio d)
    {
        r.ClienteId = d.ClienteId;
        r.EquipamentoId = d.EquipamentoId;
        r.TipoServico = d.TipoServico;
        r.DataServico = d.DataServico;
        r.ServicoExecutado = Campos.Normalizar(d.ServicoExecutado) ?? "";
        r.Observacoes = Campos.Normalizar(d.Observacoes);
        r.VerificacaoAnteriorData = d.VerificacaoAnteriorData;
        r.VerificacaoAnteriorCertificado = Campos.Normalizar(d.VerificacaoAnteriorCertificado);
        r.NumeroSeloReparador = Campos.Normalizar(d.NumeroSeloReparador);
        r.GarantiaDias = d.GarantiaDias;
        r.Total = d.Total;
        r.Desconto = d.Desconto;
        r.TotalGeral = d.Total - d.Desconto;
        r.CatalogoServicoId = d.CatalogoServicoId;
    }

    // ---------- leitura ----------

    /// <summary>O técnico só enxerga os relatórios em que é o executor; o gestor, todos.</summary>
    private static IQueryable<Relatorio> Visiveis(IQueryable<Relatorio> consulta, AtorDoRelatorio ator) =>
        ator.EhGestor ? consulta : consulta.Where(r => r.TecnicoId == ator.Id);

    private async Task<RelatorioDetalhe> DetalharAsync(Relatorio r, CancellationToken ct)
    {
        var cliente = Ler<SnapshotDoCliente>(r.ClienteSnapshot);
        var equipamento = Ler<SnapshotDoEquipamento>(r.EquipamentoSnapshot);
        var emissor = Ler<SnapshotDoEmissor>(r.EmissorSnapshot);

        // Rascunho mostra o cadastro vivo; o emitido mostra a cópia do momento da emissão.
        var clienteNome = cliente?.Nome
            ?? await db.Clientes.AsNoTracking().Where(c => c.Id == r.ClienteId).Select(c => c.NomeRazaoSocial).FirstOrDefaultAsync(ct) ?? "";
        var equipamentoDescricao = equipamento?.Descricao
            ?? await db.Equipamentos.AsNoTracking().Where(e => e.Id == r.EquipamentoId).Select(e => e.Descricao).FirstOrDefaultAsync(ct) ?? "";
        var tecnicoNome = await db.Users.AsNoTracking().Where(u => u.Id == r.TecnicoId).Select(u => u.Nome).FirstOrDefaultAsync(ct) ?? "";

        return new RelatorioDetalhe(
            r.Id, r.Numero, Formatar(r.Numero), r.Status, r.TipoServico, r.DataServico, r.ClienteId, clienteNome,
            r.EquipamentoId, equipamentoDescricao, r.TecnicoId, tecnicoNome, r.ServicoExecutado, r.Observacoes,
            r.VerificacaoAnteriorData, r.VerificacaoAnteriorCertificado, r.NumeroSeloReparador, r.GarantiaDias,
            Extenso.Garantia(r.GarantiaDias), r.Total, r.Desconto, r.TotalGeral, r.CatalogoServicoId, r.Revisao,
            cliente, equipamento, emissor, r.EmitidoEm, r.EmitidoPor, r.CriadoEm, r.AtualizadoEm, Versao.Codificar(r.Versao));
    }

    private sealed class LinhaDeLista
    {
        public required Relatorio Relatorio { get; init; }
        public required string ClienteNome { get; init; }
        public required string EquipamentoDescricao { get; init; }
        public required string TecnicoNome { get; init; }
    }

    private static RelatorioResumo Resumo(LinhaDeLista l)
    {
        var r = l.Relatorio;
        // O emitido mostra os nomes da cópia da emissão, não os do cadastro de hoje.
        var cliente = Ler<SnapshotDoCliente>(r.ClienteSnapshot)?.Nome ?? l.ClienteNome;
        var equipamento = Ler<SnapshotDoEquipamento>(r.EquipamentoSnapshot)?.Descricao ?? l.EquipamentoDescricao;
        return new RelatorioResumo(r.Id, r.Numero, Formatar(r.Numero), r.Status, r.TipoServico, r.DataServico, r.ClienteId,
            cliente, r.EquipamentoId, equipamento, r.TecnicoId, l.TecnicoNome, r.TotalGeral, r.Revisao);
    }

    private static string? Formatar(int? numero) => numero?.ToString("D6", CultureInfo.InvariantCulture);

    private static T? Ler<T>(string? json) where T : class => json is null ? null : JsonSerializer.Deserialize<T>(json, Json);

    private static SnapshotDoCliente SnapshotDe(Cliente c) => new(
        c.NomeRazaoSocial, c.NomeFantasia, c.Documento, c.InscricaoEstadualRg, c.Logradouro, c.Numero, c.Bairro, c.Cep,
        c.Cidade, c.Uf, c.Telefone);

    private static SnapshotDoEquipamento SnapshotDe(Equipamento e) => new(e.Descricao, e.NumeroSerie, e.NumeroInmetro);

    private static SnapshotDoEmissor SnapshotDe(Emissor e) => new(
        e.RazaoSocial, e.NomeFantasia, e.Cnpj, e.InscricaoEstadual, e.Logradouro, e.Numero, e.Bairro, e.Cep, e.Cidade, e.Uf,
        e.Telefones, e.NumeroAutorizacaoIpem, e.ResponsavelTecnicoNome, e.ResponsavelTecnicoCpf);

    // ---------- apoio ----------

    private void RegistrarEvento(Guid relatorioId, TipoEventoRelatorio tipo, Guid usuarioId, DateTime quando, string? detalhe) =>
        db.Eventos.Add(new RelatorioEvento
        {
            Id = Guid.NewGuid(), RelatorioId = relatorioId, Tipo = tipo, UsuarioId = usuarioId, OcorridoEm = quando, Detalhe = detalhe
        });

    private static ErroNegocio Validacao(string mensagem) => new(CodigosDeErro.Validacao, mensagem);

    private static Resultado<T> NaoEncontrado<T>() =>
        Resultado<T>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Relatório não encontrado."));

    private static Resultado NaoEncontrado() =>
        Resultado.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Relatório não encontrado."));

    private static Resultado<T> NaoERascunho<T>() => Resultado<T>.Falha(ErroNaoERascunho());

    private static Resultado NaoERascunho() => Resultado.Falha(ErroNaoERascunho());

    private static ErroNegocio ErroNaoERascunho() =>
        new(CodigosDeErro.RelatorioNaoERascunho, "Este relatório já foi emitido: só pode ser alterado por retificação.");
}
