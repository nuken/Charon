using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Charon.Config;

namespace Charon.Providers;

public class ChannelSyncService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<(List<ProviderChannel> Channels, int MatchedCount)> ScanAndMatchAsync(
        CharonConfig config,
        LoadedProvider provider,
        string dvrIp,
        int dvrPort,
        Action<string> log,
        Action<string> statusCallback)
    {
        statusCallback($"Step 1/2: Scraping live channel lineup from {provider.Manifest.Name}...");
        log($"[Sync] Launching browser scan for {provider.Manifest.Name} using authenticated Tuner profile...");

        List<ProviderChannel> scrapedChannels = await ScrapeProviderLineupViaCdpAsync(config, provider, log, statusCallback);
        if (scrapedChannels.Count == 0)
        {
            throw new InvalidOperationException(
                $"No channels were found on {provider.Manifest.Name}. Please click '1. Login to Provider', sign in, CLOSE the Chrome login window completely, and then click '2. Scan & Sync Lineup'.");
        }

        // If the provider (like Spectrum) already supplies native Gracenote tmsid values, skip the Channels DVR lookup!
        int prePopulated = scrapedChannels.Count(c => !string.IsNullOrWhiteSpace(c.StationId));
        if (prePopulated == scrapedChannels.Count)
        {
            log($"[Sync] Scraped {scrapedChannels.Count} channels from {provider.Manifest.Name} with native Gracenote tmsid values already populated.");
            return (scrapedChannels, prePopulated);
        }

        log($"[Sync] Scraped {scrapedChannels.Count} channels from {provider.Manifest.Name}. Resolving Gracenote IDs via http://{dvrIp}:{dvrPort}...");
        int matchedCount = await MatchGracenoteStationsAsync(scrapedChannels, dvrIp, dvrPort, log, statusCallback);

        return (scrapedChannels, matchedCount);
    }

    private static async Task<List<ProviderChannel>> ScrapeProviderLineupViaCdpAsync(
        CharonConfig config,
        LoadedProvider provider,
        Action<string> log,
        Action<string> statusCallback)
    {
        string baseProfilesDir = ResolveExistingProfilesRoot();
        string selectedProfile = SelectUnlockedAuthenticatedProfile(baseProfilesDir, log);

        log($"[Sync] Using authenticated Chrome profile: {selectedProfile}");

        int cdpPort = FindFreeLoopbackPort(9330);
        bool isSpectrum = provider.Manifest.Id.Contains("spectrum", StringComparison.OrdinalIgnoreCase);
        string targetUrl = isSpectrum
            ? "https://watch.spectrum.net/guide"
            : "https://tv.youtube.com/live";

        int posX = config.WindowPlacement == WindowPlacementMode.VisibleDebug ? 80 : -3200;
        int posY = config.WindowPlacement == WindowPlacementMode.VisibleDebug ? 80 : 0;

        // Add --disable-extensions during lineup scan so TabCaptureExt CSS/scripts never lock the guide grid!
        string args = string.Join(" ",
        [
            $"--user-data-dir=\"{selectedProfile}\"",
            $"--remote-debugging-port={cdpPort}",
            "--remote-allow-origins=*",
            "--disable-extensions",
            $"--window-position={posX},{posY}",
            "--window-size=1280,1000",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-blink-features=AutomationControlled",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--mute-audio",
            $"\"{targetUrl}\""
        ]);

        Process? chromeProc = null;
        string? wsUrl = null;

        try
        {
            chromeProc = Process.Start(new ProcessStartInfo
            {
                FileName = config.ResolvedChromePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            wsUrl = await WaitForPageWebSocketUrlAsync(cdpPort, TimeSpan.FromSeconds(15));
            if (string.IsNullOrWhiteSpace(wsUrl))
                throw new TimeoutException("Timed out connecting to Chrome DevTools Protocol. Make sure the Login window is closed before scanning.");

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

            if (isSpectrum)
            {
                string spectrumJs = GetSpectrumScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for {provider.Manifest.Name} guide to load (attempt {attempt})...");
                    string? jsonResult = await EvaluateJsStringAsync(ws, attempt, spectrumJs);
                    if (!string.IsNullOrWhiteSpace(jsonResult) && jsonResult.StartsWith("[", StringComparison.Ordinal) && jsonResult != "[]")
                    {
                        var parsed = ProviderManager.ParseChannelsJson(jsonResult);
                        if (parsed.Count > 0) return parsed;
                    }
                    await Task.Delay(1500);
                }
                return [];
            }
            else
            {
                // YouTube TV: Wait for initial guide grid, then scroll using real CDP hardware mouseWheel events
                var allChannels = new List<ProviderChannel>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int msgId = 1;
                var waitDeadline = DateTime.UtcNow.AddSeconds(25);
                bool gridReady = false;

                while (DateTime.UtcNow < waitDeadline)
                {
                    statusCallback("Step 1/2: Waiting for YouTube TV live guide grid to load...");
                    string? res = await EvaluateJsStringAsync(ws, msgId++, GetYouTubeTvScraperJs());
                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var batch = ProviderManager.ParseChannelsJson(res);
                            if (batch.Count > 0)
                            {
                                gridReady = true;
                                foreach (var ch in batch)
                                {
                                    if (seenNames.Add(ch.Name))
                                    {
                                        ch.ChannelNumber = (allChannels.Count + 1).ToString();
                                        allChannels.Add(ch);
                                    }
                                }
                                break;
                            }
                        }
                    }
                    await Task.Delay(1200);
                }

                if (!gridReady)
                    return [];

                // Now dispatch real CDP mouseWheel + PageDown events to scroll through the entire virtualized guide
                int stagnantPasses = 0;
                for (int scrollStep = 1; scrollStep <= 45; scrollStep++)
                {
                    statusCallback($"Step 1/2: Scrolling YouTube TV guide... ({allChannels.Count} channels found so far)");

                    // Send 3 hardware mouseWheel ticks down at the center of the guide (x=640, y=550)
                    for (int w = 0; w < 3; w++)
                    {
                        await SendCdpCommandAsync(ws, msgId++, "Input.dispatchMouseEvent", new
                        {
                            type = "mouseWheel",
                            x = 640,
                            y = 550,
                            deltaX = 0,
                            deltaY = 700
                        });
                        await Task.Delay(90);
                    }

                    await Task.Delay(250);

                    string? batchJson = await EvaluateJsStringAsync(ws, msgId++, GetYouTubeTvScraperJs());
                    int beforeCount = allChannels.Count;

                    if (!string.IsNullOrWhiteSpace(batchJson) && batchJson.StartsWith("[", StringComparison.Ordinal))
                    {
                        var batch = ProviderManager.ParseChannelsJson(batchJson);
                        foreach (var ch in batch)
                        {
                            if (seenNames.Add(ch.Name))
                            {
                                ch.ChannelNumber = (allChannels.Count + 1).ToString();
                                allChannels.Add(ch);
                            }
                        }
                    }

                    if (allChannels.Count == beforeCount)
                    {
                        stagnantPasses++;
                        if (stagnantPasses >= 5)
                            break;
                    }
                    else
                    {
                        stagnantPasses = 0;
                    }
                }

                return allChannels;
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
                    await SendCdpCommandAsync(closeWs, 9999, "Browser.close", new { });
                    await Task.Delay(300);
                }
                catch { }
            }

            if (chromeProc is { HasExited: false })
            {
                try { chromeProc.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static string ResolveExistingProfilesRoot()
    {
        // Check standard Profiles path and ChromeProfiles path in case ChromeTuner uses either naming
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Profiles"),
            Path.Combine(AppContext.BaseDirectory, "ChromeProfiles")
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
                return path;
        }

        return candidates[0];
    }

    private static string SelectUnlockedAuthenticatedProfile(string baseProfilesDir, Action<string> log)
    {
        Directory.CreateDirectory(baseProfilesDir);

        // Try Tuner_1 first, then Tuner_2, Tuner_3, Tuner_4
        for (int i = 1; i <= 4; i++)
        {
            string candidate = Path.Combine(baseProfilesDir, $"Tuner_{i}");
            if (!Directory.Exists(candidate))
                continue;

            if (!IsProfileInUseByChrome(candidate))
            {
                return candidate;
            }
            else if (i == 1)
            {
                log("[Sync] Notice: Tuner_1 is currently open in Chrome; checking synced backup tuner profiles...");
            }
        }

        // If Tuner_1 was left open from clicking "1. Login to Provider", close the idle login browser so Tuner_1 can be used
        string tuner1 = Path.Combine(baseProfilesDir, "Tuner_1");
        Directory.CreateDirectory(tuner1);
        KillChromeUsingProfile(tuner1, log);
        return tuner1;
    }

    private static bool IsProfileInUseByChrome(string profileDir)
    {
        string cookiesPath = Path.Combine(profileDir, "Default", "Network", "Cookies");
        if (!File.Exists(cookiesPath))
            return false;

        try
        {
            using var fs = new FileStream(cookiesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void KillChromeUsingProfile(string profileDir, Action<string> log)
    {
        try
        {
            // Clean up stale lockfile if present
            string lockFile = Path.Combine(profileDir, "lockfile");
            if (File.Exists(lockFile))
            {
                foreach (var proc in Process.GetProcessesByName("chrome"))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                Thread.Sleep(400);
            }
        }
        catch (Exception ex)
        {
            log($"[Sync] Warning releasing profile lock: {ex.Message}");
        }
    }

    private static string GetYouTubeTvScraperJs() => """
        (() => {
          if (location.pathname.includes('/welcome')) {
            return JSON.stringify({ status: 'not_logged_in', url: location.href });
          }

          if (!location.pathname.startsWith('/live')) {
            location.href = 'https://tv.youtube.com/live';
            return JSON.stringify({ status: 'navigating_to_live', url: location.href });
          }

          const list = [];
          const seen = new Set();
          let chNum = 1;

          const queryAllDeep = (selector, root = document) => {
            const results = Array.from(root.querySelectorAll(selector));
            const allElements = root.querySelectorAll('*');
            for (const el of allElements) {
              if (el.shadowRoot) {
                results.push(...queryAllDeep(selector, el.shadowRoot));
              }
            }
            return results;
          };

          const thumbs = queryAllDeep("ytu-endpoint.tenx-thumb[aria-label], [aria-label^='watch ']");
          thumbs.forEach(thumb => {
            const label = thumb.getAttribute("aria-label") || "";
            if (label.startsWith("watch ")) {
              const name = label.slice(6).trim();
              const anchor = thumb.tagName.toLowerCase() === 'a' ? thumb : thumb.querySelector("a");
              const href = anchor ? (anchor.getAttribute("href") || "") : "";
              if ((href.startsWith("watch/") || href.startsWith("/watch/")) && !seen.has(name)) {
                seen.add(name);
                const cleanHref = href.replace(/^\//, '');
                const safeId = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
                list.push({
                  Id: safeId,
                  Name: name,
                  ChannelNumber: String(chNum++),
                  StationId: "",
                  Url: `https://tv.youtube.com/${cleanHref}`
                });
              }
            }
          });

          if (list.length === 0) {
            return JSON.stringify({ status: 'waiting_for_grid', url: location.href, title: document.title });
          }

          return JSON.stringify(list);
        })()
        """;

    private static string GetSpectrumScraperJs() => """
        (async () => {
          const clean = (s) => (s || '').replace(/^["']|["']$/g, '');
          const token = clean(localStorage.getItem('oauth_token'));
          const devId = clean(localStorage.getItem('device_id'));
          const list = [];

          if (token) {
            try {
              const hdrs = {
                'Authorization': `Bearer ${token}`,
                'device_id': devId,
                'x-client-id': 'stva-ovp',
                'x-client-version': '17.32.0.289483649'
              };

              // Check location (behindOwnModem / inMarket) just like FastChannels spectrum.py
              let availFlag = null;
              try {
                const locRes = await fetch('https://apis.spectrum.net/pinxt/customer/location/v1', { headers: hdrs });
                if (locRes.ok) {
                  const loc = await locRes.json();
                  if (loc && !loc.behindOwnModem) {
                    availFlag = loc.inMarket ? 'availableInMarket' : 'availableOutOfMarket';
                  }
                }
              } catch (e) {}

              const r = await fetch('https://apis-vid.spectrum.net/lantern/lrs/api/smarttv/channels/v3?streamVersion=5', { headers: hdrs });
              if (r.ok) {
                const rows = await r.json();
                const seen = new Set();
                for (const ch of rows) {
                  const name = (ch.networkName || ch.callSign || '').trim();
                  const callSign = (ch.callSign || '').trim();
                  const tms = String(ch.tmsGuideId || '').trim();
                  if (!ch.online || ch.onlineEntitled === false || !tms || !name || seen.has(tms)) continue;
                  if (availFlag && ch[availFlag] === false) continue;
                  if (/^~mc\d+/i.test(name) || /^SPCTRM\d*$/i.test(callSign)) continue;
                  seen.add(tms);
                  list.push({
                    Id: `${(callSign || name).toLowerCase().replace(/[^a-z0-9]+/g, '-')}-${tms}`,
                    Name: name.replace(/ (?:HD|DT)(?: (?:East|West))?$/, ''),
                    ChannelNumber: String((ch.channelNumbers && ch.channelNumbers[0]) || ''),
                    StationId: tms,
                    Url: `https://watch.spectrum.net/livetv?tmsid=${tms}`
                  });
                }
              }
            } catch (e) {}
          }

          // Fallback to guide DOM if needed
          if (list.length === 0) {
            const seen = new Set();
            document.querySelectorAll('li.channel-header-row:not(.nonstreamable)').forEach(li => {
              const cs = li.querySelector('p.callsign')?.textContent?.trim() || '';
              const num = li.querySelector('p.channel-number')?.textContent?.trim() || '';
              const tms = (li.querySelector('img.channel-logo')?.getAttribute('src') || '').match(/\/guide\/(\d+)\?/)?.[1] || '';
              const idx = li.getAttribute('channel-index') || '';
              const rawRow = document.getElementById(`rowheader-${idx}`)?.textContent?.trim() || '';
              const name = rawRow.replace(/^Channel \d+ /, '') || cs;
              if (!tms || seen.has(tms) || name.startsWith('~') || /^Spectrum\d/i.test(name)) return;
              seen.add(tms);
              list.push({
                Id: `${cs.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-${tms}`,
                Name: name.replace(/ (?:HD|DT)(?: (?:East|West))?$/, ''),
                ChannelNumber: num,
                StationId: tms,
                Url: `https://watch.spectrum.net/livetv?tmsid=${tms}`
              });
            });
          }

          if (list.length === 0) {
            return JSON.stringify({ status: token ? 'waiting_for_spectrum_guide' : 'not_logged_in_no_oauth_token', url: location.href });
          }

          return JSON.stringify(list);
        })()
        """;

    private static async Task<int> MatchGracenoteStationsAsync(
        List<ProviderChannel> channels,
        string dvrIp,
        int dvrPort,
        Action<string> log,
        Action<string> statusCallback)
    {
        string serverUrl = $"http://{dvrIp}:{dvrPort}";
        int updatedCount = 0;

        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            statusCallback($"Step 2/2: Matching Gracenote IDs ({i + 1} / {channels.Count}) — {ch.Name}...");

            if (!string.IsNullOrWhiteSpace(ch.StationId))
            {
                updatedCount++;
                continue;
            }

            string rawName = ch.Name.Trim();
            List<TmsStation>? results = await QueryChannelsDvrTmsAsync(serverUrl, rawName);

            // Tier 2: Strip trailing numbers (e.g., "WSOC 9" -> "WSOC", "FOX 46" -> "FOX")
            if (results is null || results.Count == 0)
            {
                string stripped = System.Text.RegularExpressions.Regex.Replace(rawName, @"\s+\d+$", "");
                if (!string.Equals(stripped, rawName, StringComparison.OrdinalIgnoreCase))
                {
                    results = await QueryChannelsDvrTmsAsync(serverUrl, stripped);
                }
            }

            // Tier 3: First word if multi-word (e.g., "WCNC NBC" -> "WCNC")
            if ((results is null || results.Count == 0) && rawName.Contains(' '))
            {
                string firstWord = rawName.Split(' ')[0];
                if (firstWord.Length >= 3)
                {
                    results = await QueryChannelsDvrTmsAsync(serverUrl, firstWord);
                }
            }

            if (results is { Count: > 0 })
            {
                var match = results.FirstOrDefault(r =>
                    (!string.IsNullOrWhiteSpace(r.CallSign) &&
                     (string.Equals(System.Text.RegularExpressions.Regex.Replace(r.CallSign, @"DT\d*$", ""), rawName, StringComparison.OrdinalIgnoreCase) ||
                      r.CallSign.StartsWith(rawName, StringComparison.OrdinalIgnoreCase))) ||
                    (!string.IsNullOrWhiteSpace(r.Name) && r.Name.Contains(rawName, StringComparison.OrdinalIgnoreCase)))
                    ?? results[0];

                if (!string.IsNullOrWhiteSpace(match.StationId))
                {
                    ch.StationId = match.StationId;
                    updatedCount++;
                    log($"[Gracenote] [OK] {ch.Name} -> {match.Name} ({match.CallSign}) [StationId: {match.StationId}]");
                }
            }
            else
            {
                log($"[Gracenote] [??] {ch.Name}: No match found (leaving blank for Channels DVR auto-map)");
            }

            await Task.Delay(40);
        }

        return updatedCount;
    }

    private static async Task<List<TmsStation>?> QueryChannelsDvrTmsAsync(string serverUrl, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return null;

        string encoded = Uri.EscapeDataString(searchTerm.Trim());
        string url = $"{serverUrl}/tms/stations/{encoded}";

        try
        {
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<TmsStation>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

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

    private static async Task<string?> WaitForPageWebSocketUrlAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json");
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
            await Task.Delay(300);
        }
        return null;
    }

    private static async Task<string?> EvaluateJsStringAsync(ClientWebSocket ws, int id, string expression)
    {
        await SendCdpCommandAsync(ws, id, "Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true
        });

        var buffer = new byte[65536];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(buffer, CancellationToken.None);
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
        return null;
    }

    private static async Task SendCdpCommandAsync(ClientWebSocket ws, int id, string method, object parameters)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private sealed class TmsStation
    {
        public string? StationId { get; set; }
        public string? Name { get; set; }
        public string? CallSign { get; set; }
        public string? Type { get; set; }
    }
}