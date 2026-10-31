using Jvd.Application.Autenticacao;
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

        if (atual.UsadoEm is not null || atual.RevogadoEm is not null)
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
            new UsuarioAutenticado(usuario.Id, usuario.Nome, usuario.Perfil));

        return (tokens, refreshId);
    }
}
