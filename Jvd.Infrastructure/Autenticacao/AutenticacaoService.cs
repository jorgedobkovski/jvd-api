using Jvd.Application.Autenticacao;
using Jvd.Application.Comum;
using Jvd.Domain;
using Jvd.Infrastructure.Identity;
using Jvd.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jvd.Infrastructure.Autenticacao;

public sealed class AutenticacaoService(
    JvdDbContext db,
    UserManager<Usuario> usuarios,
    EmissorDeTokens emissor,
    IOptions<JwtOptions> opcoes,
    TimeProvider relogio,
    ILogger<AutenticacaoService> log) : IAutenticacaoService
{
    // Hash de uma senha qualquer, calculado uma vez, usado para gastar o mesmo tempo quando o e-mail
    // não existe (evita descobrir contas pelo tempo de resposta).
    private static readonly Lazy<string> HashFalso = new(() =>
        new PasswordHasher<Usuario>().HashPassword(new Usuario(), Guid.NewGuid().ToString("N") + "Aa1"));

    private readonly JwtOptions _jwt = opcoes.Value;

    public async Task<ResultadoAutenticacao> LoginAsync(LoginComando comando, CancellationToken ct = default)
    {
        var usuario = await usuarios.FindByEmailAsync(comando.Email.Trim());

        if (usuario is null)
        {
            _ = usuarios.PasswordHasher.VerifyHashedPassword(new Usuario(), HashFalso.Value, comando.Senha);
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.CredenciaisInvalidas);
        }

        if (!usuario.Ativo || await usuarios.IsLockedOutAsync(usuario))
        {
            // Mesmo custo de uma senha errada: sem isso, conta inativa/bloqueada responderia bem mais rápido
            // e o tempo de resposta revelaria que a conta existe (auditoria P2-1).
            _ = usuarios.PasswordHasher.VerifyHashedPassword(new Usuario(), HashFalso.Value, comando.Senha);
            log.LogWarning("Login recusado (conta inativa ou bloqueada) para o usuário {UsuarioId}", usuario.Id);
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.CredenciaisInvalidas);
        }

        if (!await usuarios.CheckPasswordAsync(usuario, comando.Senha))
        {
            await usuarios.AccessFailedAsync(usuario);
            log.LogWarning("Senha incorreta para o usuário {UsuarioId}", usuario.Id);
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.CredenciaisInvalidas);
        }

        await usuarios.ResetAccessFailedCountAsync(usuario);
        var (tokens, _) = await EmitirAsync(usuario, ct);
        return ResultadoAutenticacao.Ok(tokens);
    }

    public async Task<ResultadoAutenticacao> RenovarAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = EmissorDeTokens.CalcularHash(refreshToken);
        var agora = relogio.GetUtcNow().UtcDateTime;

        var atual = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (atual is null)
        {
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.TokenInvalido);
        }

        // Revogado DE PROPÓSITO (logout, troca/redefinição de senha, desativação, mudança de perfil): só recusa.
        // Um aparelho esquecido com sessão antiga não pode derrubar as sessões novas do usuário.
        if (atual.RevogadoEm is not null)
        {
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.TokenInvalido);
        }

        // Já usado (rotacionado) e apresentado de novo: pedido simultâneo ou possível roubo.
        if (atual.UsadoEm is not null)
        {
            return await TratarReapresentacaoAsync(atual, agora, ct);
        }

        if (atual.ExpiraEm <= agora)
        {
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.TokenInvalido);
        }

        var usuario = await usuarios.FindByIdAsync(atual.UsuarioId.ToString());
        if (usuario is null || !usuario.Ativo || await usuarios.IsLockedOutAsync(usuario))
        {
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.TokenInvalido);
        }

        // Consumir o token antigo e gravar o novo é UMA operação: se a gravação do novo falhar (queda de
        // conexão, cancelamento), o antigo volta a valer e o cliente pode tentar de novo sem ser tratado
        // como ladrão (auditoria P1-1).
        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        // Consumo atômico: só um dos pedidos simultâneos com o mesmo token consegue marcar como usado.
        var consumidos = await db.RefreshTokens
            .Where(t => t.Id == atual.Id && t.UsadoEm == null && t.RevogadoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsadoEm, agora), ct);

        if (consumidos != 1)
        {
            await transacao.RollbackAsync(ct);
            var relido = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == atual.Id, ct);
            if (relido?.RevogadoEm is not null)
            {
                return ResultadoAutenticacao.Erro(FalhaAutenticacao.TokenInvalido);
            }

            return await TratarReapresentacaoAsync(relido ?? atual, agora, ct);
        }

        var (tokens, novoId) = await EmitirAsync(usuario, ct);

        Guid? substituidoPor = novoId;
        await db.RefreshTokens
            .Where(t => t.Id == atual.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SubstituidoPorId, substituidoPor), ct);

        await transacao.CommitAsync(ct);
        return ResultadoAutenticacao.Ok(tokens);
    }

    public async Task EncerrarSessaoAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = EmissorDeTokens.CalcularHash(refreshToken);
        var agora = relogio.GetUtcNow().UtcDateTime;

        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevogadoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEm, agora), ct);
    }

    public async Task RevogarTodasAsync(Guid usuarioId, CancellationToken ct = default)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        await db.RefreshTokens
            .Where(t => t.UsuarioId == usuarioId && t.RevogadoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevogadoEm, agora), ct);
    }

    /// <summary>
    /// Troca a PRÓPRIA senha: confere a senha atual (errar conta como falha de login), aplica a política, limpa a
    /// obrigação de troca, revoga todas as sessões e emite tokens novos — tudo numa só transação.
    /// </summary>
    public async Task<Resultado<TokensEmitidos>> TrocarSenhaAsync(
        Guid usuarioId, string senhaAtual, string novaSenha, CancellationToken ct = default)
    {
        var usuario = await usuarios.FindByIdAsync(usuarioId.ToString());
        if (usuario is null || !usuario.Ativo || await usuarios.IsLockedOutAsync(usuario))
        {
            return Resultado<TokensEmitidos>.Falha(new ErroNegocio(CodigosDeErro.SessaoInvalida, "Sessão inválida."));
        }

        // Quem tem só um access token roubado não pode adivinhar a senha atual à vontade:
        // errar aqui conta como falha de login e leva ao bloqueio da conta.
        if (!await usuarios.CheckPasswordAsync(usuario, senhaAtual))
        {
            await usuarios.AccessFailedAsync(usuario);
            log.LogWarning("Senha atual incorreta na troca de senha do usuário {UsuarioId}", usuario.Id);
            return Resultado<TokensEmitidos>.Falha(new ErroNegocio(CodigosDeErro.SenhaAtualIncorreta, "A senha atual está incorreta."));
        }

        if (string.Equals(senhaAtual, novaSenha, StringComparison.Ordinal))
        {
            return Resultado<TokensEmitidos>.Falha(new ErroNegocio(CodigosDeErro.NovaSenhaIgualAtual, "A nova senha precisa ser diferente da atual."));
        }

        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        var troca = await usuarios.ChangePasswordAsync(usuario, senhaAtual, novaSenha);
        if (!troca.Succeeded)
        {
            return Resultado<TokensEmitidos>.Falha(TradutorDeErrosIdentity.Senha(troca.Errors));
        }

        var agora = relogio.GetUtcNow().UtcDateTime;
        usuario.DeveTrocarSenha = false;
        usuario.AtualizadoEm = agora;
        var atualizado = await usuarios.UpdateAsync(usuario);
        if (!atualizado.Succeeded)
        {
            return Resultado<TokensEmitidos>.Falha(TradutorDeErrosIdentity.Usuario(atualizado.Errors));
        }

        await usuarios.ResetAccessFailedCountAsync(usuario);
        await RevogarTodasAsync(usuario.Id, ct);

        db.EventosDeCadastro.Add(new EventoDeCadastro
        {
            Id = Guid.NewGuid(),
            Entidade = "usuario",
            EntidadeId = usuario.Id,
            Tipo = TipoEventoCadastro.SenhaTrocada,
            AtorId = usuario.Id,
            OcorridoEm = agora,
            Campos = "senha"
        });

        var (tokens, _) = await EmitirAsync(usuario, ct); // SaveChanges grava também o evento
        await transacao.CommitAsync(ct);

        return Resultado<TokensEmitidos>.Ok(tokens);
    }

    /// <summary>
    /// Refresh token já usado ou revogado foi apresentado de novo. Dentro da janela de tolerância após o
    /// uso legítimo (e sem revogação), é só um pedido simultâneo: falha com 401 e NÃO derruba as sessões
    /// (auditoria P1-2). Fora dela é reúso = possível roubo: revoga todas as sessões do usuário.
    /// </summary>
    private async Task<ResultadoAutenticacao> TratarReapresentacaoAsync(RefreshToken token, DateTime agora, CancellationToken ct)
    {
        var tolerancia = TimeSpan.FromSeconds(_jwt.RefreshToleranciaSegundos);
        var pedidoSimultaneo = tolerancia > TimeSpan.Zero // 0 desliga a tolerância
                               && token.RevogadoEm is null
                               && token.UsadoEm is { } usadoEm
                               && agora - usadoEm <= tolerancia;

        if (pedidoSimultaneo)
        {
            log.LogInformation("Refresh token reapresentado dentro da janela de tolerância (usuário {UsuarioId}); sessões mantidas.", token.UsuarioId);
            return ResultadoAutenticacao.Erro(FalhaAutenticacao.TokenInvalido);
        }

        await RevogarTodasAsync(token.UsuarioId, ct);
        log.LogWarning("Reúso de refresh token detectado; sessões do usuário {UsuarioId} foram revogadas", token.UsuarioId);
        return ResultadoAutenticacao.Erro(FalhaAutenticacao.TokenInvalido);
    }

    private async Task<(TokensEmitidos Tokens, Guid RefreshTokenId)> EmitirAsync(Usuario usuario, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        var (accessToken, accessExpira) = emissor.GerarAccessToken(usuario);

        var refreshEmClaro = EmissorDeTokens.GerarRefreshTokenOpaco();
        var refreshExpira = agora.AddDays(_jwt.RefreshTokenDias);
        var refreshId = Guid.NewGuid();

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = refreshId,
            UsuarioId = usuario.Id,
            TokenHash = EmissorDeTokens.CalcularHash(refreshEmClaro),
            CriadoEm = agora,
            ExpiraEm = refreshExpira
        });
        await db.SaveChangesAsync(ct);

        var tokens = new TokensEmitidos(
            accessToken, accessExpira,
            refreshEmClaro, refreshExpira,
            new UsuarioAutenticado(usuario.Id, usuario.Nome, usuario.Perfil, usuario.DeveTrocarSenha));

        return (tokens, refreshId);
    }
}
