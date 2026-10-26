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
        bool isXfinity = provider.Manifest.Id.Contains("xfinity", StringComparison.OrdinalIgnoreCase);
        string targetUrl = isSpectrum
            ? "https://watch.spectrum.net/guide"
            : isXfinity
            ? "https://www.xfinity.com/stream/listings"
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
            List<TmsStation>? results = null;

            // 1. For Xfinity, the URL callSign (e.g., HBOSG, SHO2e, ESPNU) is infinitely more accurate than the generic display name.
            var xfMatch = System.Text.RegularExpressions.Regex.Match(ch.Url ?? "", @"/stream/live/([^/]+)/\d+/");
            string? urlCallSign = xfMatch.Success ? Uri.UnescapeDataString(xfMatch.Groups[1].Value) : null;

            if (!string.IsNullOrWhiteSpace(urlCallSign))
            {
                results = await QueryChannelsDvrTmsAsync(serverUrl, urlCallSign);

                if (results is null || results.Count == 0)
                {
                    // Strip trailing HD/D (e.g. "CNNHD" -> "CNN", "HGTVD" -> "HGTV")
                    string cleanCs = System.Text.RegularExpressions.Regex.Replace(urlCallSign, @"(?:HD|DT\d*|D)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (cleanCs.Length >= 3 && !string.Equals(cleanCs, urlCallSign, StringComparison.OrdinalIgnoreCase))
                    {
                        results = await QueryChannelsDvrTmsAsync(serverUrl, cleanCs);
                    }
                }
            }

            // 2. Fallback to the display name if callSign lookups failed (or if it is YouTube TV / Spectrum)
            if (results is null || results.Count == 0)
            {
                results = await QueryChannelsDvrTmsAsync(serverUrl, rawName);

                if (results is null || results.Count == 0)
                {
                    string stripped = System.Text.RegularExpressions.Regex.Replace(rawName, @"\s+\d+$", "");
                    if (!string.Equals(stripped, rawName, StringComparison.OrdinalIgnoreCase))
                    {
                        results = await QueryChannelsDvrTmsAsync(serverUrl, stripped);
                    }
                }

                if ((results is null || results.Count == 0) && rawName.Contains(' '))
                {
                    string firstWord = rawName.Split(' ')[0];
                    if (firstWord.Length >= 3)
                    {
                        results = await QueryChannelsDvrTmsAsync(serverUrl, firstWord);
                    }
                }
            }

            if (results is { Count: > 0 })
            {
                var match = results.FirstOrDefault(r =>
                    (!string.IsNullOrWhiteSpace(r.CallSign) &&
                     (string.Equals(System.Text.RegularExpressions.Regex.Replace(r.CallSign, @"DT\d*$", ""), rawName, StringComparison.OrdinalIgnoreCase) ||
                      r.CallSign.StartsWith(rawName, StringComparison.OrdinalIgnoreCase) ||
                      (!string.IsNullOrWhiteSpace(urlCallSign) && r.CallSign.StartsWith(urlCallSign, StringComparison.OrdinalIgnoreCase)))) ||
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