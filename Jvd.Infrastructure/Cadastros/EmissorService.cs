using Jvd.Application.Comum;
using Jvd.Application.Emissor;
using Jvd.Domain;
using Jvd.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jvd.Infrastructure.Cadastros;

public sealed class EmissorService(JvdDbContext db, TimeProvider relogio, ILogger<EmissorService> log) : IEmissorService
{
    private const string Entidade = "emissor";

    public async Task<Resultado<EmissorResumo>> ObterAsync(CancellationToken ct = default)
    {
        var emissor = await db.Emissores.AsNoTracking().SingleOrDefaultAsync(e => e.Id == Emissor.IdUnico, ct);
        return emissor is null ? NaoConfigurado() : Resultado<EmissorResumo>.Ok(Resumo(emissor));
    }

    public async Task<Resultado<EmissorResumo>> SalvarAsync(
        DadosDoEmissor dados, string? versao, Guid atorId, CancellationToken ct = default)
    {
        var (erros, d) = Validar(dados);
        if (erros.Count > 0) return Resultado<EmissorResumo>.Falha(erros);

        var informouVersao = !string.IsNullOrWhiteSpace(versao);
        byte[] versaoRecebida = [];
        if (informouVersao && !Versao.TentarDecodificar(versao, out versaoRecebida))
        {
            return Resultado<EmissorResumo>.Falha(Validacao("Versão do cadastro inválida."));
        }

        var emissor = await db.Emissores.SingleOrDefaultAsync(e => e.Id == Emissor.IdUnico, ct);
        var agora = relogio.GetUtcNow().UtcDateTime;

        if (emissor is null)
        {
            // Primeira configuração: não há versão a conferir.
            if (informouVersao) return NaoConfigurado();

            emissor = new Emissor { Id = Emissor.IdUnico };
            Aplicar(emissor, d);
            emissor.AtualizadoEm = agora;
            db.Emissores.Add(emissor);
            RegistrarEvento(TipoEventoCadastro.Criado, atorId, agora, string.Join(',', CamposPreenchidos(emissor)));

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (Concorrencia.EhChaveDuplicada(ex))
            {
                // Duas primeiras gravações ao mesmo tempo: a outra venceu.
                return Resultado<EmissorResumo>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("cadastro")));
            }
            catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
            {
                return Resultado<EmissorResumo>.Falha(Concorrencia.Conflito());
            }

            log.LogInformation("Emissor configurado por {AtorId}.", atorId);
            return Resultado<EmissorResumo>.Ok(Resumo(emissor));
        }

        if (!informouVersao)
        {
            // Sem versão num emissor que já existe: quase sempre é a segunda de duas primeiras gravações simultâneas.
            return Resultado<EmissorResumo>.Falha(new ErroNegocio(CodigosDeErro.Conflito,
                "O emissor já está configurado. Recarregue e envie a versão atual do cadastro para alterá-lo."));
        }

        var alterados = CamposAlterados(emissor, d);
        if (alterados.Count == 0) return Resultado<EmissorResumo>.Ok(Resumo(emissor));

        Aplicar(emissor, d);
        emissor.AtualizadoEm = agora;
        db.Entry(emissor).Property(e => e.Versao).OriginalValue = versaoRecebida;
        RegistrarEvento(TipoEventoCadastro.Editado, atorId, agora, string.Join(',', alterados));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Resultado<EmissorResumo>.Falha(new ErroNegocio(CodigosDeErro.Conflito, Versao.MensagemDeConflito("cadastro")));
        }
        catch (Exception ex) when (Concorrencia.EhDeadlock(ex))
        {
            return Resultado<EmissorResumo>.Falha(Concorrencia.Conflito());
        }

        log.LogInformation("Emissor alterado por {AtorId}.", atorId);
        return Resultado<EmissorResumo>.Ok(Resumo(emissor));
    }

    // ---------- validação e normalização ----------

    private static (List<ErroNegocio> Erros, DadosDoEmissor Dados) Validar(DadosDoEmissor dados)
    {
        var erros = new List<ErroNegocio>();

        string? Texto(string? valor, int maximo, string rotulo)
        {
            var normalizado = Campos.Normalizar(valor);
            if (Campos.Excede(normalizado, maximo)) erros.Add(Validacao($"{rotulo}: máximo de {maximo} caracteres."));
            return normalizado;
        }

        var razao = Campos.Normalizar(dados.RazaoSocial);
        if (razao is null || Campos.Excede(razao, 200)) erros.Add(Validacao("A razão social é obrigatória (até 200 caracteres)."));

        var cnpj = Documentos.SomenteDigitos(dados.Cnpj);
        if (!Documentos.CnpjValido(cnpj)) erros.Add(Validacao("CNPJ inválido."));

        var fantasia = Texto(dados.NomeFantasia, 200, "Nome fantasia");
        var inscricao = Texto(dados.InscricaoEstadual, 30, "Inscrição estadual");
        var logradouro = Texto(dados.Logradouro, 150, "Logradouro");
        var numero = Texto(dados.Numero, 20, "Número");
        var bairro = Texto(dados.Bairro, 80, "Bairro");
        var cidade = Texto(dados.Cidade, 80, "Cidade");
        var telefones = Texto(dados.Telefones, 200, "Telefones");
        var ipem = Texto(dados.NumeroAutorizacaoIpem, 60, "Número de autorização do IPEM");
        var responsavel = Texto(dados.ResponsavelTecnicoNome, 150, "Nome do responsável técnico");

        string? cep = null;
        if (!string.IsNullOrWhiteSpace(dados.Cep))
        {
            cep = Campos.Cep(dados.Cep);
            if (!Campos.CepValido(cep)) erros.Add(Validacao("CEP inválido (8 dígitos)."));
        }

        var uf = Campos.Uf(dados.Uf);
        if (uf is not null && !Campos.UfValida(uf)) erros.Add(Validacao("UF inválida."));

        string? cpf = null;
        if (!string.IsNullOrWhiteSpace(dados.ResponsavelTecnicoCpf))
        {
            cpf = Documentos.SomenteDigitos(dados.ResponsavelTecnicoCpf);
            if (!Documentos.CpfValido(cpf)) erros.Add(Validacao("CPF do responsável técnico inválido."));
        }

        return (erros, new DadosDoEmissor(razao ?? "", fantasia, cnpj ?? "", inscricao, logradouro, numero, bairro, cep, cidade, uf,
            telefones, ipem, responsavel, cpf));
    }

    private static void Aplicar(Emissor e, DadosDoEmissor d)
    {
        e.RazaoSocial = d.RazaoSocial;
        e.NomeFantasia = d.NomeFantasia;
        e.Cnpj = d.Cnpj;
        e.InscricaoEstadual = d.InscricaoEstadual;
        e.Logradouro = d.Logradouro;
        e.Numero = d.Numero;
        e.Bairro = d.Bairro;
        e.Cep = d.Cep;
        e.Cidade = d.Cidade;
        e.Uf = d.Uf;
        e.Telefones = d.Telefones;
        e.NumeroAutorizacaoIpem = d.NumeroAutorizacaoIpem;
        e.ResponsavelTecnicoNome = d.ResponsavelTecnicoNome;
        e.ResponsavelTecnicoCpf = d.ResponsavelTecnicoCpf;
    }

    /// <summary>Nomes (nunca valores) dos campos que mudariam com os dados informados.</summary>
    private static List<string> CamposAlterados(Emissor e, DadosDoEmissor d)
    {
        var alterados = new List<string>();
        void Comparar(string nome, string? atual, string? novo)
        {
            if (!string.Equals(atual, novo, StringComparison.Ordinal)) alterados.Add(nome);
        }

        Comparar("razao_social", e.RazaoSocial, d.RazaoSocial);
        Comparar("nome_fantasia", e.NomeFantasia, d.NomeFantasia);
        Comparar("cnpj", e.Cnpj, d.Cnpj);
        Comparar("inscricao_estadual", e.InscricaoEstadual, d.InscricaoEstadual);
        Comparar("logradouro", e.Logradouro, d.Logradouro);
        Comparar("numero", e.Numero, d.Numero);
        Comparar("bairro", e.Bairro, d.Bairro);
        Comparar("cep", e.Cep, d.Cep);
        Comparar("cidade", e.Cidade, d.Cidade);
        Comparar("uf", e.Uf, d.Uf);
        Comparar("telefones", e.Telefones, d.Telefones);
        Comparar("numero_autorizacao_ipem", e.NumeroAutorizacaoIpem, d.NumeroAutorizacaoIpem);
        Comparar("responsavel_tecnico_nome", e.ResponsavelTecnicoNome, d.ResponsavelTecnicoNome);
        Comparar("responsavel_tecnico_cpf", e.ResponsavelTecnicoCpf, d.ResponsavelTecnicoCpf);
        return alterados;
    }

    private static IEnumerable<string> CamposPreenchidos(Emissor e)
    {
        yield return "razao_social";
        yield return "cnpj";
        if (e.NomeFantasia is not null) yield return "nome_fantasia";
        if (e.InscricaoEstadual is not null) yield return "inscricao_estadual";
        if (e.Logradouro is not null) yield return "logradouro";
        if (e.Numero is not null) yield return "numero";
        if (e.Bairro is not null) yield return "bairro";
        if (e.Cep is not null) yield return "cep";
        if (e.Cidade is not null) yield return "cidade";
        if (e.Uf is not null) yield return "uf";
        if (e.Telefones is not null) yield return "telefones";
        if (e.NumeroAutorizacaoIpem is not null) yield return "numero_autorizacao_ipem";
        if (e.ResponsavelTecnicoNome is not null) yield return "responsavel_tecnico_nome";
        if (e.ResponsavelTecnicoCpf is not null) yield return "responsavel_tecnico_cpf";
    }

    // ---------- apoio ----------

    private void RegistrarEvento(TipoEventoCadastro tipo, Guid atorId, DateTime quando, string? campos) =>
        db.EventosDeCadastro.Add(new EventoDeCadastro
        {
            Id = Guid.NewGuid(), Entidade = Entidade, EntidadeId = Emissor.IdUnico,
            Tipo = tipo, AtorId = atorId, OcorridoEm = quando, Campos = campos
        });

    private static ErroNegocio Validacao(string mensagem) => new(CodigosDeErro.Validacao, mensagem);

    private static Resultado<EmissorResumo> NaoConfigurado() =>
        Resultado<EmissorResumo>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "O emissor ainda não foi configurado."));

    private static EmissorResumo Resumo(Emissor e) => new(
        e.RazaoSocial, e.NomeFantasia, e.Cnpj, e.InscricaoEstadual, e.Logradouro, e.Numero, e.Bairro, e.Cep, e.Cidade,
        e.Uf, e.Telefones, e.NumeroAutorizacaoIpem, e.ResponsavelTecnicoNome, e.ResponsavelTecnicoCpf, e.AtualizadoEm,
        Versao.Codificar(e.Versao));
}
