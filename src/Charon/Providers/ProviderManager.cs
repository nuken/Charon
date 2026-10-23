using System.IO;
using System.Text;
using System.Text.Json;
using Charon.Config;

namespace Charon.Providers;

public class ProviderManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GroupTitle { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string LoginUrl { get; set; } = string.Empty;
    public int TuneScriptDelayMs { get; set; } = 1500;
}

public class ProviderChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ChannelNumber { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class LoadedProvider
{
    public required ProviderManifest Manifest { get; init; }
    public required List<ProviderChannel> Channels { get; init; }
    public required string FolderPath { get; init; }
    public string? TuneJs { get; init; }
    public string? StyleCss { get; init; }
}

public class ProviderManager
{
    private readonly string _providersRoot;
    private readonly List<LoadedProvider> _providers = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public IReadOnlyList<LoadedProvider> Providers => _providers;
    public string ProvidersRoot => _providersRoot;

    public ProviderManager()
    {
        _providersRoot = Path.Combine(AppContext.BaseDirectory, "Providers");
        ReloadProviders();
    }

    public void ReloadProviders()
    {
        _providers.Clear();
        Directory.CreateDirectory(_providersRoot);

        // Ensure both default provider directories exist out of the box
        string defaultYttvDir = Path.Combine(_providersRoot, "YouTubeTV");
        string defaultSpectrumDir = Path.Combine(_providersRoot, "Spectrum");
        Directory.CreateDirectory(defaultYttvDir);
        Directory.CreateDirectory(defaultSpectrumDir);

        // If someone placed channels.json directly in Providers\ root instead of Providers\YouTubeTV\, move/copy it into Providers\YouTubeTV\
        string rootChannelsFile = Path.Combine(_providersRoot, "channels.json");
        if (File.Exists(rootChannelsFile))
        {
            string targetYttvChannels = Path.Combine(defaultYttvDir, "channels.json");
            if (!File.Exists(targetYttvChannels))
                File.Copy(rootChannelsFile, targetYttvChannels);
        }

        // Scan all subdirectories in Providers\
        foreach (var dir in Directory.GetDirectories(_providersRoot))
        {
            LoadOrRepairProviderDirectory(dir);
        }
    }

    private void LoadOrRepairProviderDirectory(string dir)
    {
        string folderName = Path.GetFileName(dir);
        bool isYttv = folderName.Equals("YouTubeTV", StringComparison.OrdinalIgnoreCase);
        bool isSpectrum = folderName.Equals("Spectrum", StringComparison.OrdinalIgnoreCase);

        string manifestPath = Path.Combine(dir, "provider.json");
        string altManifestPath = Path.Combine(dir, "manifest.json");
        string channelsPath = Path.Combine(dir, "channels.json");
        string tuneJsPath = Path.Combine(dir, "tune.js");
        string styleCssPath = Path.Combine(dir, "style.css");

        string defaultName = isYttv ? "YouTube TV" : isSpectrum ? "Spectrum TV" : folderName;
        string defaultLoginUrl = isSpectrum ? "https://watch.spectrum.net/guide" : "https://tv.youtube.com";
        int defaultDelayMs = isSpectrum ? 2000 : 1500;

        // Support manifest.json if present, or auto-create provider.json if missing!
        if (!File.Exists(manifestPath) && File.Exists(altManifestPath))
        {
            File.Copy(altManifestPath, manifestPath);
        }
        else if (!File.Exists(manifestPath))
        {
            var autoManifest = new ProviderManifest
            {
                Id = folderName.ToLowerInvariant(),
                Name = defaultName,
                GroupTitle = defaultName,
                Enabled = true,
                LoginUrl = defaultLoginUrl,
                TuneScriptDelayMs = defaultDelayMs
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(autoManifest, JsonOptions), new UTF8Encoding(false));
        }

        if (!File.Exists(channelsPath))
        {
            File.WriteAllText(channelsPath, "[]", new UTF8Encoding(false));
        }
		
		// Auto-create/update YouTube TV's tune.js and style.css
        if (isYttv)
        {
            const string yttvTuneJs = """
                (function() {
                  if (location.pathname.startsWith('/live')) return;

                  const queryAllDeep = (selector, root = document) => {
                    const results = Array.from(root.querySelectorAll(selector));
                    const all = root.querySelectorAll('*');
                    for (const el of all) {
                      if (el.shadowRoot) {
                        results.push(...queryAllDeep(selector, el.shadowRoot));
                      }
                    }
                    return results;
                  };

                  // 1. Control the YouTube HTML5 player API
                  const players = queryAllDeep('#movie_player, .html5-video-player');
                  for (const player of players) {
                    try {
                      if (typeof player.unMute === 'function') player.unMute();
                      if (typeof player.setVolume === 'function') player.setVolume(100);
                      if (typeof player.playVideo === 'function' && typeof player.getPlayerState === 'function' && player.getPlayerState() !== 1) {
                        player.playVideo();
                      }
                      if (typeof player.hideControls === 'function') player.hideControls();
                      player.classList.add('ytp-autohide');
                      if (typeof player.setSize === 'function') {
                        player.setSize(window.innerWidth, window.innerHeight);
                      }
                    } catch (e) {}
                  }

                  // 2. Find the active live video element (piercing Shadow DOMs)
                  const videos = queryAllDeep('video');
                  const v = videos.find(x => x.videoWidth > 0 || !x.closest('ytu-epg-row, ytu-guide, .tenx-thumb')) || videos[0];
                  if (!v) return;

                  v.muted = false;
                  v.volume = 1.0;
                  if (v.paused) v.play().catch(() => {});

                  // 3. Force the video element itself to fill 1280x720 at (0,0)
                  const fullBoxCss = [
                    ['position', 'fixed'],
                    ['inset', '0px'],
                    ['top', '0px'],
                    ['left', '0px'],
                    ['right', '0px'],
                    ['bottom', '0px'],
                    ['margin', '0px'],
                    ['margin-left', '0px'],
                    ['padding', '0px'],
                    ['padding-left', '0px'],
                    ['width', '100vw'],
                    ['height', '100vh'],
                    ['min-width', '100vw'],
                    ['min-height', '100vh'],
                    ['max-width', '100vw'],
                    ['max-height', '100vh'],
                    ['transform', 'none'],
                    ['translate', 'none'],
                    ['contain', 'none'],
                    ['will-change', 'auto']
                  ];

                  for (const [prop, val] of fullBoxCss) {
                    v.style.setProperty(prop, val, 'important');
                  }
                  v.style.setProperty('object-fit', 'fill', 'important');
                  v.style.setProperty('z-index', '2147483647', 'important');
                  v.style.setProperty('background', '#000', 'important');

                  // 4. Walk ALL ancestors (including across ShadowRoot boundaries!) to strip any left offset or containing block trap
                  let curr = v.parentElement || (v.getRootNode && v.getRootNode().host);
                  while (curr && curr !== document && curr !== document.documentElement) {
                    if (curr.style) {
                      for (const [prop, val] of fullBoxCss) {
                        curr.style.setProperty(prop, val, 'important');
                      }
                      // Also zero out any CSS custom variables YouTube TV uses for the left guide drawer width!
                      curr.style.setProperty('--ytu-guide-width', '0px', 'important');
                      curr.style.setProperty('--app-drawer-width', '0px', 'important');
                      curr.style.setProperty('--ytu-watch-sidebar-width', '0px', 'important');
                    }
                    if (curr.parentElement) {
                      curr = curr.parentElement;
                    } else {
                      const root = curr.getRootNode && curr.getRootNode();
                      curr = root && root.host ? root.host : null;
                    }
                  }

                  // 5. Hide any sibling sidebars/drawers inside all Shadow Roots
                  const sidebars = queryAllDeep('tp-yt-app-drawer, ytu-guide, ytu-mini-guide, ytu-nav-bar, ytu-header-bar, ytu-player-bar, ytu-player-controls, .ytp-chrome-bottom, .ytp-chrome-top');
                  for (const sb of sidebars) {
                    if (sb.style) {
                      sb.style.setProperty('display', 'none', 'important');
                      sb.style.setProperty('width', '0px', 'important');
                    }
                  }
                })();
                """;
            File.WriteAllText(tuneJsPath, yttvTuneJs, new UTF8Encoding(false));

            const string yttvStyleCss = """
                :root, html, body, ytu-app {
                  --ytu-guide-width: 0px !important;
                  --app-drawer-width: 0px !important;
                  --ytu-watch-sidebar-width: 0px !important;
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, yttvStyleCss, new UTF8Encoding(false));
        }

        // Auto-create Spectrum's tune.js and style.css if missing
        if (isSpectrum)
        {
            const string spectrumTuneJs = """
                (function() {
                  const queryAllDeep = (selector, root = document) => {
                    const results = Array.from(root.querySelectorAll(selector));
                    const all = root.querySelectorAll('*');
                    for (const el of all) {
                      if (el.shadowRoot) {
                        results.push(...queryAllDeep(selector, el.shadowRoot));
                      }
                    }
                    return results;
                  };

                  const videos = queryAllDeep('video');
                  const v = videos.find(x => x.readyState > 0 || x.src || x.srcObject) || videos[0];
                  if (!v) return;

                  v.muted = false;
                  v.volume = 1.0;
                  if (v.paused) {
                    v.play().catch(() => {});
                  }

                  v.style.setProperty('position', 'fixed', 'important');
                  v.style.setProperty('inset', '0px', 'important');
                  v.style.setProperty('top', '0px', 'important');
                  v.style.setProperty('left', '0px', 'important');
                  v.style.setProperty('width', '100vw', 'important');
                  v.style.setProperty('height', '100vh', 'important');
                  v.style.setProperty('object-fit', 'fill', 'important');
                  v.style.setProperty('z-index', '2147483647', 'important');
                  v.style.setProperty('opacity', '1', 'important');
                  v.style.setProperty('visibility', 'visible', 'important');

                  // Walk up the ancestor chain from <video> to <body>:
                  // Keep every direct ancestor visible, and hide EVERY sibling of those ancestors (all overlays, controls, headers, spinners)!
                  const ancestors = new Set();
                  let curr = v;
                  while (curr) {
                    ancestors.add(curr);
                    curr = curr.parentElement || (curr.getRootNode && curr.getRootNode().host);
                  }

                  for (const anc of ancestors) {
                    if (anc !== v && anc.style) {
                      anc.style.setProperty('transform', 'none', 'important');
                      anc.style.setProperty('contain', 'none', 'important');
                      anc.style.setProperty('z-index', '2147483647', 'important');
                    }
                    const parent = anc.parentElement || (anc.getRootNode && anc.getRootNode());
                    if (parent && parent.children) {
                      for (const sibling of parent.children) {
                        if (!ancestors.has(sibling) && sibling.tagName !== 'STYLE' && sibling.tagName !== 'SCRIPT' && sibling.style) {
                          sibling.style.setProperty('opacity', '0', 'important');
                          sibling.style.setProperty('visibility', 'hidden', 'important');
                          sibling.style.setProperty('pointer-events', 'none', 'important');
                        }
                      }
                    }
                  }
                })();
                """;
            File.WriteAllText(tuneJsPath, spectrumTuneJs, new UTF8Encoding(false));
        }

        ProviderManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<ProviderManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch { }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
        {
            manifest = new ProviderManifest
            {
                Id = folderName.ToLowerInvariant(),
                Name = defaultName,
                GroupTitle = defaultName,
                Enabled = true,
                LoginUrl = defaultLoginUrl,
                TuneScriptDelayMs = defaultDelayMs
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        }

        List<ProviderChannel> channels = [];
        try
        {
            channels = JsonSerializer.Deserialize<List<ProviderChannel>>(File.ReadAllText(channelsPath), JsonOptions) ?? [];
        }
        catch { }

        _providers.Add(new LoadedProvider
        {
            Manifest = manifest,
            Channels = channels,
            FolderPath = dir,
            TuneJs = File.Exists(tuneJsPath) ? File.ReadAllText(tuneJsPath) : null,
            StyleCss = File.Exists(styleCssPath) ? File.ReadAllText(styleCssPath) : null
        });
    }

    public static List<ProviderChannel> ParseChannelsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var channels = JsonSerializer.Deserialize<List<ProviderChannel>>(json, JsonOptions);
        return channels ?? throw new InvalidOperationException("JSON did not contain a valid channel array.");
    }

    public static string FormatChannelsJson(IEnumerable<ProviderChannel> channels)
    {
        return JsonSerializer.Serialize(channels, JsonOptions);
    }

    public string SaveProviderChannels(LoadedProvider provider, List<ProviderChannel> updatedChannels)
    {
        Directory.CreateDirectory(provider.FolderPath);
        string channelsPath = Path.Combine(provider.FolderPath, "channels.json");
        string json = FormatChannelsJson(updatedChannels);

        // 1. Save to the active runtime provider folder (e.g., C:\Charon\Providers\YouTubeTV\channels.json)
        File.WriteAllText(channelsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 2. Also mirror to the source repo folder if present on this machine
        try
        {
            string folderName = Path.GetFileName(provider.FolderPath);
            string devRepoChannelsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Charon", "Providers", folderName, "channels.json");

            string? devDir = Path.GetDirectoryName(devRepoChannelsPath);
            if (devDir is not null && Directory.Exists(devDir) &&
                !string.Equals(Path.GetFullPath(channelsPath), Path.GetFullPath(devRepoChannelsPath), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(devRepoChannelsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch { }

        provider.Channels.Clear();
        provider.Channels.AddRange(updatedChannels);

        return channelsPath;
    }

    public (LoadedProvider? Provider, ProviderChannel? Channel) FindChannel(string providerId, string channelId)
    {
        var provider = _providers.FirstOrDefault(p =>
            p.Manifest.Enabled && p.Manifest.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));

        var channel = provider?.Channels.FirstOrDefault(c =>
            c.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase));

        return (provider, channel);
    }

    public string GenerateM3uPlaylist(CharonConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        string baseUrl = $"http://{config.ResolvedLanIp}:{config.ResolvedPort}";

        foreach (var provider in _providers.Where(p => p.Manifest.Enabled))
        {
            string group = string.IsNullOrWhiteSpace(provider.Manifest.GroupTitle)
                ? provider.Manifest.Name
                : provider.Manifest.GroupTitle;

            foreach (var ch in provider.Channels)
            {
                string uniqueChannelId = $"{provider.Manifest.Id}-{ch.Id}";
                var extInf = new StringBuilder($"#EXTINF:-1 channel-id=\"{uniqueChannelId}\"");

                if (!string.IsNullOrWhiteSpace(ch.ChannelNumber))
                    extInf.Append($" channel-number=\"{ch.ChannelNumber}\"");

                if (!string.IsNullOrWhiteSpace(ch.StationId))
                {
                    extInf.Append($" tvg-id=\"{ch.StationId}\"");
                    extInf.Append($" tvc-guide-stationid=\"{ch.StationId}\"");
                }

                extInf.Append($" tvg-name=\"{ch.Name}\"");
                extInf.Append($" group-title=\"{group}\",{ch.Name}");

                sb.AppendLine(extInf.ToString());
                sb.AppendLine($"{baseUrl}/stream/{provider.Manifest.Id}/{ch.Id}");
            }
        }

        return sb.ToString();
    }
}