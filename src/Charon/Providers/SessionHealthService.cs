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
        if (providerId.Contains("cnn", StringComparison.OrdinalIgnoreCase) ||
        providerId.Contains("cbs", StringComparison.OrdinalIgnoreCase))
    {
        hours = Math.Min(hours, 6);
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
                        string checkUrl = isCbs
                            ? "https://www.cbs.com/live-tv/stream/tveverywhere/"
                            : isCnn
                                ? "https://www.cnn.com/videos/cnn"
                                : provider.Manifest.LoginUrl;

                        await SendCdpCommandAsync(activeWs, cmdId++, "Page.navigate", new { url = checkUrl }, ct);

                        string? json = null;
                        if (isCnn || isCbs)
                        {
                            // Give heavy TVE players 4s to commit navigation before polling
                            await Task.Delay(TimeSpan.FromSeconds(4), ct);

                            var deadline = DateTime.UtcNow.AddSeconds(isCbs ? 20 : 18);
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

                            // Unload active video/DRM stream before navigating to the next provider
                            if (ws is { State: WebSocketState.Open })
                            {
                                try
                                {
                                    await SendCdpCommandAsync(ws, cmdId++, "Page.navigate", new { url = "about:blank" }, ct);
                                    await Task.Delay(300, ct);
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
        (() => {
          const id = {{JsonSerializer.Serialize(providerId.ToLowerInvariant())}};
          const href = location.href;
          const path = location.pathname.toLowerCase();

          // 1. Spectrum exposes oauth_token + xoauth_token_expiration in localStorage
          if (id.includes('spectrum')) {
            const token = (localStorage.getItem('oauth_token') || '').replace(/^["']|["']$/g, '');
            if (!token) return JSON.stringify({ state: 'expired', detail: 'No oauth_token in localStorage' });
            const expRaw = (localStorage.getItem('xoauth_token_expiration') || '').replace(/^["']|["']$/g, '');
            const expMs = Number(expRaw);
            if (expMs > 0) {
              const hoursLeft = (expMs - Date.now()) / 3600000;
              if (hoursLeft <= 0) return JSON.stringify({ state: 'expired', detail: 'Spectrum token expired' });
              if (hoursLeft <= 3) return JSON.stringify({ state: 'expiring', detail: `${hoursLeft.toFixed(1)}h remaining` });
              return JSON.stringify({ state: 'ok', detail: `${hoursLeft.toFixed(1)}h remaining` });
            }
            return JSON.stringify({ state: 'ok', detail: 'oauth_token present' });
          }

          // 2. CNN: verify Adobe Pass shortAuthorize or active video, and flag TVE login prompt
          if (id.includes('cnn')) {
            const bodyText = document.body?.innerText || '';
            if (/have a tv provider\?/i.test(bodyText) && /connect now/i.test(bodyText)) {
              return JSON.stringify({ state: 'expired', detail: 'Visible "Have a TV Provider? Connect now" prompt' });
            }
            const hitShortAuth = performance.getEntriesByType('resource')
              .some(r => /sp\.auth\.adobe\.com\/adobe-services\/shortAuthorize/i.test(r.name));
            const hasPlayingVideo = Array.from(document.querySelectorAll('video'))
              .some(v => v.readyState >= 2 || v.currentTime > 0);

            if (hitShortAuth || hasPlayingVideo) {
              return JSON.stringify({ state: 'ok', detail: hitShortAuth ? 'shortAuthorize verified' : 'video active' });
            }
            return JSON.stringify({ state: 'pending', detail: 'Waiting for AccessEnabler shortAuthorize...' });
          }
          // 2b. CBS: click "Start Watching" overlay, then verify auth cookie + active video/shortAuthorize
          if (id.includes('cbs')) {
            const bodyText = document.body?.innerText || '';
            if (/error with this account'?s settings|unable to continue|sign in with your tv provider|verify your tv provider|choose your tv provider/i.test(bodyText)) {
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
            const hitShortAuth = performance.getEntriesByType('resource')
              .some(r => /sp\.auth\.adobe\.com\/adobe-services\/shortAuthorize/i.test(r.name));
            const videos = queryAllDeep('video');
            const hasPlayingVideo = videos.some(v => v.readyState >= 2 || v.currentTime > 0);

            if ((hasMvpdAuthz || hasCbsCom) && (hasPlayingVideo || hitShortAuth)) {
              const authTag = hasMvpdAuthz ? 'mvpd_authz' : 'CBS_COM';
              const playTag = hasPlayingVideo ? 'video active' : 'shortAuthorize verified';
              return JSON.stringify({ state: 'ok', detail: `${authTag} + ${playTag} (${location.pathname})` });
            }

            const snippet = bodyText.replace(/\s+/g, ' ').trim().slice(0, 80);
            return JSON.stringify({
              state: 'pending',
              detail: `url=${location.pathname} mvpd_authz=${hasMvpdAuthz} CBS_COM=${hasCbsCom} videos=${videos.length} shortAuth=${hitShortAuth} text="${snippet}"`
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