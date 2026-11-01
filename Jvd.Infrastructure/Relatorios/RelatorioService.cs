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
                Id = r.Id, Numero = r.Numero, Status = r.Status, TipoServico = r.TipoServico, DataServico = r.DataServico,
                ClienteId = r.ClienteId, EquipamentoId = r.EquipamentoId, TecnicoId = r.TecnicoId, TotalGeral = r.TotalGeral,
                Revisao = r.Revisao, ClienteSnapshot = r.ClienteSnapshot, EquipamentoSnapshot = r.EquipamentoSnapshot,
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

    // ---------- retificação ----------

    public async Task<Resultado<RelatorioDetalhe>> RetificarAsync(
        Guid id, DadosDaRetificacao dados, string versao, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        if (!Versao.TentarDecodificar(versao, out var versaoRecebida))
        {
            return Resultado<RelatorioDetalhe>.Falha(Validacao("Versão do relatório inválida."));
        }

        var relatorio = await Visiveis(db.Relatorios, ator).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (relatorio is null) return NaoEncontrado<RelatorioDetalhe>();
        if (relatorio.Status != StatusRelatorio.Emitido) return NaoEmitido<RelatorioDetalhe>();

        var motivo = Campos.Normalizar(dados.Motivo);
        var erros = new List<ErroNegocio>();
        if (motivo is null || Campos.Excede(motivo, 500)) erros.Add(Validacao("O motivo da retificação é obrigatório (até 500 caracteres)."));
        if (string.IsNullOrWhiteSpace(dados.ServicoExecutado)) erros.Add(Validacao("O serviço executado é obrigatório."));

        // Cliente, equipamento e executor não mudam por retificação: validam-se só os campos que ela altera.
        erros.AddRange(Validar(new DadosDoRelatorio(
            relatorio.ClienteId, relatorio.EquipamentoId, null, dados.TipoServico, dados.DataServico, dados.ServicoExecutado,
            dados.Observacoes, dados.VerificacaoAnteriorData, dados.VerificacaoAnteriorCertificado, dados.NumeroSeloReparador,
            dados.GarantiaDias, dados.Total, dados.Desconto, dados.CatalogoServicoId)));

        if (dados.CatalogoServicoId is { } catalogo && catalogo != relatorio.CatalogoServicoId
            && !await db.Catalogo.AsNoTracking().AnyAsync(c => c.Id == catalogo, ct))
        {
            erros.Add(Validacao("Serviço do catálogo inexistente."));
        }

        if (erros.Count > 0) return Resultado<RelatorioDetalhe>.Falha(erros);

        // O que mudou: só estes campos vão para o histórico (valores antes e depois).
        var antes = new Dictionary<string, object?>();
        var depois = new Dictionary<string, object?>();

        void Comparar<T>(string campo, T atual, T novo)
        {
            if (EqualityComparer<T>.Default.Equals(atual, novo)) return;
            antes[campo] = atual;
            depois[campo] = novo;
        }

        var servico = Campos.Normalizar(dados.ServicoExecutado)!;
        var observacoes = Campos.Normalizar(dados.Observacoes);
        var certificado = Campos.Normalizar(dados.VerificacaoAnteriorCertificado);
        var selo = Campos.Normalizar(dados.NumeroSeloReparador);
        var totalGeral = dados.Total - dados.Desconto;

        Comparar("tipoServico", relatorio.TipoServico.ToString(), dados.TipoServico.ToString());
        Comparar("dataServico", relatorio.DataServico, dados.DataServico);
        Comparar("servicoExecutado", relatorio.ServicoExecutado, servico);
        Comparar("observacoes", relatorio.Observacoes, observacoes);
        Comparar("verificacaoAnteriorData", relatorio.VerificacaoAnteriorData, dados.VerificacaoAnteriorData);
        Comparar("verificacaoAnteriorCertificado", relatorio.VerificacaoAnteriorCertificado, certificado);
        Comparar("numeroSeloReparador", relatorio.NumeroSeloReparador, selo);
        Comparar("garantiaDias", relatorio.GarantiaDias, dados.GarantiaDias);
        Comparar("total", relatorio.Total, dados.Total);
        Comparar("desconto", relatorio.Desconto, dados.Desconto);
        Comparar("totalGeral", relatorio.TotalGeral, totalGeral);
        Comparar("catalogoServicoId", relatorio.CatalogoServicoId, dados.CatalogoServicoId);

        string? novoSnapshotDoCliente = null, novoSnapshotDoEquipamento = null;
        if (dados.AtualizarDadosDeCadastro)
        {
            var cliente = await db.Clientes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == relatorio.ClienteId, ct);
            var equipamento = await db.Equipamentos.AsNoTracking().SingleOrDefaultAsync(e => e.Id == relatorio.EquipamentoId, ct);
            if (cliente is null || equipamento is null) return Resultado<RelatorioDetalhe>.Falha(Validacao("Cliente ou equipamento inexistente."));

            if (string.IsNullOrEmpty(cliente.Documento))
            {
                return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(
                    CodigosDeErro.ClienteSemDocumento, "O cliente não tem CPF/CNPJ: complete o cadastro antes de atualizar o relatório."));
            }

            novoSnapshotDoCliente = JsonSerializer.Serialize(SnapshotDe(cliente), Json);
            novoSnapshotDoEquipamento = JsonSerializer.Serialize(SnapshotDe(equipamento), Json);

            if (novoSnapshotDoCliente != relatorio.ClienteSnapshot)
            {
                antes["cliente"] = Ler<SnapshotDoCliente>(relatorio.ClienteSnapshot);
                depois["cliente"] = Ler<SnapshotDoCliente>(novoSnapshotDoCliente);
            }

            if (novoSnapshotDoEquipamento != relatorio.EquipamentoSnapshot)
            {
                antes["equipamento"] = Ler<SnapshotDoEquipamento>(relatorio.EquipamentoSnapshot);
                depois["equipamento"] = Ler<SnapshotDoEquipamento>(novoSnapshotDoEquipamento);
            }
        }

        if (antes.Count == 0)
        {
            return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(CodigosDeErro.RetificacaoSemAlteracao, "A retificação não altera nada."));
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        relatorio.TipoServico = dados.TipoServico;
        relatorio.DataServico = dados.DataServico;
        relatorio.ServicoExecutado = servico;
        relatorio.Observacoes = observacoes;
        relatorio.VerificacaoAnteriorData = dados.VerificacaoAnteriorData;
        relatorio.VerificacaoAnteriorCertificado = certificado;
        relatorio.NumeroSeloReparador = selo;
        relatorio.GarantiaDias = dados.GarantiaDias;
        relatorio.Total = dados.Total;
        relatorio.Desconto = dados.Desconto;
        relatorio.TotalGeral = totalGeral;
        relatorio.CatalogoServicoId = dados.CatalogoServicoId;
        if (novoSnapshotDoCliente is not null) relatorio.ClienteSnapshot = novoSnapshotDoCliente;
        if (novoSnapshotDoEquipamento is not null) relatorio.EquipamentoSnapshot = novoSnapshotDoEquipamento;
        relatorio.Revisao += 1;
        relatorio.AtualizadoEm = agora;
        db.Entry(relatorio).Property(r => r.Versao).OriginalValue = versaoRecebida;

        db.Revisoes.Add(new RelatorioRevisao
        {
            Id = Guid.NewGuid(), RelatorioId = relatorio.Id, Revisao = relatorio.Revisao, RetificadoEm = agora, RetificadoPor = ator.Id,
            Motivo = motivo!, DadosAnteriores = JsonSerializer.Serialize(antes, Json), DadosNovos = JsonSerializer.Serialize(depois, Json)
        });
        RegistrarEvento(relatorio.Id, TipoEventoRelatorio.Retificado, ator.Id, agora, $"Retificado (revisão {relatorio.Revisao}).");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("relatório")));
        }
        catch (DbUpdateException ex) when (Concorrencia.EhChaveDuplicada(ex))
        {
            // Duas retificações com a mesma revisão ao mesmo tempo: a outra venceu.
            return Resultado<RelatorioDetalhe>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("relatório")));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado<RelatorioDetalhe>.Falha(Concorrencia.Conflito());
        }

        log.LogInformation("Relatório {RelatorioId} retificado por {AtorId} (revisão {Revisao}).", relatorio.Id, ator.Id, relatorio.Revisao);
        return Resultado<RelatorioDetalhe>.Ok(await DetalharAsync(relatorio, ct));
    }

    // ---------- cancelamento ----------

    public async Task<Resultado<RelatorioDetalhe>> CancelarAsync(
        Guid id, string? motivo, string versao, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        if (!Versao.TentarDecodificar(versao, out var versaoRecebida))
        {
            return Resultado<RelatorioDetalhe>.Falha(Validacao("Versão do relatório inválida."));
        }

        var relatorio = await Visiveis(db.Relatorios, ator).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (relatorio is null) return NaoEncontrado<RelatorioDetalhe>();
        if (relatorio.Status != StatusRelatorio.Emitido) return NaoEmitido<RelatorioDetalhe>();

        var texto = Campos.Normalizar(motivo);
        if (texto is null || Campos.Excede(texto, 500))
        {
            return Resultado<RelatorioDetalhe>.Falha(Validacao("O motivo do cancelamento é obrigatório (até 500 caracteres)."));
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        relatorio.Status = StatusRelatorio.Cancelado;
        relatorio.CanceladoEm = agora;
        relatorio.CanceladoPor = ator.Id;
        relatorio.MotivoCancelamento = texto;
        relatorio.AtualizadoEm = agora;
        db.Entry(relatorio).Property(r => r.Versao).OriginalValue = versaoRecebida;
        RegistrarEvento(relatorio.Id, TipoEventoRelatorio.Cancelado, ator.Id, agora, "Relatório cancelado."); // o motivo fica no relatório, não na trilha

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

        log.LogInformation("Relatório {RelatorioId} cancelado por {AtorId}.", relatorio.Id, ator.Id);
        return Resultado<RelatorioDetalhe>.Ok(await DetalharAsync(relatorio, ct));
    }

    // ---------- histórico ----------

    public async Task<Resultado<IReadOnlyList<RevisaoDoRelatorio>>> ListarRevisoesAsync(
        Guid id, AtorDoRelatorio ator, CancellationToken ct = default)
    {
        if (!await Visiveis(db.Relatorios.AsNoTracking(), ator).AnyAsync(r => r.Id == id, ct))
        {
            return NaoEncontrado<IReadOnlyList<RevisaoDoRelatorio>>();
        }

        var linhas = await db.Revisoes.AsNoTracking().Where(v => v.RelatorioId == id).OrderBy(v => v.Revisao)
            .Select(v => new
            {
                v.Revisao, v.RetificadoEm, v.RetificadoPor, v.Motivo, v.DadosAnteriores, v.DadosNovos,
                Nome = db.Users.Where(u => u.Id == v.RetificadoPor).Select(u => u.Nome).FirstOrDefault() ?? ""
            })
            .ToListAsync(ct);

        IReadOnlyList<RevisaoDoRelatorio> revisoes = linhas.Select(l => new RevisaoDoRelatorio(
            l.Revisao, l.RetificadoEm, l.RetificadoPor, l.Nome, l.Motivo, Elemento(l.DadosAnteriores), Elemento(l.DadosNovos))).ToList();
        return Resultado<IReadOnlyList<RevisaoDoRelatorio>>.Ok(revisoes);
    }

    private static JsonElement Elemento(string json)
    {
        using var documento = JsonDocument.Parse(json);
        return documento.RootElement.Clone();
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
            cliente, equipamento, emissor, r.EmitidoEm, r.EmitidoPor, r.CanceladoEm, r.CanceladoPor, r.MotivoCancelamento,
            r.CriadoEm, r.AtualizadoEm, Versao.Codificar(r.Versao));
    }

    /// <summary>Só as colunas que o resumo usa: a listagem não carrega o texto do serviço nem o snapshot do emissor.</summary>
    private sealed class LinhaDeLista
    {
        public required Guid Id { get; init; }
        public required int? Numero { get; init; }
        public required StatusRelatorio Status { get; init; }
        public required TipoServico TipoServico { get; init; }
        public required DateOnly DataServico { get; init; }
        public required Guid ClienteId { get; init; }
        public required Guid EquipamentoId { get; init; }
        public required Guid TecnicoId { get; init; }
        public required decimal TotalGeral { get; init; }
        public required int Revisao { get; init; }
        public required string? ClienteSnapshot { get; init; }
        public required string? EquipamentoSnapshot { get; init; }
        public required string ClienteNome { get; init; }
        public required string EquipamentoDescricao { get; init; }
        public required string TecnicoNome { get; init; }
    }

    private static RelatorioResumo Resumo(LinhaDeLista l)
    {
        // O emitido mostra os nomes da cópia da emissão, não os do cadastro de hoje.
        var cliente = Ler<SnapshotDoCliente>(l.ClienteSnapshot)?.Nome ?? l.ClienteNome;
        var equipamento = Ler<SnapshotDoEquipamento>(l.EquipamentoSnapshot)?.Descricao ?? l.EquipamentoDescricao;
        return new RelatorioResumo(l.Id, l.Numero, Formatar(l.Numero), l.Status, l.TipoServico, l.DataServico, l.ClienteId,
            cliente, l.EquipamentoId, equipamento, l.TecnicoId, l.TecnicoNome, l.TotalGeral, l.Revisao);
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

    private static Resultado<T> NaoEmitido<T>() => Resultado<T>.Falha(new ErroNegocio(
        CodigosDeErro.RelatorioNaoEmitido, "Só um relatório emitido pode ser retificado ou cancelado."));

    private static Resultado<T> NaoERascunho<T>() => Resultado<T>.Falha(ErroNaoERascunho());

    private static Resultado NaoERascunho() => Resultado.Falha(ErroNaoERascunho());

    private static ErroNegocio ErroNaoERascunho() =>
        new(CodigosDeErro.RelatorioNaoERascunho, "Este relatório já foi emitido: só pode ser alterado por retificação.");
}
