using Jvd.Application.Clientes;
using Jvd.Application.Comum;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jvd.Infrastructure.Cadastros;

public sealed class ClienteService(JvdDbContext db, TimeProvider relogio, ILogger<ClienteService> log) : IClienteService
{
    private const string Entidade = "cliente";
    private const int TamanhoMaximoDaPagina = 100;
    private const int MaximoDeIdsNoAviso = 5;

    // ---------- consulta ----------

    public async Task<PaginaDeResultados<ClienteResumo>> ListarAsync(
        FiltroDeClientes filtro, bool incluirInativos, CancellationToken ct = default)
    {
        var pagina = Math.Max(1, filtro.Pagina);
        var tamanho = Math.Clamp(filtro.Tamanho, 1, TamanhoMaximoDaPagina);

        var consulta = db.Clientes.AsNoTracking();

        // Regra de negócio: quem não pode ver inativos NUNCA os recebe, qualquer que seja o filtro pedido.
        if (!incluirInativos) consulta = consulta.Where(c => c.Ativo);
        else if (filtro.Ativo is { } ativo) consulta = consulta.Where(c => c.Ativo == ativo);

        if (filtro.Incompleto is { } incompleto) consulta = consulta.Where(c => c.CadastroIncompleto == incompleto);

        var uf = Campos.Uf(filtro.Uf);
        if (uf is not null) consulta = consulta.Where(c => c.Uf == uf);

        if (Campos.Normalizar(filtro.Busca) is { } busca)
        {
            var digitos = Documentos.SomenteDigitos(busca);
            var buscarDocumento = digitos is { Length: >= 3 };
            consulta = consulta.Where(c =>
                c.NomeRazaoSocial.Contains(busca)
                || (c.NomeFantasia != null && c.NomeFantasia.Contains(busca))
                || (c.Cidade != null && c.Cidade.Contains(busca))
                || (buscarDocumento && c.Documento != null && c.Documento.Contains(digitos!)));
        }

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderBy(c => c.NomeRazaoSocial).ThenBy(c => c.Id)
            .Skip((pagina - 1) * tamanho).Take(tamanho)
            .ToListAsync(ct);

        return new PaginaDeResultados<ClienteResumo>(itens.Select(Resumo).ToList(), pagina, tamanho, total);
    }

    public async Task<Resultado<ClienteResumo>> ObterAsync(Guid id, bool incluirInativos, CancellationToken ct = default)
    {
        var cliente = await db.Clientes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return cliente is null || (!cliente.Ativo && !incluirInativos)
            ? NaoEncontrado<ClienteResumo>()
            : Resultado<ClienteResumo>.Ok(Resumo(cliente));
    }

    // ---------- escrita ----------

    public async Task<Resultado<ClienteSalvo>> CriarAsync(DadosDoCliente dados, Guid atorId, CancellationToken ct = default)
    {
        var (erros, d) = Validar(dados);
        if (erros.Count > 0) return Resultado<ClienteSalvo>.Falha(erros);

        var agora = relogio.GetUtcNow().UtcDateTime;
        var cliente = new Cliente
        {
            Id = Guid.NewGuid(),
            Origem = OrigemCadastro.Manual,
            Ativo = true,
            CriadoEm = agora,
            AtualizadoEm = agora
        };
        Aplicar(cliente, d);

        db.Clientes.Add(cliente);
        RegistrarEvento(TipoEventoCadastro.Criado, cliente.Id, atorId, agora, string.Join(',', CamposPreenchidos(cliente)));
        await db.SaveChangesAsync(ct);

        log.LogInformation("Cliente {ClienteId} criado por {AtorId}.", cliente.Id, atorId);
        return Resultado<ClienteSalvo>.Ok(new ClienteSalvo(Resumo(cliente), await AvisosDeDocumentoAsync(cliente, ct)));
    }

    public async Task<Resultado<ClienteSalvo>> AtualizarAsync(
        Guid id, DadosDoCliente dados, string versao, Guid atorId, bool podeEditarInativo, CancellationToken ct = default)
    {
        if (!Versao.TentarDecodificar(versao, out var versaoRecebida))
        {
            return Resultado<ClienteSalvo>.Falha(Validacao("Versão do cadastro inválida."));
        }

        var (erros, d) = Validar(dados);
        if (erros.Count > 0) return Resultado<ClienteSalvo>.Falha(erros);

        var cliente = await db.Clientes.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (cliente is null || (!cliente.Ativo && !podeEditarInativo))
        {
            return NaoEncontrado<ClienteSalvo>();
        }

        var alterados = CamposAlterados(cliente, d);
        if (alterados.Count == 0)
        {
            return Resultado<ClienteSalvo>.Ok(new ClienteSalvo(Resumo(cliente), []));
        }

        var documentoMudou = alterados.Contains("documento");
        Aplicar(cliente, d);
        var agora = relogio.GetUtcNow().UtcDateTime;
        cliente.AtualizadoEm = agora;

        // Concorrência otimista: a gravação só vale se ninguém alterou o registro desde a versão que a pessoa viu.
        db.Entry(cliente).Property(c => c.Versao).OriginalValue = versaoRecebida;
        RegistrarEvento(TipoEventoCadastro.Editado, cliente.Id, atorId, agora, string.Join(',', alterados));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado<ClienteSalvo>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("cadastro")));
        }

        var avisos = documentoMudou ? await AvisosDeDocumentoAsync(cliente, ct) : [];
        return Resultado<ClienteSalvo>.Ok(new ClienteSalvo(Resumo(cliente), avisos));
    }

    public Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default) =>
        AlterarAtivoAsync(id, atorId, ativo: false, ct);

    public Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default) =>
        AlterarAtivoAsync(id, atorId, ativo: true, ct);

    private async Task<Resultado> AlterarAtivoAsync(Guid id, Guid atorId, bool ativo, CancellationToken ct)
    {
        var cliente = await db.Clientes.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (cliente is null) return NaoEncontrado();
        if (cliente.Ativo == ativo) return Resultado.Ok(); // idempotente

        var agora = relogio.GetUtcNow().UtcDateTime;
        cliente.Ativo = ativo;
        cliente.AtualizadoEm = agora;
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

        log.LogInformation("Cliente {ClienteId} {Acao} por {AtorId}.", id, ativo ? "reativado" : "desativado", atorId);
        return Resultado.Ok();
    }

    // ---------- validação e normalização ----------

    private static (List<ErroNegocio> Erros, DadosDoCliente Dados) Validar(DadosDoCliente dados)
    {
        var erros = new List<ErroNegocio>();

        var nome = Campos.Normalizar(dados.NomeRazaoSocial);
        if (nome is null || Campos.Excede(nome, 200)) erros.Add(Validacao("O nome/razão social é obrigatório (até 200 caracteres)."));

        string? Texto(string? valor, int maximo, string rotulo)
        {
            var normalizado = Campos.Normalizar(valor);
            if (Campos.Excede(normalizado, maximo)) erros.Add(Validacao($"{rotulo}: máximo de {maximo} caracteres."));
            return normalizado;
        }

        var fantasia = Texto(dados.NomeFantasia, 200, "Nome fantasia");
        var inscricao = Texto(dados.InscricaoEstadualRg, 30, "Inscrição estadual/RG");
        var logradouro = Texto(dados.Logradouro, 150, "Logradouro");
        var numero = Texto(dados.Numero, 20, "Número");
        var bairro = Texto(dados.Bairro, 80, "Bairro");
        var cidade = Texto(dados.Cidade, 80, "Cidade");
        var telefone = Texto(dados.Telefone, 30, "Telefone");

        string? documento = null;
        if (!string.IsNullOrWhiteSpace(dados.Documento))
        {
            documento = Documentos.SomenteDigitos(dados.Documento);
            if (!Documentos.CpfOuCnpjValido(documento)) erros.Add(Validacao("CPF/CNPJ inválido."));
        }

        string? cep = null;
        if (!string.IsNullOrWhiteSpace(dados.Cep))
        {
            cep = Campos.Cep(dados.Cep);
            if (!Campos.CepValido(cep)) erros.Add(Validacao("CEP inválido (8 dígitos)."));
        }

        var uf = Campos.Uf(dados.Uf);
        if (uf is not null && !Campos.UfValida(uf)) erros.Add(Validacao("UF inválida."));

        var email = Campos.Normalizar(dados.Email);
        if (email is not null && !Campos.EmailValido(email)) erros.Add(Validacao("E-mail inválido."));

        return (erros, new DadosDoCliente(nome ?? "", fantasia, documento, inscricao, logradouro, numero, bairro, cep, cidade, uf, telefone, email));
    }

    private static void Aplicar(Cliente c, DadosDoCliente d)
    {
        c.NomeRazaoSocial = d.NomeRazaoSocial;
        c.NomeFantasia = d.NomeFantasia;
        c.Documento = d.Documento;
        c.InscricaoEstadualRg = d.InscricaoEstadualRg;
        c.Logradouro = d.Logradouro;
        c.Numero = d.Numero;
        c.Bairro = d.Bairro;
        c.Cep = d.Cep;
        c.Cidade = d.Cidade;
        c.Uf = d.Uf;
        c.Telefone = d.Telefone;
        c.Email = d.Email;
        c.CadastroIncompleto = d.Documento is null; // calculado: incompleto enquanto não houver CPF/CNPJ
    }

    /// <summary>Nomes (nunca valores) dos campos que mudariam com os dados informados.</summary>
    private static List<string> CamposAlterados(Cliente c, DadosDoCliente d)
    {
        var alterados = new List<string>();
        void Comparar(string nome, string? atual, string? novo)
        {
            if (!string.Equals(atual, novo, StringComparison.Ordinal)) alterados.Add(nome);
        }

        Comparar("nome_razao_social", c.NomeRazaoSocial, d.NomeRazaoSocial);
        Comparar("nome_fantasia", c.NomeFantasia, d.NomeFantasia);
        Comparar("documento", c.Documento, d.Documento);
        Comparar("inscricao_estadual_rg", c.InscricaoEstadualRg, d.InscricaoEstadualRg);
        Comparar("logradouro", c.Logradouro, d.Logradouro);
        Comparar("numero", c.Numero, d.Numero);
        Comparar("bairro", c.Bairro, d.Bairro);
        Comparar("cep", c.Cep, d.Cep);
        Comparar("cidade", c.Cidade, d.Cidade);
        Comparar("uf", c.Uf, d.Uf);
        Comparar("telefone", c.Telefone, d.Telefone);
        Comparar("email", c.Email, d.Email);
        return alterados;
    }

    private static IEnumerable<string> CamposPreenchidos(Cliente c)
    {
        yield return "nome_razao_social";
        if (c.NomeFantasia is not null) yield return "nome_fantasia";
        if (c.Documento is not null) yield return "documento";
        if (c.InscricaoEstadualRg is not null) yield return "inscricao_estadual_rg";
        if (c.Logradouro is not null) yield return "logradouro";
        if (c.Numero is not null) yield return "numero";
        if (c.Bairro is not null) yield return "bairro";
        if (c.Cep is not null) yield return "cep";
        if (c.Cidade is not null) yield return "cidade";
        if (c.Uf is not null) yield return "uf";
        if (c.Telefone is not null) yield return "telefone";
        if (c.Email is not null) yield return "email";
    }

    // ---------- apoio ----------

    private async Task<IReadOnlyList<Aviso>> AvisosDeDocumentoAsync(Cliente cliente, CancellationToken ct)
    {
        if (cliente.Documento is null) return [];

        var outros = await db.Clientes.AsNoTracking()
            .Where(c => c.Documento == cliente.Documento && c.Id != cliente.Id)
            .OrderBy(c => c.CriadoEm).Select(c => c.Id)
            .Take(MaximoDeIdsNoAviso).ToListAsync(ct);

        return outros.Count == 0
            ? []
            : [new Aviso("cliente.documento_repetido",
                "Já existe outro cadastro com este CPF/CNPJ (pode ser uma filial).", outros)];
    }

    private void RegistrarEvento(TipoEventoCadastro tipo, Guid entidadeId, Guid atorId, DateTime quando, string? campos) =>
        db.EventosDeCadastro.Add(new EventoDeCadastro
        {
            Id = Guid.NewGuid(), Entidade = Entidade, EntidadeId = entidadeId,
            Tipo = tipo, AtorId = atorId, OcorridoEm = quando, Campos = campos
        });

    private static ErroNegocio Validacao(string mensagem) => new(CodigosDeErro.Validacao, mensagem);

    private static Resultado NaoEncontrado() =>
        Resultado.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Cliente não encontrado."));

    private static Resultado<T> NaoEncontrado<T>() =>
        Resultado<T>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Cliente não encontrado."));

    private static ClienteResumo Resumo(Cliente c) => new(
        c.Id, c.NomeRazaoSocial, c.NomeFantasia, c.Documento, c.InscricaoEstadualRg, c.Logradouro, c.Numero,
        c.Bairro, c.Cep, c.Cidade, c.Uf, c.Telefone, c.Email, c.CadastroIncompleto, c.Origem, c.Ativo,
        c.CriadoEm, c.AtualizadoEm, Versao.Codificar(c.Versao));
}
