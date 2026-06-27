using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamPhantom.Models;

namespace SteamPhantom.Services;

/// <summary>
/// Thin wrapper around SteamKit2 3.0+: connect → credentials auth (with
/// Steam Guard via IAuthenticator) → token-based logon → owned-games
/// fetch via the same unified-messages call Steam's own client uses.
/// No Web API key.
/// </summary>
public class SteamKitClient : IAsyncDisposable
{
    private readonly SteamClient _client;
    private readonly CallbackManager _manager;
    private readonly SteamUser _steamUser;
    private readonly SteamUnifiedMessages _unified;

    private readonly CancellationTokenSource _pumpCts = new();
    private Task? _pumpTask;
    private bool _disposed;

    public bool IsLoggedOn { get; private set; }
    public ulong SteamId64 { get; private set; }
    public string? AccountName { get; private set; }

    public SteamKitClient()
    {
        _client = new SteamClient();
        _manager = new CallbackManager(_client);
        _steamUser = _client.GetHandler<SteamUser>()
            ?? throw new InvalidOperationException("SteamUser handler missing.");
        _unified = _client.GetHandler<SteamUnifiedMessages>()
            ?? throw new InvalidOperationException("SteamUnifiedMessages handler missing.");
    }

    private void StartPump()
    {
        if (_pumpTask is not null) return;
        _pumpTask = Task.Run(() =>
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                try { _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50)); }
                catch { /* swallow; transient connection blips */ }
            }
        });
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        StartPump();
        if (_client.IsConnected) return;

        var tcs = new TaskCompletionSource();
        IDisposable? connSub = null, disSub = null;
        connSub = _manager.Subscribe<SteamClient.ConnectedCallback>(_ => tcs.TrySetResult());
        disSub  = _manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetException(new InvalidOperationException("Steam disconnected before login."));
        });

        try
        {
            _client.Connect();
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        finally
        {
            connSub?.Dispose();
            disSub?.Dispose();
        }
    }

    public async Task<AuthResult> LoginWithCredentialsAsync(
        string username, string password, IAuthenticator authenticator, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var authSession = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(
            new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = authenticator,
                // CRITICAL: tokens issued for non-SteamClient platforms are
                // rejected by SteamUser.LogOn with EResult.AccessDenied. We need
                // a SteamClient-scoped token to do re-logons across launches.
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
            }).ConfigureAwait(false);

        var poll = await authSession.PollingWaitForResultAsync(ct).ConfigureAwait(false);

        await LogOnWithTokenAsync(poll.AccountName, poll.RefreshToken, ct).ConfigureAwait(false);
        return new AuthResult(poll.AccountName, poll.RefreshToken);
    }

    public async Task LogOnWithTokenAsync(string accountName, string refreshToken, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<EResult>();
        var sub = _manager.Subscribe<SteamUser.LoggedOnCallback>(cb => tcs.TrySetResult(cb.Result));
        try
        {
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = accountName,
                AccessToken = refreshToken,
            });

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (result != EResult.OK)
                throw new InvalidOperationException($"Steam login failed: {result}.");

            IsLoggedOn = true;
            SteamId64 = _steamUser.SteamID?.ConvertToUInt64() ?? 0;
            AccountName = accountName;
        }
        finally
        {
            sub.Dispose();
        }
    }

    public async Task<IReadOnlyList<OwnedGame>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        if (!IsLoggedOn) throw new InvalidOperationException("Not signed in.");

        var playerService = _unified.CreateService<Player>();
        var request = new CPlayer_GetOwnedGames_Request
        {
            steamid = SteamId64,
            include_appinfo = true,
            include_played_free_games = true,
        };

        var response = await playerService.GetOwnedGames(request);
        var games = response.Body.games ?? new List<CPlayer_GetOwnedGames_Response.Game>();

        return games
            .Select(g => new OwnedGame
            {
                AppId = (uint)g.appid,
                Name = string.IsNullOrEmpty(g.name) ? $"App {g.appid}" : g.name,
                PlaytimeMinutes = (int)g.playtime_forever,
                IconHash = g.img_icon_url ?? string.Empty,
            })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void LogOff()
    {
        if (IsLoggedOn)
        {
            try { _steamUser.LogOff(); } catch { }
            IsLoggedOn = false;
            AccountName = null;
            SteamId64 = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        LogOff();
        _pumpCts.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); } catch { }
        }
        try { _client.Disconnect(); } catch { }
        _pumpCts.Dispose();
    }
}

public record AuthResult(string AccountName, string RefreshToken);
