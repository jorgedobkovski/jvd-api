using System.Data;
using System.Net.Mail;
using Jvd.Application.Autenticacao;
using Jvd.Application.Comum;
using Jvd.Application.Usuarios;
using Jvd.Domain;
using Jvd.Infrastructure.Autenticacao;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jvd.Infrastructure.Usuarios;

public sealed class UsuarioService(
    JvdDbContext db,
    UserManager<Usuario> usuarios,
    IAutenticacaoService autenticacao,
    TimeProvider relogio,
    ILogger<UsuarioService> log) : IUsuarioService
{
    private const string Entidade = "usuario";
    private const int TamanhoMaximoDaPagina = 100;

    // ---------- consulta ----------

    public async Task<PaginaDeResultados<UsuarioResumo>> ListarAsync(FiltroDeUsuarios filtro, CancellationToken ct = default)
    {
        var pagina = Math.Max(1, filtro.Pagina);
        var tamanho = Math.Clamp(filtro.Tamanho, 1, TamanhoMaximoDaPagina);

        var consulta = db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filtro.Busca))
        {
            var busca = filtro.Busca.Trim();
            consulta = consulta.Where(u => u.Nome.Contains(busca) || u.Email!.Contains(busca));
        }

        if (filtro.Perfil is { } perfil) consulta = consulta.Where(u => u.Perfil == perfil);
        if (filtro.Ativo is { } ativo) consulta = consulta.Where(u => u.Ativo == ativo);

        var total = await consulta.CountAsync(ct);
        var itens = await consulta
            .OrderBy(u => u.Nome).ThenBy(u => u.Id)
            .Skip((pagina - 1) * tamanho).Take(tamanho)
            .ToListAsync(ct);

        return new PaginaDeResultados<UsuarioResumo>(itens.Select(Resumo).ToList(), pagina, tamanho, total);
    }

    public async Task<Resultado<UsuarioResumo>> ObterAsync(Guid id, CancellationToken ct = default)
    {
        var usuario = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        return usuario is null ? NaoEncontrado<UsuarioResumo>() : Resultado<UsuarioResumo>.Ok(Resumo(usuario));
    }

    // ---------- escrita ----------

    public async Task<Resultado<UsuarioResumo>> CriarAsync(CriarUsuarioComando comando, CancellationToken ct = default)
    {
        var nome = comando.Nome?.Trim() ?? "";
        var email = comando.Email?.Trim() ?? "";
        var cpf = Documentos.SomenteDigitos(comando.Cpf);

        var erros = ValidarDadosBasicos(nome, comando.Perfil, comando.Cpf, cpf);
        if (!EmailValido(email)) erros.Add(Validacao("E-mail inválido."));
        if (string.IsNullOrWhiteSpace(comando.SenhaProvisoria)) erros.Add(Validacao("A senha provisória é obrigatória."));
        if (erros.Count > 0) return Resultado<UsuarioResumo>.Falha(erros);

        if (await usuarios.FindByEmailAsync(email) is not null)
        {
            return Resultado<UsuarioResumo>.Falha(new ErroNegocio(CodigosDeErro.EmailEmUso, "Já existe um usuário com este e-mail."));
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Nome = nome,
            Perfil = comando.Perfil,
            Cpf = cpf,
            Ativo = true,
            DeveTrocarSenha = true, // ADR-006: a senha provisória só serve para trocar a senha
            CriadoEm = agora,
            AtualizadoEm = agora
        };

        try
        {
            await using var transacao = await db.Database.BeginTransactionAsync(ct);

            var criado = await usuarios.CreateAsync(usuario, comando.SenhaProvisoria);
            if (!criado.Succeeded)
            {
                return Resultado<UsuarioResumo>.Falha(TradutorDeErrosIdentity.Usuario(criado.Errors));
            }

            RegistrarEvento(TipoEventoCadastro.Criado, usuario.Id, comando.AtorId, agora,
                cpf is null ? "nome,email,perfil" : "nome,email,perfil,cpf");
            await db.SaveChangesAsync(ct);
            await transacao.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Corrida entre dois cadastros do mesmo e-mail: o índice único do banco decide.
            return Resultado<UsuarioResumo>.Falha(new ErroNegocio(CodigosDeErro.EmailEmUso, "Já existe um usuário com este e-mail."));
        }

        log.LogInformation("Usuário {UsuarioId} criado por {AtorId}.", usuario.Id, comando.AtorId);
        return Resultado<UsuarioResumo>.Ok(Resumo(usuario));
    }

    public async Task<Resultado<UsuarioResumo>> AtualizarAsync(AtualizarUsuarioComando comando, CancellationToken ct = default)
    {
        var nome = comando.Nome?.Trim() ?? "";
        var cpf = Documentos.SomenteDigitos(comando.Cpf);

        var erros = ValidarDadosBasicos(nome, comando.Perfil, comando.Cpf, cpf);
        if (erros.Count > 0) return Resultado<UsuarioResumo>.Falha(erros);

        try
        {
        // Serializable: a contagem de gestores ativos e a alteração acontecem como uma unidade.
        await using var transacao = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var usuario = await usuarios.FindByIdAsync(comando.Id.ToString());
        if (usuario is null) return NaoEncontrado<UsuarioResumo>();

        var campos = new List<string>();
        if (usuario.Nome != nome) campos.Add("nome");
        if (usuario.Perfil != comando.Perfil) campos.Add("perfil");
        if (usuario.Cpf != cpf) campos.Add("cpf");
        if (campos.Count == 0) return Resultado<UsuarioResumo>.Ok(Resumo(usuario));

        var rebaixando = usuario.Perfil == PerfilUsuario.Gestor && comando.Perfil != PerfilUsuario.Gestor;
        if (rebaixando)
        {
            if (comando.Id == comando.AtorId)
            {
                return Resultado<UsuarioResumo>.Falha(new ErroNegocio(CodigosDeErro.NaoPodeAgirEmSi, "Você não pode alterar o seu próprio perfil."));
            }

            if (usuario.Ativo && await ContarGestoresAtivosAsync(ct) <= 1)
            {
                return Resultado<UsuarioResumo>.Falha(new ErroNegocio(CodigosDeErro.UltimoGestor, "O sistema precisa de pelo menos um gestor ativo."));
            }
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        usuario.Nome = nome;
        usuario.Perfil = comando.Perfil;
        usuario.Cpf = cpf;
        usuario.AtualizadoEm = agora;

        var atualizado = await usuarios.UpdateAsync(usuario);
        if (!atualizado.Succeeded)
        {
            return Resultado<UsuarioResumo>.Falha(TradutorDeErrosIdentity.Usuario(atualizado.Errors));
        }

        // O perfil viaja dentro do token: ao mudar, as sessões antigas deixam de valer.
        if (campos.Contains("perfil"))
        {
            await autenticacao.RevogarTodasAsync(usuario.Id, ct);
        }

        RegistrarEvento(TipoEventoCadastro.Editado, usuario.Id, comando.AtorId, agora, string.Join(',', campos));
        await db.SaveChangesAsync(ct);
        await transacao.CommitAsync(ct);

        return Resultado<UsuarioResumo>.Ok(Resumo(usuario));
        }
        catch (Exception ex) when (EhDeadlock(ex))
        {
            return Resultado<UsuarioResumo>.Falha(ConflitoDeConcorrencia());
        }
    }

    public async Task<Resultado> DesativarAsync(Guid id, Guid atorId, CancellationToken ct = default)
    {
        if (id == atorId)
        {
            return Resultado.Falha(new ErroNegocio(CodigosDeErro.NaoPodeAgirEmSi, "Você não pode desativar a sua própria conta."));
        }

        try
        {
        await using var transacao = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var usuario = await usuarios.FindByIdAsync(id.ToString());
        if (usuario is null) return NaoEncontrado();
        if (!usuario.Ativo) return Resultado.Ok(); // idempotente

        if (usuario.Perfil == PerfilUsuario.Gestor && await ContarGestoresAtivosAsync(ct) <= 1)
        {
            return Resultado.Falha(new ErroNegocio(CodigosDeErro.UltimoGestor, "O sistema precisa de pelo menos um gestor ativo."));
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        usuario.Ativo = false;
        usuario.AtualizadoEm = agora;
        var atualizado = await usuarios.UpdateAsync(usuario);
        if (!atualizado.Succeeded) return Resultado.Falha(TradutorDeErrosIdentity.Usuario(atualizado.Errors));

        await autenticacao.RevogarTodasAsync(usuario.Id, ct);
        RegistrarEvento(TipoEventoCadastro.Desativado, usuario.Id, atorId, agora, "ativo");
        await db.SaveChangesAsync(ct);
        await transacao.CommitAsync(ct);

        log.LogInformation("Usuário {UsuarioId} desativado por {AtorId}.", id, atorId);
        return Resultado.Ok();
        }
        catch (Exception ex) when (EhDeadlock(ex))
        {
            return Resultado.Falha(ConflitoDeConcorrencia());
        }
    }

    public async Task<Resultado> ReativarAsync(Guid id, Guid atorId, CancellationToken ct = default)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        var usuario = await usuarios.FindByIdAsync(id.ToString());
        if (usuario is null) return NaoEncontrado();
        if (usuario.Ativo) return Resultado.Ok(); // idempotente

        var agora = relogio.GetUtcNow().UtcDateTime;
        usuario.Ativo = true;
        usuario.AtualizadoEm = agora;
        var atualizado = await usuarios.UpdateAsync(usuario);
        if (!atualizado.Succeeded) return Resultado.Falha(TradutorDeErrosIdentity.Usuario(atualizado.Errors));

        RegistrarEvento(TipoEventoCadastro.Reativado, usuario.Id, atorId, agora, "ativo");
        await db.SaveChangesAsync(ct);
        await transacao.CommitAsync(ct);

        log.LogInformation("Usuário {UsuarioId} reativado por {AtorId}.", id, atorId);
        return Resultado.Ok();
    }

    public async Task<Resultado> RedefinirSenhaAsync(Guid id, string novaSenhaProvisoria, Guid atorId, CancellationToken ct = default)
    {
        if (id == atorId)
        {
            return Resultado.Falha(new ErroNegocio(CodigosDeErro.NaoPodeAgirEmSi, "Para trocar a sua própria senha, use a troca de senha."));
        }

        if (string.IsNullOrWhiteSpace(novaSenhaProvisoria))
        {
            return Resultado.Falha(Validacao("A senha provisória é obrigatória."));
        }

        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        var usuario = await usuarios.FindByIdAsync(id.ToString());
        if (usuario is null) return NaoEncontrado();

        // Valida a política ANTES de trocar o hash (UserManager não expõe "definir senha" sem token).
        var problemas = new List<IdentityError>();
        foreach (var validador in usuarios.PasswordValidators)
        {
            var validacao = await validador.ValidateAsync(usuarios, usuario, novaSenhaProvisoria);
            if (!validacao.Succeeded) problemas.AddRange(validacao.Errors);
        }

        if (problemas.Count > 0) return Resultado.Falha(TradutorDeErrosIdentity.Senha(problemas));

        var agora = relogio.GetUtcNow().UtcDateTime;
        usuario.PasswordHash = usuarios.PasswordHasher.HashPassword(usuario, novaSenhaProvisoria);
        usuario.SecurityStamp = Guid.NewGuid().ToString();
        usuario.DeveTrocarSenha = true;
        usuario.AtualizadoEm = agora;

        var atualizado = await usuarios.UpdateAsync(usuario);
        if (!atualizado.Succeeded) return Resultado.Falha(TradutorDeErrosIdentity.Usuario(atualizado.Errors));

        // Quem esqueceu a senha costuma estar bloqueado por tentativas: libera.
        await usuarios.SetLockoutEndDateAsync(usuario, null);
        await usuarios.ResetAccessFailedCountAsync(usuario);

        await autenticacao.RevogarTodasAsync(usuario.Id, ct);
        RegistrarEvento(TipoEventoCadastro.SenhaRedefinida, usuario.Id, atorId, agora, "senha");
        await db.SaveChangesAsync(ct);
        await transacao.CommitAsync(ct);

        log.LogInformation("Senha do usuário {UsuarioId} redefinida por {AtorId}.", id, atorId);
        return Resultado.Ok();
    }

    // ---------- apoio ----------

    private Task<int> ContarGestoresAtivosAsync(CancellationToken ct) =>
        db.Users.CountAsync(u => u.Perfil == PerfilUsuario.Gestor && u.Ativo, ct);

    private void RegistrarEvento(TipoEventoCadastro tipo, Guid entidadeId, Guid atorId, DateTime quando, string? campos) =>
        db.EventosDeCadastro.Add(new EventoDeCadastro
        {
            Id = Guid.NewGuid(),
            Entidade = Entidade,
            EntidadeId = entidadeId,
            Tipo = tipo,
            AtorId = atorId,
            OcorridoEm = quando,
            Campos = campos
        });

    private static List<ErroNegocio> ValidarDadosBasicos(string nome, PerfilUsuario perfil, string? cpfInformado, string? cpfDigitos)
    {
        var erros = new List<ErroNegocio>();

        if (nome.Length is 0 or > 150) erros.Add(Validacao("O nome é obrigatório e deve ter até 150 caracteres."));
        if (!Enum.IsDefined(perfil)) erros.Add(Validacao("Perfil inválido."));

        var informouCpf = !string.IsNullOrWhiteSpace(cpfInformado);
        if (informouCpf && !Documentos.CpfValido(cpfDigitos)) erros.Add(Validacao("CPF inválido."));

        return erros;
    }

    private static bool EmailValido(string email) =>
        email.Length is > 0 and <= 254
        && MailAddress.TryCreate(email, out var endereco)
        && endereco.Address == email
        && endereco.Host.Contains('.');

    /// <summary>
    /// Dois pedidos simultâneos sob isolamento serializable podem gerar deadlock no SQL Server (erro 1205):
    /// o banco derruba um deles. Isso é o mecanismo funcionando — vira conflito (409) em vez de erro 500.
    /// </summary>
    private static bool EhDeadlock(Exception ex)
    {
        for (var atual = ex; atual is not null; atual = atual.InnerException)
        {
            if (atual is SqlException { Number: 1205 }) return true;
        }

        return false;
    }

    private static ErroNegocio ConflitoDeConcorrencia() =>
        new(CodigosDeErro.Conflito, "Outra alteração simultânea impediu a operação. Tente novamente.");

    private static ErroNegocio Validacao(string mensagem) => new(CodigosDeErro.Validacao, mensagem);

    private static Resultado NaoEncontrado() =>
        Resultado.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Usuário não encontrado."));

    private static Resultado<T> NaoEncontrado<T>() =>
        Resultado<T>.Falha(new ErroNegocio(CodigosDeErro.NaoEncontrado, "Usuário não encontrado."));

    private static UsuarioResumo Resumo(Usuario u) => new(
        u.Id, u.Nome, u.Email ?? "", u.Perfil, u.Cpf, u.Ativo, u.DeveTrocarSenha, u.CriadoEm, u.AtualizadoEm);
}
