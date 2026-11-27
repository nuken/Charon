using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Charon.Config;
using Charon.Tuner;

namespace Charon.Providers;

public sealed class SessionHealthService : IAsyncDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly CharonConfig _config;
    private readonly ProviderManager _providers;
    private readonly ChromeTuner _tuner;
    private readonly Func<bool> _isServerIdle;
    private readonly Action<string> _log;
    private readonly Action<string, string, bool> _notifyUser;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly Dictionary<string, DateTime> _lastCheckedUtc = new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan GetCheckInterval(string providerId)
    {
        int hours = Math.Clamp(_config.SessionCheckIntervalHours, 1, 168);
        if (providerId.Contains("espn", StringComparison.OrdinalIgnoreCase))
        {
            hours = Math.Min(hours, 3);
        }
        else if (providerId.Contains("cnn", StringComparison.OrdinalIgnoreCase) ||
                 providerId.Contains("cbs", StringComparison.OrdinalIgnoreCase) ||
                 providerId.Contains("abc", StringComparison.OrdinalIgnoreCase))
        {
            hours = Math.Min(hours, 5);
        }
        return TimeSpan.FromHours(hours);
    }

    public SessionHealthService(
        CharonConfig config,
        ProviderManager providers,
        ChromeTuner tuner,
        Func<bool> isServerIdle,
        Action<string> log,
        Action<string, string, bool> notifyUser)
    {
        _config = config;
        _providers = providers;
        _tuner = tuner;
        _isServerIdle = isServerIdle;
        _log = log;
        _notifyUser = notifyUser;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public Task RunCheckNowAsync() => RunPassAsync(force: true, _cts?.Token ?? CancellationToken.None);

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // Wait 5 minutes after startup before checking if a pass is due
            await Task.Delay(TimeSpan.FromMinutes(5), ct);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
            do
            {
                if (!_config.EnableSessionHealthCheck)
                    continue;

                await RunPassAsync(force: false, ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunPassAsync(bool force, CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct))
        {
            if (force) _log("[Health] A session health check is already running.");
            return;
        }

        try
        {
            // Only rotate through enabled providers that have at least one visible (non-hidden) channel
            var activeProviders = _providers.Providers
                .Where(p => p.Manifest.Enabled &&
                            !string.IsNullOrWhiteSpace(p.Manifest.LoginUrl) &&
                            p.Channels.Any(c => !c.IsHidden))
                .ToList();

            var now = DateTime.UtcNow;
            var dueProviders = force
                ? activeProviders
                : activeProviders
                    .Where(p => !_lastCheckedUtc.TryGetValue(p.Manifest.Id, out var last) ||
                                (now - last) >= GetCheckInterval(p.Manifest.Id))
                    .ToList();

            if (dueProviders.Count == 0)
            {
                if (force) _log("[Health] No active providers with visible channels to check.");
                return;
            }

            if (!_isServerIdle())
            {
                _log("[Health] Skipping session keep-alive pass because a tuner is currently active (will retry when idle).");
                return;
            }

            _log($"[Health] Starting idle keep-alive & session check for {dueProviders.Count} due provider(s)...");

            var expiredNames = new List<string>();
            var expiringNames = new List<string>();

            string tuner1Profile = _tuner.GetTunerProfilePath(1);
            int cdpPort = FindFreeLoopbackPort(9430);
            int posX = _config.WindowPlacement == WindowPlacementMode.VisibleDebug ? 80 : -3200;
            int posY = _config.WindowPlacement == WindowPlacementMode.VisibleDebug ? 80 : 0;

            string args = string.Join(" ",
            [
                $"--user-data-dir=\"{tuner1Profile}\"",
                $"--remote-debugging-port={cdpPort}",
                "--remote-allow-origins=*",
                "--disable-extensions",
                $"--window-position={posX},{posY}",
                "--window-size=1280,800",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-blink-features=AutomationControlled",
                "--autoplay-policy=no-user-gesture-required",
                "--disable-background-media-suspend",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
                "--mute-audio",
                "\"about:blank\""
            ]);

            Process? chromeProc = null;
            string? wsUrl = null;

            try
            {
                chromeProc = Process.Start(new ProcessStartInfo
                {
                    FileName = _config.ResolvedChromePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                wsUrl = await WaitForPageWebSocketUrlAsync(cdpPort, TimeSpan.FromSeconds(15), ct);
                if (string.IsNullOrWhiteSpace(wsUrl))
                {
                    _log("[Health] Could not attach to Chrome (Tuner_1 may be open in a Login window). Deferring check.");
                    return;
                }

                ClientWebSocket? ws = null;
                int cmdId = 1;

                async Task<ClientWebSocket> EnsureConnectedAsync()
                {
                    if (ws is { State: WebSocketState.Open })
                        return ws;

                    ws?.Dispose();
                    ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(wsUrl), ct);
                    await SendCdpCommandAsync(ws, cmdId++, "Page.enable", new { }, ct);
                    await SendCdpCommandAsync(ws, cmdId++, "Emulation.setFocusEmulationEnabled", new { enabled = true }, ct);
                    return ws;
                }

                try
                {
                    foreach (var provider in dueProviders)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Abort immediately if a client starts streaming while we're rotating
                        if (!_isServerIdle())
                        {
                            _log("[Health] A tuner became active mid-check — stopping keep-alive pass immediately.");
                            return;
                        }

                        var activeWs = await EnsureConnectedAsync();

                        bool isCbs = provider.Manifest.Id.Contains("cbs", StringComparison.OrdinalIgnoreCase);
                        bool isCnn = provider.Manifest.Id.Contains("cnn", StringComparison.OrdinalIgnoreCase);
                        bool isEspn = provider.Manifest.Id.Contains("espn", StringComparison.OrdinalIgnoreCase);
                        bool isSpectrum = provider.Manifest.Id.Contains("spectrum", StringComparison.OrdinalIgnoreCase);

                        string espnChannelUrl = provider.Channels
                            .FirstOrDefault(c => !c.IsHidden && !string.IsNullOrWhiteSpace(c.Url))?.Url
                            ?? provider.Manifest.LoginUrl;

                        string checkUrl = isCbs
                            ? "https://www.cbs.com/live-tv/stream/tveverywhere/"
                            : isCnn
                                ? "https://www.cnn.com/videos/live"
                                : isEspn
                                    ? espnChannelUrl
                                    : provider.Manifest.LoginUrl;

                        await SendCdpCommandAsync(activeWs, cmdId++, "Page.navigate", new { url = checkUrl }, ct);

                        string? json = null;
                        if (isCnn || isCbs || isEspn || isSpectrum)
                        {
                            // Give heavy TVE players & Spectrum's guide 4s to commit navigation before polling
                            await Task.Delay(TimeSpan.FromSeconds(4), ct);

                            var deadline = DateTime.UtcNow.AddSeconds(isSpectrum ? 22 : 30);
                            while (DateTime.UtcNow < deadline)
                            {
                                if (!_isServerIdle())
                                {
                                    _log("[Health] A tuner became active mid-check — stopping keep-alive pass immediately.");
                                    return;
                                }

                                activeWs = await EnsureConnectedAsync();
                                json = await EvaluateJsStringAsync(activeWs, cmdId++, GetHealthCheckJs(provider.Manifest.Id), ct);
                                if (!string.IsNullOrWhiteSpace(json) && !json.Contains("\"state\":\"pending\"", StringComparison.Ordinal))
                                    break;

                                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                            }

                            // Let live TVE streams actually run for 8s after [OK] so refreshed tokens/cookies settle before unloading
                            if ((isCnn || isCbs || isEspn) &&
                                !string.IsNullOrWhiteSpace(json) &&
                                json.Contains("\"state\":\"ok\"", StringComparison.Ordinal))
                            {
                                for (int i = 0; i < 4; i++)
                                {
                                    if (!_isServerIdle())
                                    {
                                        _log("[Health] A tuner became active mid-check — stopping keep-alive pass immediately.");
                                        return;
                                    }
                                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                                }
                            }

                            // Unload active video/DRM stream before navigating to the next provider
                            if ((isCnn || isCbs || isEspn) && ws is { State: WebSocketState.Open })
                            {
                                try
                                {
                                    await SendCdpCommandAsync(ws, cmdId++, "Page.navigate", new { url = "about:blank" }, ct);
                                    await Task.Delay(500, ct);
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(6), ct);
                            json = await EvaluateJsStringAsync(activeWs, cmdId++, GetHealthCheckJs(provider.Manifest.Id), ct);
                        }

                        ParseHealthResult(provider, json, expiredNames, expiringNames);
                        _lastCheckedUtc[provider.Manifest.Id] = DateTime.UtcNow;
                    }
                }
                finally
                {
                    ws?.Dispose();
                }
            }
            finally
            {
                if (wsUrl is not null)
                {
                    try
                    {
                        using var closeWs = new ClientWebSocket();
                        await closeWs.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                        await SendCdpCommandAsync(closeWs, 9999, "Browser.close", new { }, CancellationToken.None);
                        await Task.Delay(300, CancellationToken.None);
                    }
                    catch { }
                }

                if (chromeProc is { HasExited: false })
                {
                    try { chromeProc.WaitForExit(2000); } catch { }
                    if (!chromeProc.HasExited)
                    {
                        try { chromeProc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }

            // Push refreshed cookies/tokens from Tuner_1 out to Tuners 2–4
            if (_isServerIdle())
            {
                _tuner.SyncMasterAuthToAllTuners(_log);
            }

            // Fire Windows tray notification if any provider needs attention
            if (expiredNames.Count > 0 || expiringNames.Count > 0)
            {
                var parts = new List<string>();
                if (expiredNames.Count > 0)
                    parts.Add($"Re-login needed: {string.Join(", ", expiredNames)}");
                if (expiringNames.Count > 0)
                    parts.Add($"Expiring soon: {string.Join(", ", expiringNames)}");

                string message = string.Join(" | ", parts);
                _log($"[Health] ALERT: {message}");
                _notifyUser("Charon — Provider Login Attention", message, true);
            }
            else if (force)
            {
                _notifyUser("Charon — All Sessions Healthy",
                    $"Verified {dueProviders.Count} active provider(s) and synced Tuners 2–4.", false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log($"[Health] Error during session check: {ex.Message}");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private void ParseHealthResult(
        LoadedProvider provider,
        string? json,
        List<string> expiredNames,
        List<string> expiringNames)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            _log($"[Health] [??] {provider.Manifest.Name}: No response from page check.");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            string state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() ?? "ok" : "ok";
            string detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";

            switch (state)
            {
                case "expired":
                    expiredNames.Add(provider.Manifest.Name);
                    _log($"[Health] [EXPIRED] {provider.Manifest.Name}: {detail}");
                    break;
                case "expiring":
                    expiringNames.Add(provider.Manifest.Name);
                    _log($"[Health] [EXPIRING] {provider.Manifest.Name}: {detail}");
                    break;
				case "pending":
                    _log($"[Health] [??] {provider.Manifest.Name}: Timed out waiting for session check ({detail})");
                    break;
                default:
                    _log($"[Health] [OK] {provider.Manifest.Name}: Session active ({detail})");
                    break;
            }
        }
        catch
        {
            _log($"[Health] [OK] {provider.Manifest.Name}: Visited keep-alive page.");
        }
    }

    private static string GetHealthCheckJs(string providerId) => $$"""
        (async () => {
          const id = {{JsonSerializer.Serialize(providerId.ToLowerInvariant())}};
          const href = location.href;
          const path = location.pathname.toLowerCase();

          // 1. Spectrum: validate oauth_token server-side; on stale token, drop SAL.t/token keys (keeping SAL.d.v) and reload once
          if (id.includes('spectrum')) {
            if (/id\.spectrum\.net/i.test(location.hostname) || /\/login\b/i.test(path)) {
              return JSON.stringify({ state: 'expired', detail: `Redirected to ${location.hostname}${path}` });
            }

            const clean = (s) => (s || '').replace(/^["']|["']$/g, '').trim();
            const token = clean(localStorage.getItem('oauth_token'));
            const refreshToken = clean(localStorage.getItem('xoauth_refresh_token'));
            const deviceId = clean(localStorage.getItem('device_id'));
            const expMs = Number(clean(localStorage.getItem('xoauth_token_expiration')));
            const hoursLeft = expMs > 0 ? (expMs - Date.now()) / 3600000 : null;
            const triedRenew = sessionStorage.getItem('__charonSpectrumRenewTried') === '1';

            const triggerSilentRenew = (reason) => {
              sessionStorage.setItem('__charonSpectrumRenewTried', '1');
              ['SAL.t', 'oauth_token', 'xoauth_refresh_token', 'xoauth_token_expiration']
                .forEach(k => { try { localStorage.removeItem(k); } catch {} });
              setTimeout(() => location.reload(), 50);
              return JSON.stringify({ state: 'pending', detail: `${reason} — dropped SAL.t, reloading for silent renewal...` });
            };

            if (!token) {
              if (triedRenew) {
                return JSON.stringify({ state: 'pending', detail: 'Waiting for Spectrum silent token renewal...' });
              }
              return JSON.stringify({ state: 'expired', detail: 'No oauth_token in localStorage' });
            }

            let isValid = null;
            try {
              const resp = await fetch('https://apis.spectrum.net/auth/oauth/v2/validateSession?getLocation=false', {
                headers: {
                  'x-access-token': token,
                  'x-refresh-token': refreshToken,
                  'x-client-device-id': deviceId,
                  'x-client-id': 'stva-ovp'
                }
              });
              if (resp.ok) {
                const body = await resp.json();
                isValid = body.isValidSession === true;
              } else if (resp.status === 401 || resp.status === 403) {
                isValid = false;
              }
            } catch {}

            if (isValid === false || (hoursLeft !== null && hoursLeft <= 0)) {
              if (!triedRenew) {
                return triggerSilentRenew(isValid === false ? 'validateSession=false' : 'token expired');
              }
              return JSON.stringify({ state: 'expired', detail: 'Spectrum token invalid after silent renewal attempt' });
            }

            const tag = isValid === true ? 'verified' : 'local';
            if (hoursLeft !== null) {
              if (hoursLeft <= 3) return JSON.stringify({ state: 'expiring', detail: `${hoursLeft.toFixed(1)}h remaining (${tag})` });
              return JSON.stringify({ state: 'ok', detail: `${hoursLeft.toFixed(1)}h remaining (${tag})` });
            }
            return JSON.stringify({ state: 'ok', detail: `oauth_token present (${tag})` });
          }

          // 2. CNN: require active video playback so WBD Bolt tokenExchange completes; reload once if player fails to mount
          if (id.includes('cnn')) {
            const bodyText = document.body?.innerText || '';
            if ((/have a tv provider\?/i.test(bodyText) && /connect now/i.test(bodyText)) ||
                /connect your tv provider|link your tv provider|sign in with your tv provider|select your tv provider/i.test(bodyText)) {
              return JSON.stringify({ state: 'expired', detail: 'CNN TV provider sign-in required' });
            }

            const adobeRe = /sp\.auth\.adobe\.com\/adobe-services\/(shortAuthorize|authorize)/i;
            const hitShortAuth = performance.getEntriesByType('resource')
              .some(r => adobeRe.test(r.name));

            const videos = Array.from(document.querySelectorAll('video'));
            const hasPlayingVideo = videos.some(v => v.readyState >= 2 && v.currentTime >= 2);

            if (hasPlayingVideo) {
              sessionStorage.removeItem('__charonCnnReloaded');
              return JSON.stringify({
                state: 'ok',
                detail: hitShortAuth ? 'shortAuthorize + stream playing' : 'stream playing'
              });
            }

            // If the Watch shell loaded with no <video> and no shortAuthorize after 10s, reload once
            window.__charonCnnFirstSeenAt = window.__charonCnnFirstSeenAt || Date.now();
            if (videos.length === 0 && !hitShortAuth &&
                Date.now() - window.__charonCnnFirstSeenAt >= 10000 &&
                sessionStorage.getItem('__charonCnnReloaded') !== '1') {
              sessionStorage.setItem('__charonCnnReloaded', '1');
              setTimeout(() => location.reload(), 50);
              return JSON.stringify({ state: 'pending', detail: 'No video mounted after 10s — reloading /videos/cnn once...' });
            }

            for (const v of videos) {
              if (v.paused) {
                v.muted = true;
                v.play().catch(() => {});
              }
            }
            const playBtn = document.querySelector(
              'button[aria-label*="Play" i], button[title*="Play" i], [data-testid*="play" i]'
            );
            if (playBtn && Date.now() - (window.__charonCnnHealthClickAt || 0) > 3000) {
              window.__charonCnnHealthClickAt = Date.now();
              playBtn.click();
            }

            const snippet = bodyText.replace(/\s+/g, ' ').trim().slice(0, 100);
            return JSON.stringify({
              state: 'pending',
              detail: `url=${location.pathname} videos=${videos.length} shortAuth=${hitShortAuth} text="${snippet}"`
            });
          }

          // 2b. CBS: allow 12s grace for 6h accessToken refresh when 30d entitlementToken is present; require active playback
          if (id.includes('cbs')) {
            const bodyText = document.body?.innerText || '';
            const hasEntitlementToken = !!localStorage.getItem('entitlementToken');
            window.__charonCbsFirstSeenAt = window.__charonCbsFirstSeenAt || Date.now();
            const elapsedMs = Date.now() - window.__charonCbsFirstSeenAt;

            if (/error with this account'?s settings|unable to continue|sign in with your tv provider|verify your tv provider|choose your tv provider/i.test(bodyText)) {
              if (hasEntitlementToken && elapsedMs < 12000) {
                return JSON.stringify({ state: 'pending', detail: 'Waiting for CBS accessToken refresh...' });
              }
              return JSON.stringify({ state: 'expired', detail: 'CBS TVE session expired or provider sign-in required' });
            }

            const queryAllDeep = (selector, root = document) => {
              const results = Array.from(root.querySelectorAll(selector));
              for (const el of root.querySelectorAll('*')) {
                if (el.shadowRoot) results.push(...queryAllDeep(selector, el.shadowRoot));
              }
              return results;
            };

            const startBtn = queryAllDeep('button, a, div[role="button"], span, .button')
              .find(el => {
                const t = (el.textContent || '').trim().toLowerCase();
                return t === 'start watching' || t === 'start watching »' ||
                       el.getAttribute('aria-label')?.toLowerCase() === 'start watching';
              });
            if (startBtn && Date.now() - (window.__charonCbsHealthClickAt || 0) > 2500) {
              window.__charonCbsHealthClickAt = Date.now();
              startBtn.click();
              return JSON.stringify({ state: 'pending', detail: `Clicked Start Watching on ${location.pathname}` });
            }

            const hasMvpdAuthz = document.cookie.includes('mvpd_authz=');
            const hasCbsCom = document.cookie.includes('CBS_COM=');
            const videos = queryAllDeep('video');
            const hasPlayingVideo = videos.some(v => v.readyState >= 2 && v.currentTime >= 2);

            if ((hasMvpdAuthz || hasCbsCom) && hasPlayingVideo) {
              const authTag = hasMvpdAuthz ? 'mvpd_authz' : 'CBS_COM';
              return JSON.stringify({ state: 'ok', detail: `${authTag} + stream playing (${location.pathname})` });
            }

            for (const v of videos) {
              if (v.paused) {
                v.muted = true;
                v.play().catch(() => {});
              }
            }

            const snippet = bodyText.replace(/\s+/g, ' ').trim().slice(0, 80);
            return JSON.stringify({
              state: 'pending',
              detail: `url=${location.pathname} mvpd_authz=${hasMvpdAuthz} CBS_COM=${hasCbsCom} videos=${videos.length} text="${snippet}"`
            });
          }

          // 2c. ESPN: support MyDisney/ESPN Unlimited playback AND auto-recover YouTube TV Adobe Pass re-auth
          if (id.includes('espn')) {
            const queryAllDeep = (selector, root = document) => {
              const results = Array.from(root.querySelectorAll(selector));
              for (const el of root.querySelectorAll('*')) {
                if (el.shadowRoot) results.push(...queryAllDeep(selector, el.shadowRoot));
              }
              return results;
            };

            // If redirected to Google OAuth during automatic YouTube TV re-auth, click the signed-in Google account tile
            if (/accounts\.google\.com/i.test(location.hostname)) {
              const acctTile = document.querySelector('[data-identifier]');
              if (acctTile && !window.__charonEspnGoogleClicked) {
                window.__charonEspnGoogleClicked = true;
                acctTile.click();
                return JSON.stringify({ state: 'pending', detail: 'Selected signed-in Google account for ESPN...' });
              }
              return JSON.stringify({ state: 'pending', detail: 'Waiting on Google OAuth for ESPN...' });
            }

            const bodyText = document.body?.innerText || '';
            const reauthStep = sessionStorage.getItem('__charonEspnReauthStep') || '0';

            // Auto-recover if "User must re-authenticate" modal appears
            if (/user must re-authenticate/i.test(bodyText)) {
              if (reauthStep === '0') {
                const okBtn = queryAllDeep('button, a, div[role="button"]').find(el =>
                  /^(ok|continue)$/i.test((el.textContent || '').trim()) && el.getBoundingClientRect().width > 0
                );
                if (okBtn) {
                  sessionStorage.setItem('__charonEspnReauthStep', '1');
                  okBtn.click();
                  return JSON.stringify({ state: 'pending', detail: 'Clicked OK on ESPN re-auth prompt...' });
                }
              } else if (reauthStep === '2') {
                sessionStorage.removeItem('__charonEspnReauthStep');
                return JSON.stringify({ state: 'expired', detail: 'ESPN still requires re-auth after automatic YouTube TV retry' });
              }
            }

            // If TV provider picker opens during auto-reauth, click YouTube TV
            if (/select your tv provider|choose your tv provider/i.test(bodyText)) {
              if (reauthStep === '1') {
                const yttvBtn = queryAllDeep('button, a, li, div[role="button"]').find(el =>
                  /youtube tv/i.test((el.textContent || '').trim()) && el.getBoundingClientRect().width > 0
                );
                if (yttvBtn) {
                  sessionStorage.setItem('__charonEspnReauthStep', '2');
                  yttvBtn.click();
                  return JSON.stringify({ state: 'pending', detail: 'Selecting YouTube TV in ESPN provider picker...' });
                }
              }
              return JSON.stringify({ state: 'expired', detail: 'ESPN TV provider sign-in required' });
            }

            // On /watch/catalog/<uuid>, click into the Live Now tile (avoiding Upcoming/ESPN+ tiles)
            if (location.pathname.startsWith('/watch/catalog/')) {
              const allPlayerLinks = Array.from(document.querySelectorAll('a[href*="/watch/player/"]'));
              const liveLink =
                allPlayerLinks.find(a => a.getAttribute('href')?.includes('#bucketId=1')) ||
                allPlayerLinks.find(a => /\blive\b/i.test(a.textContent || '') && !/upcoming|espn\+/i.test(a.textContent || '')) ||
                allPlayerLinks.find(a => !/upcoming|espn\+/i.test(a.textContent || '')) ||
                allPlayerLinks[0];
              if (liveLink && Date.now() - (window.__charonEspnHealthNavAt || 0) > 3000) {
                window.__charonEspnHealthNavAt = Date.now();
                liveLink.click();
                return JSON.stringify({ state: 'pending', detail: `Opening live ESPN stream (${liveLink.getAttribute('href')})` });
              }
              return JSON.stringify({ state: 'pending', detail: `Waiting for live tile on ${location.pathname}` });
            }

            let profile = null;
            try {
              profile = JSON.parse(sessionStorage.getItem('adobe_pass_profile') || 'null');
            } catch {}

            const videos = queryAllDeep('video');
            const hasPlayingVideo = videos.some(v => v.readyState >= 2 && v.currentTime >= 2);

            if (hasPlayingVideo) {
              sessionStorage.removeItem('__charonEspnReauthStep');
              if (profile && profile.mvpd) {
                const notAfter = Number(profile.notAfter || 0);
                const expMs = notAfter > 1e12 ? notAfter : notAfter * 1000;
                if (expMs > 0) {
                  const hoursLeft = (expMs - Date.now()) / 3600000;
                  if (hoursLeft <= 0) {
                    return JSON.stringify({ state: 'expired', detail: `ESPN ${profile.mvpd} session expired` });
                  }
                  if (hoursLeft <= 24) {
                    return JSON.stringify({ state: 'expiring', detail: `${profile.mvpd}, ${hoursLeft.toFixed(1)}h remaining (stream playing)` });
                  }
                  return JSON.stringify({ state: 'ok', detail: `${profile.mvpd}, ${(hoursLeft / 24).toFixed(1)}d remaining (stream playing)` });
                }
                return JSON.stringify({ state: 'ok', detail: `${profile.mvpd} + stream playing` });
              }

              return JSON.stringify({ state: 'ok', detail: 'MyDisney + stream playing' });
            }

            for (const v of videos) {
              if (v.paused) {
                v.muted = true;
                v.play().catch(() => {});
              }
            }
            const playBtn = queryAllDeep('button[aria-label*="Play" i], button[title*="Play" i]')[0];
            if (playBtn && Date.now() - (window.__charonEspnHealthClickAt || 0) > 3000) {
              window.__charonEspnHealthClickAt = Date.now();
              playBtn.click();
            }

            const snippet = bodyText.replace(/\s+/g, ' ').trim().slice(0, 100);
            return JSON.stringify({
              state: 'pending',
              detail: `url=${location.pathname} mvpd=${profile?.mvpd || 'none'} videos=${videos.length} text="${snippet}"`
            });
          }
          // 3. Common redirect-to-login paths across YTTV, Hulu, Fubo, Sling, Philo, TVE sites
          if (/\/(welcome|login|signin|sign-in|signup|auth|mvpd|activate)\b/i.test(path) ||
              /accounts\.google\.com|login\.|auth\./i.test(location.hostname)) {
            return JSON.stringify({ state: 'expired', detail: `Redirected to ${location.hostname}${path}` });
          }

          // 4. Check for prominent "Sign In" / "Link TV Provider" buttons in main content (ignoring footers/nav)
          const hasVideo = document.querySelectorAll('video').length > 0;
          if (!hasVideo) {
            const signInBtn = Array.from(document.querySelectorAll('a, button')).find(el => {
              if (el.closest('footer, [class*="footer" i], nav')) return false;
              const t = (el.textContent || '').trim();
              return /^(sign in|log in|link tv provider|connect tv provider|choose tv provider)$/i.test(t) &&
                     el.getBoundingClientRect().width > 0;
            });
            if (signInBtn) {
              return JSON.stringify({ state: 'expired', detail: `Visible "${signInBtn.textContent.trim()}" prompt` });
            }
          }

          return JSON.stringify({ state: 'ok', detail: href });
        })()
        """;

    private static int FindFreeLoopbackPort(int startPort)
    {
        for (int p = startPort; p < startPort + 100; p++)
        {
            try
            {
                using var l = new TcpListener(System.Net.IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { }
        }
        return startPort;
    }

    private static async Task<string?> WaitForPageWebSocketUrlAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json", ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    string url = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (type == "page" && !url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (el.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                            return wsProp.GetString();
                    }
                }
            }
            catch { }
            await Task.Delay(300, ct);
        }
        return null;
    }

    private static async Task<string?> EvaluateJsStringAsync(ClientWebSocket ws, int id, string expression, CancellationToken ct)
    {
        try
        {
            await SendCdpCommandAsync(ws, id, "Runtime.evaluate", new
            {
                expression,
                returnByValue = true,
                awaitPromise = true,
                userGesture = true
            }, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            var buffer = new byte[65536];
            using var ms = new MemoryStream();

            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buffer, timeoutCts.Token);
                if (res.MessageType == WebSocketMessageType.Close)
                    return null;

                ms.Write(buffer, 0, res.Count);
                if (res.EndOfMessage)
                {
                    string msg = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);

                    using var doc = JsonDocument.Parse(msg);
                    if (doc.RootElement.TryGetProperty("id", out var msgId) && msgId.GetInt32() == id)
                    {
                        if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                            resultEl.TryGetProperty("result", out var innerResult) &&
                            innerResult.TryGetProperty("value", out var valEl) &&
                            valEl.ValueKind == JsonValueKind.String)
                        {
                            return valEl.GetString();
                        }
                        return null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }

        return null;
    }

    private static async Task SendCdpCommandAsync(ClientWebSocket ws, int id, string method, object parameters, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_loopTask is not null)
            {
                try { await _loopTask; } catch { }
            }
            _cts.Dispose();
            _cts = null;
        }
        _runLock.Dispose();
    }
}