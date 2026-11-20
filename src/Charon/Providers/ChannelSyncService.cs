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

    public static bool CanScanProvider(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId) &&
        (providerId.Contains("youtubetv", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("spectrum", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("xfinity", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Contains("philo", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Contains("hulu", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Contains("fubo", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Contains("sling", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("nbc", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("abc", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("cbs", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("fox", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("usa", StringComparison.OrdinalIgnoreCase) ||
         providerId.Contains("espn", StringComparison.OrdinalIgnoreCase));
		 
    public static async Task<(List<ProviderChannel> Channels, int MatchedCount)> ScanAndMatchAsync(
        CharonConfig config,
        LoadedProvider provider,
        string dvrIp,
        int dvrPort,
        Action<string> log,
        Action<string> statusCallback)
    {
        if (!CanScanProvider(provider.Manifest.Id))
        {
            throw new InvalidOperationException(
                $"{provider.Manifest.Name} uses a pre-configured channel lineup with Gracenote IDs already mapped and does not require scanning. " +
                "Simply unhide the channels you want in the Channel Lineup Editor and click Save Lineup.");
        }

        statusCallback($"Step 1/2: Scraping live channel lineup from {provider.Manifest.Name}...");
        log($"[Sync] Launching browser scan for {provider.Manifest.Name} using authenticated Tuner profile...");

        List<ProviderChannel> scrapedChannels = await ScrapeProviderLineupViaCdpAsync(config, provider, log, statusCallback);
        if (scrapedChannels.Count == 0)
        {
            throw new InvalidOperationException(
                $"No channels were found on {provider.Manifest.Name}. Please click '1. Login to Provider', sign in, CLOSE the Chrome login window completely, and then click '2. Scan & Sync Lineup'.");
        }
		
		// Apply the user's current hide choices before matching, so hidden channels (e.g. Sling Freestream) skip the DVR lookup
        var previousHidden = provider.Channels
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IsHidden, StringComparer.OrdinalIgnoreCase);
        foreach (var ch in scrapedChannels)
        {
            if (previousHidden.TryGetValue(ch.Id, out bool hidden))
                ch.IsHidden = hidden;
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
        bool isXfinity = provider.Manifest.Id.Contains("xfinity", StringComparison.OrdinalIgnoreCase);
        bool isSling = provider.Manifest.Id.Contains("sling", StringComparison.OrdinalIgnoreCase);
		bool isPhilo = provider.Manifest.Id.Contains("philo", StringComparison.OrdinalIgnoreCase);
		bool isNbc = provider.Manifest.Id.Contains("nbc", StringComparison.OrdinalIgnoreCase);
		bool isHulu = provider.Manifest.Id.Contains("hulu", StringComparison.OrdinalIgnoreCase);
		bool isFubo = provider.Manifest.Id.Contains("fubo", StringComparison.OrdinalIgnoreCase);
		bool isAbc = provider.Manifest.Id.Contains("abc", StringComparison.OrdinalIgnoreCase);
		bool isCbs = provider.Manifest.Id.Contains("cbs", StringComparison.OrdinalIgnoreCase);
		bool isFox = provider.Manifest.Id.Contains("fox", StringComparison.OrdinalIgnoreCase);
		bool isUsa = provider.Manifest.Id.Contains("usa", StringComparison.OrdinalIgnoreCase);
		bool isEspn = provider.Manifest.Id.Contains("espn", StringComparison.OrdinalIgnoreCase);
		string targetUrl = isSpectrum
            ? "https://watch.spectrum.net/guide"
            : isXfinity
            ? "https://www.xfinity.com/stream/listings"
			: isSling
            ? "https://watch.sling.com/dashboard/grid_guide/grid_guide_a_z"
			: isPhilo
            // Start on Home, not the guide: the scraper must hook fetch before the guide's first request
            ? "https://www.philo.com/player/mytv"
			: isNbc 
			? "https://www.nbc.com/live"
			: isHulu
            ? "https://www.hulu.com/live"
			: isFubo
            // Start on Home, not the guide: the scraper must hook the guide request before it is sent
            ? "https://www.fubo.tv/p/home"
			: isAbc
            ? "https://abc.com/watch-live"
			: isCbs
            ? "https://www.cbs.com/live-tv/stream/tveverywhere/"
			: isFox
            ? "https://www.fox.com/live/channels"
			: isUsa
            ? "https://www.usanetwork.com/live"
			: isEspn
            ? "https://www.espn.com/watch/"
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
			else if (isXfinity)
            {
                string xfinityJs = GetXfinityScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for Xfinity Stream guide data (<tv-page-grid>) to load (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, xfinityJs);
                    if (!string.IsNullOrWhiteSpace(res) && res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                    {
                        var parsed = ProviderManager.ParseChannelsJson(res);
                        if (parsed.Count > 0)
                            return parsed;
                    }
                    await Task.Delay(1500);
                }

                return [];
            }
			 else if (isSling)
            {
                // Sling: pass "Who's Watching?", open the A-Z guide, then read the full lineup from its virtualized list
                string slingJs = GetSlingScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(60);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for Sling TV A-Z guide to load (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, slingJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0)
                                return parsed;
                        }
                    }

                    await Task.Delay(1500);
                }

                return [];
            }
			else if (isPhilo)
            {
                // Philo: capture the guide's own GraphQL request, then replay it to read the full lineup
                string philoJs = GetPhiloScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(45);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for Philo guide to load (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, philoJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0)
                                return parsed;
                        }
                    }

                    await Task.Delay(1500);
                }

                return [];
            }
			else if (isNbc)
            {
                string nbcJs = GetNbcScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                
                while (DateTime.UtcNow < deadline)
                {
                    statusCallback($"Step 1/2: Resolving local NBC affiliate via geolocation...");
                    string? res = await EvaluateJsStringAsync(ws, 1, nbcJs);
                    
                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0) return parsed;
                        }
                    }
                    await Task.Delay(1000);
                }
                return [];
            }
			else if (isHulu)
            {
                // Hulu: read the live guide's full channel list from the web app's Redux store
                string huluJs = GetHuluScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(45);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for Hulu live guide to load (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, huluJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0)
                                return parsed;
                        }
                    }

                    await Task.Delay(1500);
                }

                return [];
            }
			else if (isFubo)
            {
                // Fubo: capture the guide's own channel-list response (only the account's subscribed channels)
                string fuboJs = GetFuboScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(45);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for Fubo guide to load (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, fuboJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0)
                                return parsed;
                        }
                    }

                    await Task.Delay(1500);
                }

                return [];
            }
			else if (isAbc)
            {
                string abcJs = GetAbcScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(25);

                while (DateTime.UtcNow < deadline)
                {
                    statusCallback($"Step 1/2: Resolving local ABC affiliate & lineup via abc.com/watch-live...");
                    string? res = await EvaluateJsStringAsync(ws, 1, abcJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0) return parsed;
                        }
                    }
                    await Task.Delay(1000);
                }
                return [];
            }
			else if (isCbs)
            {
                string cbsJs = GetCbsScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(25);

                while (DateTime.UtcNow < deadline)
                {
                    statusCallback($"Step 1/2: Resolving local CBS affiliate via cbs.com...");
                    string? res = await EvaluateJsStringAsync(ws, 1, cbsJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0) return parsed;
                        }
                    }
                    await Task.Delay(1000);
                }
                return [];
            }
			else if (isFox)
            {
                string foxJs = GetFoxScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(25);

                while (DateTime.UtcNow < deadline)
                {
                    statusCallback($"Step 1/2: Scraping FOX One live channels & local affiliate via fox.com...");
                    string? res = await EvaluateJsStringAsync(ws, 1, foxJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0) return parsed;
                        }
                    }
                    await Task.Delay(1000);
                }
                return [];
            }
			else if (isUsa)
            {
                string usaJs = GetUsaScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(25);

                while (DateTime.UtcNow < deadline)
                {
                    statusCallback($"Step 1/2: Scraping USA Network live channels (East/West) via usanetwork.com...");
                    string? res = await EvaluateJsStringAsync(ws, 1, usaJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0) return parsed;
                        }
                    }
                    await Task.Delay(1000);
                }
                return [];
            }
			else if (isEspn)
            {
                string espnJs = GetEspnScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(30);

                while (DateTime.UtcNow < deadline)
                {
                    statusCallback($"Step 1/2: Scraping ESPN Watch live channels via espn.com/watch...");
                    string? res = await EvaluateJsStringAsync(ws, 1, espnJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0) return parsed;
                        }
                    }
                    await Task.Delay(1000);
                }
                return [];
            }
            else
            {
                // YouTube TV: Wait for <ytu-epg-row> data models to mount and auto-scroll #id-bottom-main-content
                string yttvJs = GetYouTubeTvScraperJs();
                var deadline = DateTime.UtcNow.AddSeconds(45);
                int attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    statusCallback($"Step 1/2: Waiting for YouTube TV live guide (<ytu-epg-row>) to load and harvest (attempt {attempt})...");
                    string? res = await EvaluateJsStringAsync(ws, attempt, yttvJs);

                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        if (res.StartsWith("{\"status\":", StringComparison.Ordinal))
                        {
                            log($"[Sync] {res}");
                        }
                        else if (res.StartsWith("[", StringComparison.Ordinal) && res != "[]")
                        {
                            var parsed = ProviderManager.ParseChannelsJson(res);
                            if (parsed.Count > 0)
                                return parsed;
                        }
                    }

                    await Task.Delay(1500);
                }

                return [];
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
        (async () => {
          if (location.pathname.includes('/welcome')) {
            return JSON.stringify({ status: 'not_logged_in', url: location.href });
          }

          if (!location.pathname.startsWith('/live')) {
            location.href = 'https://tv.youtube.com/live';
            return JSON.stringify({ status: 'navigating_to_live', url: location.href });
          }

          const container = document.querySelector('#id-bottom-main-content') 
            || document.querySelector('.bottom-main-content');
          const initialRows = document.querySelectorAll('ytu-epg-row');

          if (!container || initialRows.length === 0) {
            return JSON.stringify({ status: 'waiting_for_grid', url: location.href, title: document.title });
          }

          // Ensure Polymer has populated .data on the mounted rows before starting the scroll pass
          const firstData = initialRows[0]?.data;
          if (!firstData || (!firstData.station && !firstData.navigationEndpoint)) {
            return JSON.stringify({ status: 'waiting_for_polymer_data', url: location.href });
          }

          const channels = new Map();
          let lastTop = -1;
          let stagnantPasses = 0;
          const maxPasses = 120;
          let chNum = 1;

          container.scrollTop = 0;
          await new Promise(r => setTimeout(r, 200));

          for (let i = 0; i < maxPasses; i++) {
            const rows = container.querySelectorAll('ytu-epg-row');

            for (const r of rows) {
              const d = r.data || {};
              const st = d.station?.epgStationRenderer || {};

              const ep = d.navigationEndpoint?.watchEndpoint 
                || st.navigationEndpoint?.watchEndpoint 
                || d.airings?.[0]?.navigationEndpoint?.watchEndpoint;
              const videoId = ep?.videoId;

              if (!videoId) continue;

              // Key by permanent YouTube stationId (UC...) so duplicate display names (e.g. PBS NC, ESPN)
              // stay separate and M3U channel-id stays permanent across program changes
              const ytStationId = d.stationId || st.stationId || videoId;
              if (channels.has(ytStationId)) continue;

              const name = st.name?.runs?.[0]?.text 
                || st.callSign?.runs?.[0]?.text
                || st.icon?.accessibility?.accessibilityData?.label
                || r.getAttribute('aria-label')?.replace(/^watch\s+/i, '').trim()
                || ytStationId;

              const safeSlug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
              const shortHash = ytStationId.length > 6 ? ytStationId.slice(-6).toLowerCase() : ytStationId.toLowerCase();

              channels.set(ytStationId, {
                Id: `${safeSlug}-${shortHash}`,
                Name: name,
                ChannelNumber: String(chNum++),
                StationId: "",
                Url: `https://tv.youtube.com/watch/${videoId}`
              });
            }

            container.scrollTop += 650;
            await new Promise(r => setTimeout(r, 120));

            if (container.scrollTop === lastTop) {
              stagnantPasses++;
              if (stagnantPasses >= 3) break;
            } else {
              stagnantPasses = 0;
              lastTop = container.scrollTop;
            }
          }

          container.scrollTop = 0;
          return JSON.stringify(Array.from(channels.values()));
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
		
	private static string GetXfinityScraperJs() => """
        (() => {
          const queryAllDeep = (selector, root = document) => {
            const list = Array.from(root.querySelectorAll(selector));
            const all = root.querySelectorAll('*');
            for (const el of all) {
              if (el.shadowRoot) list.push(...queryAllDeep(selector, el.shadowRoot));
            }
            return list;
          };

          const grid = document.querySelector('tv-page-grid') || queryAllDeep('tv-page-grid')[0];
          const rawChannels = grid && Array.isArray(grid.channels) ? grid.channels : null;
          if (!rawChannels || rawChannels.length === 0) {
            return JSON.stringify([]);
          }

          // Clean HD/SD suffixes only when the base callSign remains >= 3 chars (protects ID, WE, E!, FX)
          const normalizeCallSign = (cs) => {
            const clean = String(cs || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
            const stripped = clean.replace(/(?:HD|DT\d*|D|H)$/, '');
            return stripped.length >= 3 ? stripped : clean;
          };

          // Map known sister channels that share a parent companyId into distinct sub-keys
          const getSisterSubKey = (upperCall) => {
            // ESPN family (all share companyId 7642769970651506105)
            if (['ESPN2', 'ES2HD'].includes(upperCall)) return 'ESPN2';
            if (['ESPNN', 'ESWHD', 'ESPNEWS'].includes(upperCall)) return 'ESPNEWS';
            if (upperCall.startsWith('ESPNU')) return 'ESPNU';
            if (upperCall.startsWith('ESPN')) return 'ESPN';

            // Hallmark family (all share companyId 7889068523651137105)
            if (['HMYS', 'HMYSD'].includes(upperCall)) return 'HMYS';
            if (['HFM', 'HFMSD'].includes(upperCall)) return 'HFM';
            if (['HLMRK', 'HCHD'].includes(upperCall)) return 'HLMRK';

            // Nickelodeon family (share companyId 7294331549102474105)
            if (upperCall === 'NTOON') return 'NTOON';
            if (['NICM', 'NICK2'].includes(upperCall)) return 'NICK2';

            return null;
          };

          const scoreChannel = (c) => {
            const num = Number(c.number || 0);
            let score = 0;
            // Strongly prefer the primary 1000-1899 HD tier
            if (num >= 1000 && num <= 1899) score += 500;
            if (c.isHD) score += 100;
            if (!Array.isArray(c.restrictStreaming) || !c.restrictStreaming.includes('out-of-home')) score += 20;
            return score;
          };

          const bestByNetwork = new Map();

          for (const c of rawChannels) {
            if (!c || c.entitled === false) continue;

            const num = Number(c.number || 0);
            // Keep only core linear channels (1-1899); drops 3000+ international, 10000+ duplicates, 13000+ League Pass, 14000+ FAST
            if (num <= 0 || num > 1899) continue;

            // Drop local OTA subchannel diginets (1150-1195: MeTV, Buzzr, Catchy, Story, Grit, Comet, Charge, PBS Kids/Create)
            // Remove this single line if you WANT local OTA subchannels included
            if (num >= 1150 && num <= 1195) continue;

            const rawCall = String(c.callSign || '').trim();
            const companyId = String(c.companyId || c.networkAffiliateCompanyId || '').trim();
            if (!rawCall || !companyId) continue;

            const hint = String(c.callSignVoiceOverHint || '').trim();
            const upperHint = hint.toUpperCase();
            const upperCall = rawCall.toUpperCase();

            // 1. Filter out Sports Overflow, League Pass, and Extra Innings
            if (upperCall.endsWith('LP') || /^BTFB\d/i.test(upperCall) || upperHint.includes('LEAGUE PASS')) continue;

            // 2. Filter out Music Choice, FAST, Xumo, Local/Public Access, and ScreenPix barkers
            if (/^MUSIC CHOICE/i.test(upperHint) || /^MC\d+$/.test(upperCall) || upperCall.startsWith('~MC')) continue;
            if (upperHint === 'XUMO PLAY' || upperCall.includes('XUMO') || upperCall.startsWith('FAST')) continue;
            if (upperHint.includes('LOCAL PROGRAMMING') || upperHint.includes('LOCAL NEWS AND WEATHER')) continue;
            if (upperHint.includes('PUBLIC AFFAIRS') || upperHint.includes('PUBLIC ACCESS') || upperHint.includes('PEG')) continue;
            if (upperCall.includes('EVENTS') || upperHint.includes('SPECIALS AND EVENTS')) continue;
            if (upperHint.includes('SCREENPIX') || upperCall.startsWith('SCPX') || upperCall.startsWith('SPX')) continue;
            if (upperHint.includes('ENTERTAINMENT STUDIOS')) continue; // JusticeCentral/Recipe.TV/Comedy.TV fillers

            // 3. Filter out Shopping, Jewelry & Religious networks
            if (/^(?:QVC|HSN|JTV|SHPLC|SHOPL|GEMS)/i.test(upperCall)) continue;
            if (upperHint.includes('SHOPPING') || upperHint.includes('QVC') || upperHint.includes('HSN') || upperHint.includes('JEWELRY') || upperHint.includes('GEMSTONE')) continue;
            if (['TBN', 'TBNHD', 'EWTN', 'EWTNH', 'GLC', 'SBN', 'WHMB', 'POSTV', 'KONCS', 'KONCH', 'INSPD', 'IMPCT', 'JBSHD', 'FETVH', 'NTD24'].includes(upperCall)) continue;
            if (upperHint.includes('TRINITY BROADCASTING') || upperHint.includes('CHRISTIAN') || upperHint.includes('GOD TV') || upperHint.includes('ETERNAL WORD')) continue;

            // 4. Determine deduplication key:
            // Because Xfinity gives SD (COM/HIST/NGEO) and HD (CMDYD/HISHD/NGCHD) the exact same companyId,
            // keying by companyId automatically merges SD + HD pairs!
            const isPremium = /^(?:HBO|SHO|PSHO|STZ|MAX|ENC|TMC|MGM|FLIX)/i.test(upperCall);
            const sisterKey = getSisterSubKey(upperCall);

            let mapKey;
            if (sisterKey) {
              mapKey = `SISTER_${sisterKey}`;
            } else if (isPremium) {
              // Collapse only exact SD/HD pairs of the same premium feed (e.g. HBO-E + HBOHD, HBO2 + HBO2H, TMCe + TMCHD)
              mapKey = `PREM_${normalizeCallSign(upperCall)}`;
            } else {
              mapKey = `COMP_${companyId}`;
            }

            const existing = bestByNetwork.get(mapKey);
            if (!existing || scoreChannel(c) > scoreChannel(existing)) {
              bestByNetwork.set(mapKey, c);
            }
          }

          const results = [];
          const sorted = Array.from(bestByNetwork.values())
            .sort((a, b) => Number(a.number || 99999) - Number(b.number || 99999));

          for (const c of sorted) {
            const callSign = String(c.callSign).trim();
            const upperCall = callSign.toUpperCase();
            const companyId = String(c.companyId || c.networkAffiliateCompanyId).trim();
            const num = String(c.number || results.length + 1);

            const hint = String(c.callSignVoiceOverHint || '').trim();
            let displayName = hint || callSign;

            // Clean up shared parent voiceover names so Channels DVR and the user see the true channel name
            if (['ESPN2', 'ES2HD'].includes(upperCall)) displayName = 'ESPN2';
            else if (['ESPNN', 'ESWHD'].includes(upperCall)) displayName = 'ESPNews';
            else if (['HMYS', 'HMYSD'].includes(upperCall)) displayName = 'Hallmark Mystery';
            else if (['HFM', 'HFMSD'].includes(upperCall)) displayName = 'Hallmark Family';
            else if (upperCall === 'NTOON') displayName = 'Nicktoons';
            else if (['HBO', 'SHOWTIME', 'PARAMOUNT+ WITH SHOWTIME', 'CINEMAX', 'STARZ', 'MGM+', 'THE MOVIE CHANNEL'].includes(hint.toUpperCase()) && upperCall !== hint.toUpperCase()) {
              displayName = `${hint} (${callSign})`;
            }

            const safeId = callSign.toLowerCase().replace(/[^a-z0-9]+/g, '') + '-xf';

            results.push({
              Id: safeId,
              Name: displayName,
              ChannelNumber: num,
              StationId: '',
              Url: `https://www.xfinity.com/stream/live/${encodeURIComponent(callSign)}/${companyId}/${encodeURIComponent(callSign)}`
            });
          }

          return JSON.stringify(results);
        })()
        """;
		
	private static string GetSlingScraperJs() => """
        (() => {
          const path = location.pathname;
          const status = (s, extra) => JSON.stringify(Object.assign({ status: s, url: location.href }, extra || {}));

          // "Who's Watching?" appears on every fresh page load unless "Don't Ask Again" was chosen
          if (path.startsWith('/switch_user_profile')) {
            const avatar = document.querySelector('[data-testid^="SwitchUserProfilesIcons-0"] .Card-Avatar-Interaction-Container');
            if (avatar) avatar.click();
            return status(avatar ? 'selecting_profile' : 'waiting_for_profile_picker');
          }

          if (!path.startsWith('/dashboard/grid_guide')) {
            if (path.startsWith('/dashboard')) {
              history.pushState({}, '', '/dashboard/grid_guide/grid_guide_a_z');
              dispatchEvent(new PopStateEvent('popstate'));
              return status('opening_guide');
            }
            return status(location.hostname === 'watch.sling.com' ? 'waiting_for_sling' : 'not_logged_in');
          }

          // The guide is a react-window list; its itemData.getChannel(i) holds every row, not just the rendered ones
          const cell = document.querySelector('img.grid-cell-image') || document.querySelector('[data-testid^="channel-"]');
          const fiberKey = cell && Object.keys(cell).find((k) => k.startsWith('__reactFiber'));
          let fiber = fiberKey ? cell[fiberKey] : null;
          let list = null;
          for (let i = 0; fiber && i < 40; i++, fiber = fiber.return) {
            const p = fiber.memoizedProps;
            if (p && p.itemData && typeof p.itemData.getChannel === 'function') { list = p; break; }
          }
          if (!list) return status('waiting_for_guide');

          const total = Number(list.itemCount || 0);
          const results = [];
          const seen = new Set();
          let missing = 0;

          for (let i = 0; i < total; i++) {
            let c = null;
            try { c = list.itemData.getChannel(i); } catch (e) {}
            if (!c || !c.id) { missing++; continue; }
            if (c.format && c.format !== 'GRID_GUIDE_CHANNEL') continue;

            const guid = String(c.id).toLowerCase();
            if (!/^[0-9a-f]{32}$/.test(guid) || seen.has(guid)) continue;
            seen.add(guid);

            const adobe = (c.actions && c.actions.DETAIL_VIEW && c.actions.DETAIL_VIEW.adobe) || {};
            const name = String(c.title || adobe.ChannelName || '').trim();
            const callSign = String(adobe.CallSign || '').trim();
            // e.g. "Starz:224" for a subscription, "Freestream:658" for Sling's free channels
            const pkg = String(adobe.PackageName || '');
            if (!name) continue;

            results.push({
              Id: `${(callSign || name).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}-${guid.slice(0, 8)}`,
              Name: name,
              ChannelNumber: '',
              StationId: '',
              Url: `https://watch.sling.com/1/channel/${guid}/watch`,
              IsHidden: /^Freestream\b/i.test(pkg)
            });
          }

          // Rows load a page at a time; scroll the list until they all have data (the last row is a placeholder)
          if (missing > 1) {
            let scroller = cell.parentElement;
            while (scroller && !(scroller.scrollHeight > scroller.clientHeight + 10 && /auto|scroll/.test(getComputedStyle(scroller).overflowY))) {
              scroller = scroller.parentElement;
            }
            const atBottom = !scroller || scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 2;
            window.__charonSlingBottomHits = atBottom ? (window.__charonSlingBottomHits || 0) + 1 : 0;
            if (window.__charonSlingBottomHits < 2) {
              if (scroller) scroller.scrollTop += scroller.clientHeight * 5;
              return status('loading_guide_rows', { loaded: results.length, total });
            }
          }

          return JSON.stringify(results);
        })()
        """;	
		
		private static string GetPhiloScraperJs() => """
        (async () => {
          const status = (s, extra) => JSON.stringify(Object.assign({ status: s, url: location.origin + location.pathname }, extra || {}));
          const onPhilo = /(^|\.)philo\.com$/i.test(location.hostname);

          if (!onPhilo || !location.pathname.startsWith('/player')) {
            return status(onPhilo ? 'not_logged_in' : 'waiting_for_philo');
          }

          // The guide is a GraphQL "page" query sent as a persisted query whose hash changes with Philo's releases.
          // Capture the app's own request instead of hard-coding it, then replay it with large page sizes.
          if (!window.__charonPhiloFetchHooked) {
            window.__charonPhiloFetchHooked = true;
            const origFetch = window.fetch;
            window.__charonPhiloOrigFetch = origFetch;
            window.fetch = function (input, init) {
              try {
                const url = typeof input === 'string' ? input : (input && input.url) || '';
                const body = init && init.body;
                if (/\/graphql/i.test(url) && typeof body === 'string' && body.includes('GUIDE')) {
                  const ops = JSON.parse(body);
                  const op = (Array.isArray(ops) ? ops : [ops])
                    .find((o) => o && o.operationName === 'page' && o.variables && o.variables.pageType === 'GUIDE');
                  if (op) window.__charonPhiloGuideOp = op;
                }
              } catch (e) {}
              return origFetch.apply(this, arguments);
            };
          }

          const op = window.__charonPhiloGuideOp;
          if (!op) {
            // The app caches the guide in memory, so if it loaded before the hook existed, reload Home and open it from there
            if (location.pathname.startsWith('/player/guide')) {
              location.href = '/player/mytv';
              return status('reloading_home');
            }
            const link = document.querySelector('a[href="/player/guide"]');
            if (link) link.click();
            return status(link ? 'opening_guide' : 'waiting_for_philo_app');
          }

          const doFetch = window.__charonPhiloOrigFetch || window.fetch;
          const results = [];
          const seen = new Set();
          let cursor = null;

          // The server returns at most 200 rows per request
          for (let page = 0; page < 20; page++) {
            const body = [{
              ...op,
              variables: { ...op.variables, firstGroups: 200, initialTiles: 1, numSparseGroups: 0, startCursor: null, endCursor: cursor }
            }];
            const res = await doFetch('/graphql', {
              method: 'POST',
              credentials: 'include',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(body)
            });
            if (!res.ok) return status('guide_request_failed', { http: res.status });

            const json = await res.json();
            const reply = Array.isArray(json) ? json[0] : json;
            const groups = reply && reply.data && reply.data.page && reply.data.page.groups;
            if (!groups || !Array.isArray(groups.edges)) {
              window.__charonPhiloGuideOp = null;
              return status('guide_query_error', { errors: JSON.stringify((reply && reply.errors) || null).slice(0, 300) });
            }

            for (const edge of groups.edges) {
              const node = edge && edge.node;
              const ch = node && node.channel;
              if (!ch || !ch.channelId || seen.has(ch.channelId)) continue;
              seen.add(ch.channelId);

              const name = String(ch.displayName || '').trim();
              if (!name) continue;

              // channelId is base64 of "Channel:<number>"
              let numericId = '';
              try { numericId = (atob(ch.channelId.replace(/-/g, '+').replace(/_/g, '/')).match(/(\d+)\s*$/) || [])[1] || ''; } catch (e) {}
              const suffix = (numericId || ch.channelId.replace(/[^A-Za-z0-9]/g, '')).slice(-8).toLowerCase();
              const section = String((node.header && node.header.title) || '');

              results.push({
                Id: `${name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}-${suffix}`,
                Name: name,
                ChannelNumber: '',
                StationId: '',
                // Philo redirects a channel link to whatever is airing on it now
                Url: `https://www.philo.com/player/player/channel/${ch.channelId}`,
                // The guide lists the subscription lineup under "All channels", then Philo's free channels under "Free channels"
                IsHidden: /\bfree\b/i.test(section)
              });
            }

            if (!groups.pageInfo || !groups.pageInfo.hasNextPage || !groups.pageInfo.endCursor) break;
            cursor = groups.pageInfo.endCursor;
          }

          return JSON.stringify(results);
        })()
        """;
		
		private static string GetNbcScraperJs() => """
        (async () => {
          // Wait for Chrome to actually navigate to nbc.com before attempting the API fetch
          if (!location.hostname.includes('nbc.com')) {
            return JSON.stringify({ status: 'waiting_for_page' });
          }

          const results = [
            // Hardcode known Gracenote IDs for National feeds so Charon skips searching them
            { Id: "bravo-east", Name: "Bravo (East)", ChannelNumber: "210", StationId: "99936", Url: "https://www.nbc.com/live?brand=bravo&callsign=BRAVOHD" },
            { Id: "bravo-west", Name: "Bravo (West)", ChannelNumber: "211", StationId: "102960", Url: "https://www.nbc.com/live?brand=bravo&callsign=BRAVOHP" },
            { Id: "nbc-universo-east", Name: "NBC Universo (East)", ChannelNumber: "215", StationId: "96713", Url: "https://www.nbc.com/live?brand=nbc-universo&callsign=UNVSOHD" },
            { Id: "nbc-universo-west", Name: "NBC Universo (West)", ChannelNumber: "216", StationId: "102959", Url: "https://www.nbc.com/live?brand=nbc-universo&callsign=UVSPHST" },
            // Free / FAST
            { Id: "nbc-news-now", Name: "NBC News NOW", ChannelNumber: "220", StationId: "114174", Url: "https://www.nbc.com/live?brand=nbc-news&callsign=NBCNN" },
            { Id: "nbc-sports-now", Name: "NBC Sports Now", ChannelNumber: "221", StationId: "114140", Url: "https://www.nbc.com/live?brand=nbc-sports-now&callsign=PK22" }
          ];

          try {
            const r = await fetch('https://geolocation.digitalsvc.apps.nbcuni.com/geolocation/live/nbc', {
              method: 'POST',
              headers: { 
                'Content-Type': 'application/json', 
                'Accept': 'application/media.geo-v2+json',
                'client': 'oneapp', 
                'authorization': 'NBC-Basic key="nbc_live", version="3.0", type="cpc"' 
              },
              body: JSON.stringify({ adobeMvpdId: null, device: 'web' })
            });
            
            if (r.ok) {
              const data = await r.json();
              const callSign = data?.localizedChannelInfo?.geoChannel;
              if (callSign) {
                const call = callSign.toUpperCase();
                results.unshift({
                  Id: "nbc-local",
                  Name: call,
                  ChannelNumber: "204",
                  StationId: "", 
                  Url: `https://www.nbc.com/live?brand=nbc&callsign=${call}`
                });
              }
              return JSON.stringify(results);
            }
          } catch (e) {}

          window.__charonNbcGeoAttempts = (window.__charonNbcGeoAttempts || 0) + 1;
          if (window.__charonNbcGeoAttempts > 10) {
            // Give up and return just the national channels if geolocation fails for 10 straight seconds
            return JSON.stringify(results);
          }

          return JSON.stringify({ status: 'fetching_geo_data' });
        })();
        """;
		
		private static string GetHuluScraperJs() => """
        (async () => {
          const status = (s, extra) => JSON.stringify(Object.assign({ status: s, url: location.origin + location.pathname }, extra || {}));

          if (!/(^|\.)hulu\.com$/i.test(location.hostname)) return status('waiting_for_hulu');

          if (!location.pathname.startsWith('/live')) {
            if (/^\/(welcome|login|signup)/i.test(location.pathname)) return status('not_logged_in');
            if (location.pathname.startsWith('/profiles')) return status('waiting_for_profile_picker');
            location.href = 'https://www.hulu.com/live';
            return status('navigating_to_live');
          }

          // The live guide keeps its channel lists in the app's Redux store (window.store). "hulu:guide:all-channels"
          // is the guide's Channels tab: every channel in the subscription, locals included (named by call sign).
          // Its channelId is the same id a /watch/<id> link takes, which redirects to whatever is airing now.
          let live = null;
          try { live = window.store && window.store.getState().LevelOne.live; } catch (e) {}
          const cols = (live && live.collections) || [];
          const all = cols.find((c) => c.collectionId === 'hulu:guide:all-channels');
          if (!all || !Array.isArray(all.channels) || all.channels.length === 0) return status('waiting_for_guide');

          const results = [];
          const seen = new Set();
          for (const c of all.channels) {
            const id = String(c.channelId || '').toLowerCase();
            const name = String(c.channelName || '').trim();
            if (!/^[0-9a-f-]{36}$/.test(id) || !name || seen.has(id)) continue;
            seen.add(id);

            results.push({
              Id: `${name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}-${id.slice(0, 8)}`,
              Name: name,
              ChannelNumber: '',
              StationId: '',
              Url: `https://www.hulu.com/watch/${id}`
            });
          }

          return JSON.stringify(results);
        })()
        """;
		
		private static string GetFuboScraperJs() => """
        (async () => {
          const status = (s, extra) => JSON.stringify(Object.assign({ status: s, url: location.origin + location.pathname }, extra || {}));

          if (!/(^|\.)fubo\.tv$/i.test(location.hostname)) return status('waiting_for_fubo');
          if (/^\/(?:p\/)?(welcome|signin|login|signup)/i.test(location.pathname)) return status('not_logged_in');

          // The guide's channel list comes from api.fubo.tv/papi/v1/guide/epg (XHR, with auth headers the app adds).
          // Capture the app's own response rather than calling the API directly. It lists only the channels in the
          // account's subscription, each as a "channel-cell" whose id is the channel_id that Fubo's channel links take.
          if (!window.__charonFuboXhrHooked) {
            window.__charonFuboXhrHooked = true;
            const origOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function (method, url) {
              try {
                if (/\/papi\/v1\/guide\/epg/i.test(String(url))) {
                  this.addEventListener('load', () => {
                    try {
                      const json = JSON.parse(this.responseText);
                      const cells = (((json.content || {}).epg || {}).components || []).filter((c) => c && c.type === 'channel-cell');
                      if (cells.length > ((window.__charonFuboCells || []).length)) window.__charonFuboCells = cells;
                    } catch (e) {}
                  });
                }
              } catch (e) {}
              return origOpen.apply(this, arguments);
            };
          }

          const cells = window.__charonFuboCells;
          if (!cells || cells.length === 0) {
            // The scan opens on Home, so the guide's first request happens after the hook is in place.
            // If the guide was already showing, go Home and back to make it request the lineup again.
            const now = Date.now();
            if (now - (window.__charonFuboNavAt || 0) < 4000) return status('waiting_for_guide');
            window.__charonFuboNavAt = now;
            const onGuide = /^\/p\/guide/i.test(location.pathname);
            const nav = document.querySelector(onGuide ? '[data-testid="navigation-home-item"]' : '[data-testid="navigation-guide-item"]');
            if (nav) { nav.click(); return status(onGuide ? 'reopening_guide' : 'opening_guide'); }
            return status('waiting_for_fubo_app');
          }

          const results = [];
          const seen = new Set();
          for (const c of cells) {
            const id = String(c.id || '').trim();
            const name = String((c.name && c.name.text) || '').trim();
            if (!/^\d+$/.test(id) || !name || seen.has(id)) continue;
            seen.add(id);

            // The full guide title (e.g. "ABC (KMGH - Denver)", "Fubo Movies (VOD2LIVE)") is only in the tracking
            // parameters of the channel's details link
            let fullName = '';
            try {
              const url = ((((c.actions || {}).on_click || [])[0] || {}).endpoint || {}).url || '';
              const m = url.match(/[?&]trkOriginElement=([^&]*)/);
              if (m) fullName = decodeURIComponent(m[1].replace(/\+/g, ' ')).trim();
            } catch (e) {}

            // Name local stations by call sign ("KMGH ABC"), which the Gracenote matcher looks up directly
            let displayName = name;
            const local = fullName.match(/\(\s*([WK][A-Z]{2,3})(?:-(?:TV|DT|CD|LD))?\s*[-–]/);
            if (local) displayName = `${local[1]} ${name}`;

            results.push({
              Id: `${displayName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}-${id}`,
              Name: displayName,
              ChannelNumber: '',
              StationId: '',
              Url: `https://link.fubo.tv/al1%3Fv%3D1%26a%3Dplay%26t%3Dchannel%26channel_id%3D${id}`,
              // Fubo's own looping channels have no Gracenote listing; add them hidden, like Sling Freestream
              IsHidden: /VOD2LIVE/i.test(fullName)
            });
          }

          return JSON.stringify(results);
        })()
        """;
		
		private static string GetAbcScraperJs() => """
        (() => {
          if (!location.hostname.includes('abc.com')) {
            return JSON.stringify({ status: 'waiting_for_page', url: location.href });
          }

          // Wait until the client-side router has resolved /watch-live/<local-uuid> and rendered the first carousel tile
          const firstTile = document.querySelector('a.AnchorLink[aria-label^="ABC (1 of "]')
            || Array.from(document.querySelectorAll('a[aria-label]')).find(a => /^ABC\s*\(1 of \d+\)/i.test(a.getAttribute('aria-label') || ''));
          const pathUuidMatch = location.pathname.match(/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i);

          window.__charonAbcAttempts = (window.__charonAbcAttempts || 0) + 1;
          if ((!firstTile || !pathUuidMatch) && window.__charonAbcAttempts <= 10) {
            return JSON.stringify({ status: 'waiting_for_local_affiliate_redirect', url: location.href });
          }

          // Core National lineup with verified permanent UUIDs and Gracenote StationIds
          const coreNational = [
            { Id: "abc-news-live", Name: "ABC News Live", ChannelNumber: "251", StationId: "113380", Url: "https://abc.com/watch-live/784b058c-3aee-4ec9-a6d3-754ba6e4dbac", IsHidden: false },
            { Id: "freeform-east", Name: "Freeform East", ChannelNumber: "252", StationId: "59615", Url: "https://abc.com/watch-live/885c669e-fa9a-4039-b42e-6c85c90cc86d", IsHidden: false },
            { Id: "freeform-west", Name: "Freeform West", ChannelNumber: "253", StationId: "63324", Url: "https://abc.com/watch-live/3507c750-e86a-4c0f-8ff4-dd23c4859009", IsHidden: true },
            { Id: "fx-east", Name: "FX East", ChannelNumber: "254", StationId: "58574", Url: "https://abc.com/watch-live/93256af4-5e80-4558-aa2e-2bdfffa119a0", IsHidden: false },
            { Id: "fx-west", Name: "FX West", ChannelNumber: "255", StationId: "59814", Url: "https://abc.com/watch-live/2cee3401-f63b-42d0-b32e-962fef610b9e", IsHidden: true },
            { Id: "fxm", Name: "FXM", ChannelNumber: "256", StationId: "70253", Url: "https://abc.com/watch-live/d298ab7e-c6b1-4efa-ac6e-a52dceed92ee", IsHidden: false },
            { Id: "fxx-east", Name: "FXX East", ChannelNumber: "257", StationId: "66379", Url: "https://abc.com/watch-live/49f4a471-8d36-4728-8457-ea65cbbc84ea", IsHidden: false },
            { Id: "fxx-west", Name: "FXX West", ChannelNumber: "258", StationId: "82571", Url: "https://abc.com/watch-live/e4c83395-62ed-4a49-829a-c55ab3c33e7d", IsHidden: true },
            { Id: "nat-geo-east", Name: "Nat Geo East", ChannelNumber: "259", StationId: "49438", Url: "https://abc.com/watch-live/0826a9a3-3384-4bb5-8841-91f01cb0e3a7", IsHidden: false },
            { Id: "nat-geo-west", Name: "Nat Geo West", ChannelNumber: "260", StationId: "71601", Url: "https://abc.com/watch-live/91456580-f32f-417c-8e1a-9f82640832a7", IsHidden: true },
            { Id: "nat-geo-wild", Name: "Nat Geo Wild", ChannelNumber: "261", StationId: "67331", Url: "https://abc.com/watch-live/239b9590-583f-4955-a499-22e9eefff9cf", IsHidden: false }
          ];

          const results = [];
          const seenUuids = new Set();

          // 1. Add Local ABC Affiliate first (e.g. WSOC -> matched to 11849 by Channels DVR)
          if (firstTile && pathUuidMatch) {
            const localUuid = pathUuidMatch[1].toLowerCase();
            const aria = firstTile.getAttribute('aria-label') || '';
            const callMatch = aria.match(/^ABC\s*\(1 of \d+\)\s+([WK][A-Z0-9]{2,4})\b/i);
            const callSign = callMatch ? callMatch[1].toUpperCase() : 'ABC Local';

            const isStaticNational = coreNational.some(c => c.Url.toLowerCase().includes(localUuid));
            const localUrl = isStaticNational ? 'https://abc.com/watch-live' : `https://abc.com/watch-live/${localUuid}`;

            if (!isStaticNational) seenUuids.add(localUuid);

            results.push({
              Id: "abc-local",
              Name: callSign,
              ChannelNumber: "250",
              StationId: "", // Left blank so MatchGracenoteStationsAsync resolves WSOC -> 11849!
              Url: localUrl,
              IsHidden: false
            });
          }

          // 2. Add all 11 Core National channels
          for (const ch of coreNational) {
            const m = ch.Url.match(/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i);
            if (m) seenUuids.add(m[1].toLowerCase());
            results.push(ch);
          }

          // 3. Also append any regional O&O FAST streams from window['__abc-com__'] (hidden by default)
          const modules = window['__abc-com__']?.page?.content?.shell?.module || [];
          const rawChannels = modules.flatMap(m => m?.channels || []).filter(c => c && c.urlValue);
          let extraNum = 262;

          for (const c of rawChannels) {
            const uuidMatch = String(c.urlValue || '').match(/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i);
            if (!uuidMatch) continue;

            const uuid = uuidMatch[1].toLowerCase();
            if (seenUuids.has(uuid)) continue;
            seenUuids.add(uuid);

            const rawTitle = String(c.title || '').trim();
            if (!rawTitle) continue;

            const slug = rawTitle.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
            results.push({
              Id: slug,
              Name: rawTitle,
              ChannelNumber: String(extraNum++),
              StationId: "",
              Url: `https://abc.com/watch-live/${uuid}`,
              IsHidden: true
            });
          }

          return JSON.stringify(results);
        })()
        """;
		
		private static string GetCbsScraperJs() => """
        (() => {
          if (!location.hostname.includes('cbs.com')) {
            return JSON.stringify({ status: 'waiting_for_page', url: location.href });
          }

          // 1. Actively click CBS's "Start Watching" splash screen button if present
          const buttons = Array.from(document.querySelectorAll('button, a, div[role="button"], span, .button'));
          const startBtn = buttons.find(el => {
            const t = (el.textContent || '').trim().toLowerCase();
            return t === 'start watching' || t === 'start watching »' || el.getAttribute('aria-label')?.toLowerCase() === 'start watching';
          });

          if (startBtn && Date.now() - (window.__charonCbsClickAt || 0) > 2000) {
            window.__charonCbsClickAt = Date.now();
            startBtn.click();
            return JSON.stringify({ status: 'clicked_start_watching_button', url: location.href });
          }

          const results = [
            { Id: "cbs-news-247", Name: "CBS News 24/7", ChannelNumber: "270", StationId: "", Url: "https://www.cbs.com/live-tv/stream/cbs-news/", IsHidden: false },
            { Id: "cbs-sports-hq", Name: "CBS Sports HQ", ChannelNumber: "271", StationId: "", Url: "https://www.cbs.com/live-tv/stream/sports/", IsHidden: true },
            { Id: "cbs-sports-golazo", Name: "CBS Sports Golazo Network", ChannelNumber: "272", StationId: "", Url: "https://www.cbs.com/live-tv/stream/golazo/", IsHidden: true }
          ];

          // 2. Hook fetch / XHR to catch the Paramount stream URL containing the call sign (e.g. /WBTV/live/)
          if (!window.__charonCbsHooked) {
            window.__charonCbsHooked = true;
            const checkUrl = (u) => {
              const m = String(u || '').match(/\/([WK][A-Z]{2,3})\/live\//i);
              if (m) window.__charonCbsCallSign = m[1].toUpperCase();
            };
            
            const origFetch = window.fetch;
            window.fetch = function (input, init) {
              checkUrl(typeof input === 'string' ? input : input?.url);
              return origFetch.apply(this, arguments);
            };

            const origOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function (method, url) {
              checkUrl(url);
              return origOpen.apply(this, arguments);
            };
          }

          let callSign = window.__charonCbsCallSign || '';

          // 3. Check performance resource entries as fallback
          if (!callSign) {
            for (const r of performance.getEntriesByType('resource')) {
              const m = r.name.match(/\/([WK][A-Z]{2,3})\/live\//i);
              if (m) {
                callSign = m[1].toUpperCase();
                window.__charonCbsCallSign = callSign;
                break;
              }
            }
          }

          // 4. Check global CBS Registry or script data if available
          if (!callSign && window.CBS && window.CBS.Registry) {
            // Check if callsign is embedded anywhere in global state
          }

          window.__charonCbsAttempts = (window.__charonCbsAttempts || 0) + 1;
          if (!callSign && window.__charonCbsAttempts <= 25) {
            return JSON.stringify({ status: 'waiting_for_cbs_player_to_load_station', attempts: window.__charonCbsAttempts, url: location.href });
          }

          if (callSign && callSign !== 'CBS') {
            results.unshift({
              Id: `cbs-local-${callSign.toLowerCase()}`,
              Name: callSign,
              ChannelNumber: "269",
              StationId: "", // Left blank so MatchGracenoteStationsAsync resolves WBTV via Channels DVR!
              Url: "https://www.cbs.com/live-tv/stream/tveverywhere/",
              IsHidden: false
            });
          }

          return JSON.stringify(results);
        })()
        """;
		
		private static string GetFoxScraperJs() => """
        (async () => {
          if (!location.hostname.includes('fox.com')) {
            return JSON.stringify({ status: 'waiting_for_page', url: location.href });
          }

          const initialLogos = document.querySelectorAll('[data-testid="GuideChannelLogo"] button');
          const watchEl = document.querySelector('a[href^="/watch/epg/"]');

          window.__charonFoxAttempts = (window.__charonFoxAttempts || 0) + 1;
          if ((initialLogos.length < 3 || !watchEl) && window.__charonFoxAttempts <= 12) {
            return JSON.stringify({
              status: 'waiting_for_fox_guide',
              logosFound: initialLogos.length,
              hasWatchLink: !!watchEl,
              url: location.href
            });
          }

          // Extract local FOX call sign (e.g. WJZY) from the hero Watch Live link
          let localCallSign = '';
          const watchHref = watchEl?.getAttribute('href') || '';
          const callMatch = watchHref.match(/\/watch\/epg\/([WK][A-Z]{2,3})-program-/i);
          if (callMatch) localCallSign = callMatch[1].toUpperCase();

          const friendlyNames = {
            FOXD2C: localCallSign || 'FOX Local',
            FNC: 'FOX News',
            FS1: 'FS1',
            FS2: 'FS2',
            BTN: 'Big Ten Network',
            FWX: 'FOX Weather',
            FBN: 'FOX Business',
            FOXD: 'FOX Deportes',
            TMZ: 'TMZ',
            SOUL: 'FOX Soul',
            FMSC: 'FOX Music',
            LIVENOW: 'LiveNOW from FOX',
            JUSTICE: 'Justice Central',
            FNT: 'FOX Nation'
          };

          const coreCodes = new Set(['FOXD2C', 'FNC', 'FS1', 'FS2', 'BTN', 'FWX', 'FBN']);
          const channels = new Map();
          let chNum = 280;

          const harvestVisible = () => {
            for (const btn of document.querySelectorAll('[data-testid="GuideChannelLogo"] button')) {
              const code = (btn.getAttribute('aria-label') || btn.querySelector('img')?.getAttribute('alt') || '').trim().toUpperCase();
              if (!code || channels.has(code)) continue;

              const name = friendlyNames[code] || code;
              channels.set(code, {
                Id: code === 'FOXD2C' && localCallSign
                  ? `fox-local-${localCallSign.toLowerCase()}`
                  : `fox-${code.toLowerCase()}`,
                Name: name,
                ChannelNumber: String(chNum++),
                StationId: '',
                Url: `https://www.fox.com/live/channels#charon_channel=${encodeURIComponent(code)}`,
                IsHidden: !coreCodes.has(code)
              });
            }
          };

          // Scroll through the Radix scroll area so any virtualized/below-the-fold rows mount
          const scroller = document.querySelector('[data-radix-scroll-area-viewport]')
            || document.querySelector('[data-testid="ChannelsScrollArea"]')
            || document.scrollingElement;

          if (scroller) scroller.scrollTop = 0;
          await new Promise(r => setTimeout(r, 300));
          harvestVisible();

          let lastTop = -1;
          let stagnant = 0;
          for (let i = 0; i < 20; i++) {
            if (!scroller) break;
            scroller.scrollTop += 400;
            await new Promise(r => setTimeout(r, 200));
            harvestVisible();

            if (scroller.scrollTop === lastTop) {
              stagnant++;
              if (stagnant >= 2) break;
            } else {
              stagnant = 0;
              lastTop = scroller.scrollTop;
            }
          }

          if (channels.size === 0) {
            return JSON.stringify({ status: 'waiting_for_fox_guide', url: location.href });
          }

          return JSON.stringify(Array.from(channels.values()));
        })()
        """;
		
		private static string GetUsaScraperJs() => """
        (() => {
          if (!location.hostname.includes('usanetwork.com')) {
            return JSON.stringify({ status: 'waiting_for_page', url: location.href });
          }

          const rows = Array.from(document.querySelectorAll('.epg-channel-feed-container'));
          if (rows.length === 0) {
            return JSON.stringify({ status: 'waiting_for_usa_epg_rows', url: location.href });
          }

          const displayNames = {
            'usa-east':    'USA (East)',
            'usa-west':    'USA (West)',
            'e!-east':     'E! (East)',
            'e!-west':     'E! (West)',
            'syfy-east':   'SYFY (East)',
            'syfy-west':   'SYFY (West)',
            'oxygen-east': 'Oxygen (East)',
            'oxygen-west': 'Oxygen (West)',
            'cnbc':        'CNBC',
            'msnow':       'MS NOW',
            'golf':        'Golf Channel'
          };

          const results = [];
          const seen = new Set();
          let chNum = 300;

          for (const row of rows) {
            const firstTile = row.querySelector('.epg-tile-container');
            const info = firstTile?.querySelector('.tile-info') || row.querySelector('.tile-info');
            const aria = (info?.getAttribute('aria-label') || '').trim();
            if (!aria) continue;

            // First token is the exact feed prefix (e.g. "USA-East", "E!-West", "MSNOW", "Golf")
            const token = aria.split(/\s+/)[0];
            const key = token.toLowerCase();
            if (!token || seen.has(key)) continue;
            seen.add(key);

            const name = displayNames[key] || token.replace(/-(East|West)$/i, ' ($1)');
            const slug = key.replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

            results.push({
              Id: `usa-${slug}`,
              Name: name,
              ChannelNumber: String(chNum++),
              StationId: '',
              Url: `https://www.usanetwork.com/live#charon_channel=${encodeURIComponent(token)}`,
              IsHidden: false
            });
          }

          if (results.length === 0) {
            return JSON.stringify({ status: 'waiting_for_usa_epg_rows', url: location.href });
          }

          return JSON.stringify(results);
        })()
        """;
		
		private static string GetEspnScraperJs() => """
        (async () => {
          if (!location.hostname.includes('espn.com')) {
            return JSON.stringify({ status: 'waiting_for_page', url: location.href });
          }

          // Scroll down to trigger lazy-loading of the Channels row near the footer
          let links = Array.from(document.querySelectorAll('a[data-track-nav_item*="|channels|"]'));
          if (links.length === 0) {
            window.scrollTo(0, document.body.scrollHeight);
            await new Promise(r => setTimeout(r, 600));
            links = Array.from(document.querySelectorAll('a[data-track-nav_item*="|channels|"]'));
          }

          if (links.length === 0) {
            return JSON.stringify({ status: 'waiting_for_espn_channels_row', url: location.href });
          }

          const renameMap = {
            'ACCN': 'ACC Network'
          };

          // Keep linear 24/7 networks visible by default; hide event hubs / conference / plus shelves
          const coreLinear = new Set([
            'ESPN', 'ESPN2', 'ESPNU', 'ESPNews', 'SEC Network', 'ACC Network', 'NFL Network', 'ESPN Deportes'
          ]);

          const results = [];
          const seen = new Set();
          let chNum = 320;

          for (const a of links) {
            const rawTitle = (a.getAttribute('data-track-title') || a.getAttribute('aria-label') || '').trim();
            const href = (a.getAttribute('href') || '').trim();
            if (!rawTitle || !href) continue;

            const name = renameMap[rawTitle] || rawTitle;
            const key = name.toLowerCase();
            if (seen.has(key)) continue;
            seen.add(key);

            const fullUrl = href.startsWith('http') ? href : `https://www.espn.com${href}`;
            const slug = key.replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

            results.push({
              Id: `espn-${slug}`,
              Name: name,
              ChannelNumber: String(chNum++),
              StationId: '',
              Url: fullUrl,
              IsHidden: !coreLinear.has(name)
            });
          }

          return JSON.stringify(results);
        })()
        """;
		
    // Direct, verified Gracenote TMS StationId table (East/Central/Mountain ID, Pacific ID)
    // Bypasses fuzzy text search completely for core national networks!
    private static readonly Dictionary<string, (string EastId, string WestId)> VerifiedGracenoteStationIds =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Kids & Family
        { "Disney Channel",             ("59684",  "59685") },
        { "Disney Junior",              ("74885",  "75004") },
        { "Disney XD",                  ("60006",  "60007") },
        { "Nickelodeon",                ("59432",  "59433") },
        { "Nick Jr.",                   ("82649",  "82650") },
        { "Nicktoons",                  ("30420",  "30420") },
        { "TeenNick",                   ("59036",  "59036") },
        { "Cartoon Network",            ("60048",  "60049") },
        { "Discovery Family",           ("67749",  "67749") },
        { "Discovery Family Channel",   ("67749",  "67749") },
        { "Universal Kids",             ("70225",  "70225") },
        { "PBS Kids",                   ("101364", "101364") },
        { "Public Broadcasting System Kids", ("101364", "101364") },

        // General Entertainment
        { "E!",                         ("61812",  "105773") },
        { "E! Entertainment Television",("61812",  "105773") },
        { "Freeform",                   ("59615",  "59616") },
        { "USA",                        ("58452",  "74030") },
        { "USA Network",                ("58452",  "74030") },
        { "TNT",                        ("42642",  "61340") },
        { "Turner Network Television",  ("42642",  "61340") },
        { "TBS",                        ("58515",  "67890") },
        { "Turner Broadcasting System", ("58515",  "67890") },
        { "truTV",                      ("64490",  "65717") },
        { "FX",                         ("58574",  "58575") },
        { "FXX",                        ("66379",  "82571") },
        { "FXM",                        ("70253",  "70253") },
        { "FXM (FX Movie Channel)",     ("70253",  "70253") },
        { "Bravo",                      ("58625",  "73994") },
        { "SYFY",                       ("58623",  "65626") },
        { "Oxygen",                     ("70522",  "70522") },
        { "Oxygen True Crime",          ("70522",  "70522") },
        { "AMC",                        ("59337",  "92041") },
        { "American Movie Classics",    ("59337",  "92041") },
        { "BBC America",                ("64492",  "64492") },
        { "IFC",                        ("59444",  "59444") },
        { "Independent Film Channel",   ("59444",  "59444") },
        { "SundanceTV",                 ("71280",  "71280") },
		{ "Sundance TV",                ("71280",  "71280") },
        { "WE tv",                      ("59296",  "59296") },
        { "A&E",                        ("51529",  "73992") },
        { "Arts and Entertainment",     ("51529",  "73992") },
        { "History",                    ("57708",  "73993") },
		{ "The HISTORY Channel",        ("57708",  "73993") },
        { "Lifetime",                   ("60150",  "73997") },
        { "Lifetime Networks",          ("60150",  "73997") },
        { "LMN",                        ("55887",  "55887") },
        { "Lifetime Movies",            ("55887",  "55887") },
        { "FYI",                        ("58988",  "58988") },
        { "fyi,",                       ("58988",  "58988") },
        { "VICE",                       ("18822",  "18822") },
        { "Comedy Central",             ("62420",  "62421") },
        { "Paramount",                  ("59186",  "59187") },
        { "Paramount Network",          ("59186",  "59187") },
        { "TV Land",                    ("73541",  "73541") },
        { "MTV",                        ("60964",  "60965") },
        { "Music Television",           ("60964",  "60965") },
        { "MTV2",                       ("75077",  "75077") },
        { "Music Television 2",         ("75077",  "75077") },
        { "MTV Classic",                ("88870",  "88870") },
        { "Music Television Classic",   ("88870",  "88870") },
        { "VH1",                        ("60046",  "60047") },
        { "VH-1: Video Hits One",       ("60046",  "60047") },
        { "BET",                        ("63236",  "63237") },
        { "Black Entertainment Television", ("63236", "63237") },
        { "BET Her",                    ("63220",  "63220") },
        { "CMT",                        ("59440",  "59440") },
        { "Logo",                       ("46762",  "46762") },
        { "Pop",                        ("68796",  "68796") },
		{ "Pop TV",                     ("68796",  "68796") },
        { "Hallmark Channel",           ("66268",  "66268") },
        { "Hallmark Mystery",           ("61522",  "61522") },
        { "Hallmark Family",            ("105723", "105723") },
        { "GFAM",                       ("82892",  "82892") },
        { "Great American Family",      ("82892",  "82892") },
        { "Game Show Network",          ("68827",  "68827") },
        { "Turner Classic Movies",      ("64312",  "64312") },
        { "TCM",                        ("64312",  "64312") },
        { "TV One",                     ("61960",  "61960") },
        { "Cleo TV",                    ("110288", "110288") },
        { "Bounce",                     ("73067",  "73067") },
        { "Comet TV",                   ("97051",  "97051") },
        { "Comet",                      ("97051",  "97051") },
        { "CHARGE!",                    ("102148", "102148") },
        { "Cozi",                       ("89994",  "89994") },
		{ "Cozi TV",                    ("89994",  "89994") },
        { "Start TV",                   ("109454", "109454") },
        { "Start TV Network",           ("109454", "109454") },
        { "Dabl",                       ("112157", "112157") },
        { "ION",                        ("62746",  "62746") },
        { "ION Media Networks",         ("62746",  "62746") },
        { "Reelz",                      ("119136", "119136") },
        { "UP (Uplifting Entertainment)",("66143", "66143") },
		{ "UPtv",                       ("66143",  "66143") },

        // Discovery / Nat Geo / Lifestyle
        { "Discovery Channel",          ("56905",  "56906") },
        { "Discovery",                  ("56905",  "56906") },
        { "Food Network",               ("50747",  "50748") },
        { "HGTV",                       ("49788",  "49789") },
        { "Home & Garden Television",   ("49788",  "49789") },
        { "TLC",                        ("57391",  "57392") },
        { "The Learning Channel",       ("57391",  "57392") },
        { "ID",                         ("65342",  "65342") },
        { "Investigation Discovery",    ("65342",  "65342") },
        { "Animal Planet",              ("57394",  "57395") },
        { "Travel Channel",             ("59303",  "59303") },
        { "Science",                    ("57390",  "57390") },
        { "Science Channel",            ("57390",  "57390") },
        { "The Science Channel",        ("57390",  "57390") },
        { "Discovery Turbo",            ("31046",  "31046") },
        { "MotorTrend",                 ("31046",  "31046") },
        { "OWN",                        ("70388",  "70389") },
        { "Oprah Winfrey Network",      ("70388",  "70389") },
        { "Magnolia Network",           ("67454",  "67454") },
        { "Cooking Channel",            ("68065",  "68065") },
        { "Destination America",        ("60468",  "60468") },
        { "American Heroes Channel",    ("78808",  "78808") },
        { "American Heroes",            ("78808",  "78808") },
        { "Discovery Life",             ("92204",  "92204") },
        { "Nat Geo",                    ("49438",  "71601") },
        { "National Geographic",        ("49438",  "71601") },
        { "National Geographic Channel",("49438",  "71601") },
        { "Nat Geo Wild",               ("67375",  "67375") },
        { "National Geographic Wild",   ("67375",  "67375") },
        { "Smithsonian Channel",        ("65799",  "65799") },

        // News & Business
        { "CNN",                        ("58646",  "58646") },
        { "Cable News Network",         ("58646",  "58646") },
        { "HLN",                        ("64549",  "64549") },
        { "Headline News",              ("64549",  "64549") },
        { "FOX News",                   ("60179",  "60179") },
        { "Fox News Channel",           ("60179",  "60179") },
        { "FOX Business",               ("58718",  "58718") },
        { "Fox Business Network",       ("58718",  "58718") },
		{ "LiveNOW from FOX",          ("119219",  "119219") },
        { "MSNBC",                      ("64241",  "64241") },
        { "MS NOW",                     ("64241",  "64241") },
        { "CNBC",                       ("58780",  "58780") },
        { "NewsNation",                 ("91096",  "91096") },
        { "NEWSMAX",                    ("97163",  "97163") },
        { "Newsmax TV",                 ("97163",  "97163") },
        { "BBC News",                   ("107709", "107709") },
        { "The Weather Channel",        ("58812",  "58812") },
        { "FOX Weather",                ("93141",  "93141") },
        { "C-SPAN",                     ("68344",  "68344") },
        { "C-SPAN2",                    ("68334",  "68334") },
        { "C-SPAN3",                    ("68332",  "68332") },

        // Sports
        { "ESPN",                       ("32645",  "32645") },
        { "Entertainment Sports Programming Network", ("32645", "32645") },
        { "ESPN2",                      ("45507",  "45507") },
        { "ESPNews",                    ("59976",  "59976") },
        { "ESPNU",                      ("60696",  "60696") },
        { "SEC Network",                ("89714",  "89714") },
        { "ACC Network",                ("111871", "111871") },
        { "FS1",                        ("82547",  "82547") },
        { "FOX Sports 1",               ("82547",  "82547") },
        { "FS2",                        ("59305",  "59305") },
        { "FOX Sports 2",               ("59305",  "59305") },
        { "BTN",                        ("58321",  "58321") },
        { "Big Ten Network",            ("58321",  "58321") },
        { "CBS Sports Network",         ("59250",  "59250") },
        { "NFL Network",                ("45399",  "45399") },
        { "National Football League Network", ("45399", "45399") },
        { "NBA TV",                     ("45526",  "45526") },
        { "National Basketball Association TV", ("45526", "45526") },
        { "MLB Network",                ("62081",  "62081") },
        { "Major League Baseball Network", ("62081", "62081") },
        { "NHL Network",                ("58690",  "58690") },
        { "National Hockey League Network", ("58690", "58690") },
        { "Golf Channel",               ("61854",  "61854") },
        { "Tennis Channel",             ("60316",  "60316") },
		{ "FOX Deportes",               ("72189",   "72189") },
		
		// Premium (Starz family, as titled in the Sling guide; East and West are separate channels there)
        { "STARZ",                      ("34941",  "34941") },
        { "Starz West",                 ("34949",  "34949") },
        { "Starz Kids & Family",        ("57581",  "57581") },
        { "Starz Comedy",               ("57569",  "57569") },
        { "Starz Edge",                 ("57573",  "57573") },
        { "Starz in Black",             ("67235",  "67235") },
        { "Starz Cinema",               ("67236",  "67236") },
        { "Starz Encore",               ("36225",  "36225") },
        { "Starz Encore West",          ("17125",  "17125") },
        { "Starz Encore Classic",       ("14764",  "14764") },
        { "Starz Encore Westerns",      ("102906", "102906") },
        { "Starz Encore Suspense",      ("14766",  "14766") },
        { "Starz Encore Black",         ("14870",  "14870") },
        { "Starz Encore Action",        ("72015",  "72015") },
        { "Starz Encore Family",        ("102903", "102903") },
        { "IndiePlex",                  ("65795",  "65795") },
        { "RetroPlex",                  ("65791",  "65791") },
        { "MoviePlex",                  ("83075",  "83075") },

        // Entertainment Studios & Shopping
        { "Comedy.TV",                  ("82470",  "82470") },
        { "JusticeCentral.TV",          ("78850",  "78850") },
		{ "Justice Central",            ("78850",  "78850") },
        { "Recipe.TV",                  ("71769",  "71769") },
        { "Cars.TV",                    ("69062",  "69062") },
        { "theGRIO",                    ("121310", "121310") },
        { "QVC",                        ("60222",  "60222") },
		{ "AWE",                        ("59436",  "59436") },
        { "HBCU GO",                    ("125163", "125163") },
        { "HSN",                        ("62077",  "62077") },
		{ "FOX Soul",                  ("119212",  "119212") },
		{ "TMZ",                       ("149408",  "149408") },
		
		 // Xfinity premium callSigns (matched against the stream URL; callSigns keep legacy names, e.g. HBO2H = HBO Hits)
        { "HBOHD",                      ("19548",  "19548") },
        { "HBO-W",                      ("10244",  "10244") },
        { "HBO2H",                      ("59368",  "59368") },
        { "HBOSG",                      ("10243",  "10243") },
        { "HBOCM",                      ("18429",  "18429") },
        { "HBOZN",                      ("18431",  "18431") },
        { "SHOHD",                      ("21868",  "21868") },
        { "SHO2e",                      ("11116",  "11116") },
        { "SHOCe",                      ("16153",  "16153") },
        { "SHOXe",                      ("18086",  "18086") },
        { "SHOFe",                      ("25274",  "25274") },
        { "TMCHD",                      ("35329",  "35329") },
        { "TMCXe",                      ("60951",  "60951") },
        { "MGM+",                       ("65687",  "65687") },
        { "MGMHT",                      ("67929",  "67929") },
        { "MGMMR",                      ("74073",  "74073") },
        { "MGMDR",                      ("103828", "103828") }
    };
    
    private static async Task<int> MatchGracenoteStationsAsync(
        List<ProviderChannel> channels,
        string dvrIp,
        int dvrPort,
        Action<string> log,
        Action<string> statusCallback)
    {
        string serverUrl = $"http://{dvrIp}:{dvrPort}";
        int updatedCount = 0;

        // Pacific (UTC-8), Alaska (UTC-9), Hawaii (UTC-10) use West/Pacific feeds;
        // Eastern (UTC-5), Central (UTC-6), and Mountain (UTC-7) use East feeds on YTTV.
        bool preferPacific = TimeZoneInfo.Local.BaseUtcOffset.TotalHours <= -8.0;
		
		int hiddenCount = channels.Count(c => c.IsHidden && string.IsNullOrWhiteSpace(c.StationId));
        if (hiddenCount > 0)
            log($"[Gracenote] Skipping {hiddenCount} hidden channels (unhide them and scan again to match their Gracenote IDs)");

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
            TmsStation? match = null;

            // Hulu titles separate feeds "TBS (East)" / "TBS (West)": look up the base name and take that feed's ID
            string lookupName = rawName;
            bool useWestFeed = preferPacific;
            var feedMatch = System.Text.RegularExpressions.Regex.Match(
                rawName, @"^(.+?)\s*\((East|West)\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (feedMatch.Success)
            {
                lookupName = feedMatch.Groups[1].Value.Trim();
                useWestFeed = feedMatch.Groups[2].Value.Equals("West", StringComparison.OrdinalIgnoreCase);
            }

            var xfMatch = System.Text.RegularExpressions.Regex.Match(ch.Url ?? "", @"/stream/live/([^/]+)/\d+/");
            string? urlCallSign = xfMatch.Success ? Uri.UnescapeDataString(xfMatch.Groups[1].Value) : null;

            // 1. Check Verified National Network Gracenote StationId table first (runs even if hidden)
            if (VerifiedGracenoteStationIds.TryGetValue(lookupName, out var verifiedIds) ||
                (!string.IsNullOrWhiteSpace(urlCallSign) && VerifiedGracenoteStationIds.TryGetValue(urlCallSign, out verifiedIds)))
            {
                ch.StationId = useWestFeed ? verifiedIds.WestId : verifiedIds.EastId;
                updatedCount++;
                log($"[Gracenote] [OK] {ch.Name} -> Verified US HD [StationId: {ch.StationId}]");
                continue;
            }

            // Skip remaining hidden channels that weren't in VerifiedGracenoteStationIds
            if (ch.IsHidden)
                continue;

            // 2. Skip vague local affiliate names that lack real FCC call letters (e.g. "FOX 46", "CW Charlotte", "TV64", "PBS NC", "SCETV")
            
            if (string.IsNullOrWhiteSpace(urlCallSign) && IsVagueLocalStationName(rawName))
            {
                log($"[Gracenote] [--] {ch.Name}: Local station without FCC call letters (leaving blank for manual mapping in Channels DVR)");
                continue;
            }

            // 3. For Xfinity, query by the URL callSign first
            if (!string.IsNullOrWhiteSpace(urlCallSign))
            {
                var results = await QueryChannelsDvrTmsAsync(serverUrl, urlCallSign);
                match = SelectBestTmsStation(results, urlCallSign, rawName, preferPacific);

                if (match is null)
                {
                    string cleanCs = System.Text.RegularExpressions.Regex.Replace(
                        urlCallSign, @"(?:HD|DT\d*|D)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (cleanCs.Length >= 3 && !string.Equals(cleanCs, urlCallSign, StringComparison.OrdinalIgnoreCase))
                    {
                        results = await QueryChannelsDvrTmsAsync(serverUrl, cleanCs);
                        match = SelectBestTmsStation(results, cleanCs, rawName, preferPacific);
                    }
                }
            }

            // 4. If the channel starts with a real W/K broadcast call sign (e.g. "WBTV", "WSOC 9", "WCNC NBC"), query that exact call sign
            if (match is null && HasRealBroadcastCallSign(rawName, out string fccCallSign))
            {
                var results = await QueryChannelsDvrTmsAsync(serverUrl, fccCallSign);
                match = SelectBestTmsStation(results, fccCallSign, fccCallSign, preferPacific);
            }

            // 5. Fallback: Search by raw display name for remaining national/FAST networks
            if (match is null)
            {
                var results = await QueryChannelsDvrTmsAsync(serverUrl, lookupName);
                match = SelectBestTmsStation(results, null, lookupName, useWestFeed);
            }

            if (match is not null && !string.IsNullOrWhiteSpace(match.StationId))
            {
                ch.StationId = match.StationId;
                updatedCount++;
                log($"[Gracenote] [OK] {ch.Name} -> {match.Name} ({match.CallSign}) [StationId: {match.StationId}]");
            }
            else
            {
                log($"[Gracenote] [??] {ch.Name}: No valid US match found (leaving blank for manual map)");
            }

            await Task.Delay(40);
        }

        return updatedCount;
    }

    private static bool HasRealBroadcastCallSign(string rawName, out string extractedCallSign)
    {
        extractedCallSign = "";
        string firstToken = rawName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        if (System.Text.RegularExpressions.Regex.IsMatch(firstToken, @"^[WK][A-Z]{2,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            string upper = firstToken.ToUpperInvariant();
            if (upper is not ("WEST" or "KIDS" or "WILD" or "WE" or "WETV"))
            {
                extractedCallSign = upper;
                return true;
            }
        }

        return false;
    }

    private static bool IsVagueLocalStationName(string rawName)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
            rawName,
            @"^(?:FOX|CBS|NBC|ABC|CW|MY|TV|CH|CHANNEL)\s*\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
            rawName,
            @"^(?:CW|PBS|Telemundo|Univision|UniMas|MyNetworkTV)\s+[A-Za-z]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (rawName.Equals("SCETV", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static TmsStation? SelectBestTmsStation(
        List<TmsStation>? candidates,
        string? targetCallSign,
        string rawName,
        bool preferPacific)
    {
        if (candidates is null || candidates.Count == 0)
            return null;

        bool isSpanishChannel = rawName.Contains("Univision", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Telemundo", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("UniMas", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Galavision", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Galavisión", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("TUDN", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Deportes", StringComparison.OrdinalIgnoreCase) ||
                                rawName.Contains("Universo", StringComparison.OrdinalIgnoreCase);

        TmsStation? best = null;
        int bestScore = int.MinValue;

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.StationId))
                continue;

            string name = c.Name?.Trim() ?? "";
            string call = c.CallSign?.Trim() ?? "";
            var langs = c.BcastLangs ?? [];

            // 1. Hard Reject: Inactive stations
            if (name.Contains("(inactive)", StringComparison.OrdinalIgnoreCase))
                continue;

            // 2. Hard Reject: Known international & FAST clone callSigns that pollute US searches
            if (call.EndsWith("UK", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("KR", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("IND", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("LAT", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("MUE", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("DAE", StringComparison.OrdinalIgnoreCase) ||
                call.StartsWith("VIZ", StringComparison.OrdinalIgnoreCase) ||
                call.StartsWith("PTV", StringComparison.OrdinalIgnoreCase) ||
                call.StartsWith("XSM", StringComparison.OrdinalIgnoreCase) ||
                call.Contains("FIFA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 3. Hard Reject: Non-English feeds (unless the channel itself is a Spanish network)
            if (!isSpanishChannel && langs.Count > 0)
            {
                bool hasUsEnglish = langs.Any(l =>
                    l.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals("en-US", StringComparison.OrdinalIgnoreCase));

                if (!hasUsEnglish)
                    continue;
            }

            int score = 0;

            // 4. CallSign match scoring
            if (!string.IsNullOrWhiteSpace(targetCallSign))
            {
                if (string.Equals(call, targetCallSign, StringComparison.OrdinalIgnoreCase))
                    score += 200;
                else if (call.StartsWith(targetCallSign, StringComparison.OrdinalIgnoreCase))
                    score += 120;
                else if (string.Equals(System.Text.RegularExpressions.Regex.Replace(call, @"DT\d*$", ""), targetCallSign, StringComparison.OrdinalIgnoreCase))
                    score += 150;
            }

            // 5. Name match scoring
            string cleanCandidateName = System.Text.RegularExpressions.Regex.Replace(name, @"\s+HD$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (string.Equals(cleanCandidateName, rawName, StringComparison.OrdinalIgnoreCase))
                score += 100;
            else if (name.Contains(rawName, StringComparison.OrdinalIgnoreCase))
                score += 40;

            // 6. HDTV preference over SDTV
            if (string.Equals(c.VideoQuality?.VideoType, "HDTV", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("HD", StringComparison.OrdinalIgnoreCase) ||
                call.EndsWith("HDP", StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }

            // 7. East vs. Pacific Timezone Feed Alignment
            bool isWestFeed = call.EndsWith("P", StringComparison.OrdinalIgnoreCase) ||
                              call.EndsWith("HDP", StringComparison.OrdinalIgnoreCase) ||
                              call.EndsWith("HDW", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Pacific", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("West", StringComparison.OrdinalIgnoreCase);

            if (preferPacific)
            {
                score += isWestFeed ? 50 : -20;
            }
            else
            {
                score += isWestFeed ? -60 : 40;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
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
        public List<string>? BcastLangs { get; set; }
        public TmsVideoQuality? VideoQuality { get; set; }
    }

    private sealed class TmsVideoQuality
    {
        public string? SignalType { get; set; }
        public string? VideoType { get; set; }
        public string? TruResolution { get; set; }
    }
}