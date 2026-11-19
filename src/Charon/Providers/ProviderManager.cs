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
	public bool IsHidden { get; set; } = false;
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
		string defaultXfinityDir = Path.Combine(_providersRoot, "Xfinity");
        string defaultSlingDir = Path.Combine(_providersRoot, "Sling");
		string defaultPhiloDir = Path.Combine(_providersRoot, "Philo");
		string defaultWbdDir = Path.Combine(_providersRoot, "WBD");
		string defaultCnnDir = Path.Combine(_providersRoot, "CNN");
		string defaultNbcDir = Path.Combine(_providersRoot, "NBC");
		string defaultHistoryDir = Path.Combine(_providersRoot, "History");
        string defaultAetvDir = Path.Combine(_providersRoot, "AETV");
        string defaultLifetimeDir = Path.Combine(_providersRoot, "Lifetime");
		string defaultUsaDir = Path.Combine(_providersRoot, "USA");
		string defaultHuluDir = Path.Combine(_providersRoot, "Hulu");
		string defaultFuboDir = Path.Combine(_providersRoot, "Fubo");
		string defaultAbcDir = Path.Combine(_providersRoot, "ABC");
		string defaultCbsDir = Path.Combine(_providersRoot, "CBS");
		string defaultFoxDir = Path.Combine(_providersRoot, "FOX");
        		
        Directory.CreateDirectory(defaultYttvDir);
        Directory.CreateDirectory(defaultSpectrumDir);
		Directory.CreateDirectory(defaultXfinityDir);
        Directory.CreateDirectory(defaultSlingDir);
		Directory.CreateDirectory(defaultPhiloDir);
		Directory.CreateDirectory(defaultWbdDir);
		Directory.CreateDirectory(defaultCnnDir);
		Directory.CreateDirectory(defaultNbcDir);
		Directory.CreateDirectory(defaultHistoryDir);
        Directory.CreateDirectory(defaultAetvDir);
        Directory.CreateDirectory(defaultLifetimeDir);
		Directory.CreateDirectory(defaultUsaDir);
		Directory.CreateDirectory(defaultHuluDir);
		Directory.CreateDirectory(defaultFuboDir);
		Directory.CreateDirectory(defaultAbcDir);
		Directory.CreateDirectory(defaultCbsDir);
		Directory.CreateDirectory(defaultFoxDir);
		
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
        bool isXfinity = folderName.Equals("Xfinity", StringComparison.OrdinalIgnoreCase);
        bool isSling = folderName.Equals("Sling", StringComparison.OrdinalIgnoreCase);
		bool isPhilo = folderName.Equals("Philo", StringComparison.OrdinalIgnoreCase);
        bool isWbd = folderName.Equals("WBD", StringComparison.OrdinalIgnoreCase);
		bool isCnn = folderName.Equals("CNN", StringComparison.OrdinalIgnoreCase);
		bool isNbc = folderName.Equals("NBC", StringComparison.OrdinalIgnoreCase);
		bool isHistory = folderName.Equals("History", StringComparison.OrdinalIgnoreCase);
        bool isAetv = folderName.Equals("AETV", StringComparison.OrdinalIgnoreCase);
        bool isLifetime = folderName.Equals("Lifetime", StringComparison.OrdinalIgnoreCase);
        bool isAeFamily = isHistory || isAetv || isLifetime;
		bool isUsa = folderName.Equals("USA", StringComparison.OrdinalIgnoreCase);
		bool isHulu = folderName.Equals("Hulu", StringComparison.OrdinalIgnoreCase);
		bool isFubo = folderName.Equals("Fubo", StringComparison.OrdinalIgnoreCase);
		bool isAbc = folderName.Equals("ABC", StringComparison.OrdinalIgnoreCase);
		bool isCbs = folderName.Equals("CBS", StringComparison.OrdinalIgnoreCase);
		bool isFox = folderName.Equals("FOX", StringComparison.OrdinalIgnoreCase);

        string manifestPath = Path.Combine(dir, "provider.json");
        string altManifestPath = Path.Combine(dir, "manifest.json");
        string channelsPath = Path.Combine(dir, "channels.json");
        string tuneJsPath = Path.Combine(dir, "tune.js");
        string styleCssPath = Path.Combine(dir, "style.css");

        string defaultName = isYttv ? "YouTube TV" 
		    : isSpectrum ? "Spectrum TV" 
			: isXfinity ? "Xfinity Stream" 
			: isSling ? "Sling TV" 
			: isPhilo ? "Philo" 
			: isWbd ? "WB-Discovery (TVE)" 
			: isCnn ? "CNN (TVE)" 
			: isNbc ? "NBCUniversal (TVE)"
			: isHistory ? "History (TVE)"
            : isAetv ? "A&E (TVE)"
            : isLifetime ? "Lifetime (TVE)"
			: isUsa ? "USA Network (TVE)"
			: isHulu ? "Hulu + Live TV"
			: isFubo ? "Fubo"
			: isAbc ? "ABC / FX / NatGeo (TVE)"
			: isCbs ? "CBS (TVE)"
			: isFox ? "FOX One (TVE)"
			: folderName;
			
        string defaultLoginUrl = isSpectrum
            ? "https://watch.spectrum.net/guide"
            : isXfinity
            ? "https://www.xfinity.com/stream"
            : isSling
            ? "https://watch.sling.com"
			: isPhilo
            ? "https://www.philo.com/player"
            : isWbd
            ? "https://watch.hgtv.com/channel/hgtv"
			: isCnn
            ? "https://www.cnn.com/live"
			: isNbc 
			? "https://www.nbc.com/live"
			: isHistory
            ? "https://play.history.com/live"
            : isAetv
            ? "https://play.aetv.com/live"
            : isLifetime
            ? "https://play.mylifetime.com/live"
			: isUsa
            ? "https://www.usanetwork.com/live"
			: isHulu
            ? "https://www.hulu.com/live"
			: isFubo
            ? "https://www.fubo.tv/p/home"
			: isAbc
            ? "https://abc.com/watch-live"
			: isCbs
            ? "https://www.cbs.com/live-tv/stream/tveverywhere/"
			: isFox
            ? "https://www.fox.com/live/channels"
            : "https://tv.youtube.com";
         int defaultDelayMs = isSpectrum || isSling || isPhilo || isHulu || isFubo ? 2000 : 1500;

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
            if (isWbd)
            {
                const string wbdDefaultChannels = """
                    [
                      {
                        "id": "hgtv",
                        "name": "HGTV",
                        "channelNumber": "229",
                        "stationId": "49788",
                        "url": "https://watch.hgtv.com/channel/hgtv",
                        "isHidden": true
                      },
                      {
                        "id": "food-network",
                        "name": "Food Network",
                        "channelNumber": "231",
                        "stationId": "50747",
                        "url": "https://watch.hgtv.com/channel/food-network",
                        "isHidden": true
                      },
                      {
                        "id": "tlc",
                        "name": "TLC",
                        "channelNumber": "280",
                        "stationId": "57391",
                        "url": "https://watch.hgtv.com/channel/tlc",
                        "isHidden": true
                      },
                      {
                        "id": "investigation-discovery",
                        "name": "Investigation Discovery",
                        "channelNumber": "285",
                        "stationId": "65342",
                        "url": "https://watch.hgtv.com/channel/investigation-discovery",
                        "isHidden": true
                      },
                      {
                        "id": "discovery",
                        "name": "Discovery Channel",
                        "channelNumber": "278",
                        "stationId": "56905",
                        "url": "https://watch.hgtv.com/channel/discovery",
                        "isHidden": true
                      },
                      {
                        "id": "cooking-channel",
                        "name": "Cooking Channel",
                        "channelNumber": "232",
                        "stationId": "68065",
                        "url": "https://watch.hgtv.com/channel/cooking-channel",
                        "isHidden": true
                      },
                      {
                        "id": "travel-channel",
                        "name": "Travel Channel",
                        "channelNumber": "277",
                        "stationId": "59303",
                        "url": "https://watch.hgtv.com/channel/travel-channel",
                        "isHidden": true
                      },
                      {
                        "id": "magnolia-network",
                        "name": "Magnolia Network",
                        "channelNumber": "230",
                        "stationId": "67454",
                        "url": "https://watch.hgtv.com/channel/magnolia-network",
                        "isHidden": true
                      },
                      {
                        "id": "own",
                        "name": "OWN",
                        "channelNumber": "279",
                        "stationId": "70388",
                        "url": "https://watch.hgtv.com/channel/own",
                        "isHidden": true
                      },
                      {
                        "id": "animal-planet",
                        "name": "Animal Planet",
                        "channelNumber": "282",
                        "stationId": "57394",
                        "url": "https://watch.hgtv.com/channel/animal-planet",
                        "isHidden": true
                      },
                      {
                        "id": "discovery-life",
                        "name": "Discovery Life",
                        "channelNumber": "261",
                        "stationId": "92204",
                        "url": "https://watch.hgtv.com/channel/discovery-life",
                        "isHidden": true
                      },
                      {
                        "id": "science",
                        "name": "Science Channel",
                        "channelNumber": "284",
                        "stationId": "57390",
                        "url": "https://watch.hgtv.com/channel/science",
                        "isHidden": true
                      },
                      {
                        "id": "ahc",
                        "name": "American Heroes Channel",
                        "channelNumber": "287",
                        "stationId": "78808",
                        "url": "https://watch.hgtv.com/channel/ahc",
                        "isHidden": true
                      },
                      {
                        "id": "motortrend",
                        "name": "MotorTrend",
                        "channelNumber": "281",
                        "stationId": "31046",
                        "url": "https://watch.hgtv.com/channel/motortrend",
                        "isHidden": true
                      },
                      {
                        "id": "destination-america",
                        "name": "Destination America",
                        "channelNumber": "286",
                        "stationId": "60468",
                        "url": "https://watch.hgtv.com/channel/destination-america",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, wbdDefaultChannels, new UTF8Encoding(false));
            }
			else if (isCnn)
            {
                const string cnnDefaultChannels = """
                    [
                      {
                        "id": "cnn",
                        "name": "CNN",
                        "channelNumber": "200",
                        "stationId": "58646",
                        "url": "https://www.cnn.com/videos/cnn",
                        "isHidden": true
                      },
                      {
                        "id": "cnni",
                        "name": "CNN International",
                        "channelNumber": "201",
                        "stationId": "10146",
                        "url": "https://www.cnn.com/videos/cnn-i",
                        "isHidden": true
                      },
                      {
                        "id": "hln",
                        "name": "HLN",
                        "channelNumber": "202",
                        "stationId": "64549",
                        "url": "https://www.cnn.com/videos/hln",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, cnnDefaultChannels, new UTF8Encoding(false));
            }
			else if (isHistory)
            {
                const string historyDefaultChannels = """
                    [
                      {
                        "id": "history",
                        "name": "History",
                        "channelNumber": "240",
                        "stationId": "57708",
                        "url": "https://play.history.com/live",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, historyDefaultChannels, new UTF8Encoding(false));
            }
            else if (isAetv)
            {
                const string aetvDefaultChannels = """
                    [
                      {
                        "id": "aetv",
                        "name": "A&E",
                        "channelNumber": "241",
                        "stationId": "51529",
                        "url": "https://play.aetv.com/live",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, aetvDefaultChannels, new UTF8Encoding(false));
            }
            else if (isLifetime)
            {
                const string lifetimeDefaultChannels = """
                    [
                      {
                        "id": "lifetime",
                        "name": "Lifetime",
                        "channelNumber": "242",
                        "stationId": "60150",
                        "url": "https://play.mylifetime.com/live",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, lifetimeDefaultChannels, new UTF8Encoding(false));
            }
			else if (isAbc)
            {
                const string abcDefaultChannels = """
                    [
                      {
                        "id": "abc-news-live",
                        "name": "ABC News Live",
                        "channelNumber": "251",
                        "stationId": "113380",
                        "url": "https://abc.com/watch-live/784b058c-3aee-4ec9-a6d3-754ba6e4dbac",
                        "isHidden": true
                      },
                      {
                        "id": "freeform-east",
                        "name": "Freeform East",
                        "channelNumber": "252",
                        "stationId": "59615",
                        "url": "https://abc.com/watch-live/885c669e-fa9a-4039-b42e-6c85c90cc86d",
                        "isHidden": true
                      },
                      {
                        "id": "freeform-west",
                        "name": "Freeform West",
                        "channelNumber": "253",
                        "stationId": "63324",
                        "url": "https://abc.com/watch-live/3507c750-e86a-4c0f-8ff4-dd23c4859009",
                        "isHidden": true
                      },
                      {
                        "id": "fx-east",
                        "name": "FX East",
                        "channelNumber": "254",
                        "stationId": "58574",
                        "url": "https://abc.com/watch-live/93256af4-5e80-4558-aa2e-2bdfffa119a0",
                        "isHidden": true
                      },
                      {
                        "id": "fx-west",
                        "name": "FX West",
                        "channelNumber": "255",
                        "stationId": "59814",
                        "url": "https://abc.com/watch-live/2cee3401-f63b-42d0-b32e-962fef610b9e",
                        "isHidden": true
                      },
                      {
                        "id": "fxm",
                        "name": "FXM",
                        "channelNumber": "256",
                        "stationId": "70253",
                        "url": "https://abc.com/watch-live/d298ab7e-c6b1-4efa-ac6e-a52dceed92ee",
                        "isHidden": true
                      },
                      {
                        "id": "fxx-east",
                        "name": "FXX East",
                        "channelNumber": "257",
                        "stationId": "66379",
                        "url": "https://abc.com/watch-live/49f4a471-8d36-4728-8457-ea65cbbc84ea",
                        "isHidden": true
                      },
                      {
                        "id": "fxx-west",
                        "name": "FXX West",
                        "channelNumber": "258",
                        "stationId": "82571",
                        "url": "https://abc.com/watch-live/e4c83395-62ed-4a49-829a-c55ab3c33e7d",
                        "isHidden": true
                      },
                      {
                        "id": "nat-geo-east",
                        "name": "Nat Geo East",
                        "channelNumber": "259",
                        "stationId": "49438",
                        "url": "https://abc.com/watch-live/0826a9a3-3384-4bb5-8841-91f01cb0e3a7",
                        "isHidden": true
                      },
                      {
                        "id": "nat-geo-west",
                        "name": "Nat Geo West",
                        "channelNumber": "260",
                        "stationId": "71601",
                        "url": "https://abc.com/watch-live/91456580-f32f-417c-8e1a-9f82640832a7",
                        "isHidden": true
                      },
                      {
                        "id": "nat-geo-wild",
                        "name": "Nat Geo Wild",
                        "channelNumber": "261",
                        "stationId": "67331",
                        "url": "https://abc.com/watch-live/239b9590-583f-4955-a499-22e9eefff9cf",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, abcDefaultChannels, new UTF8Encoding(false));
            }
			else if (isCbs)
            {
                const string cbsDefaultChannels = """
                    [
                      {
                        "id": "cbs-news-247",
                        "name": "CBS News 24/7",
                        "channelNumber": "270",
                        "stationId": "104846",
                        "url": "https://www.cbs.com/live-tv/stream/cbs-news/",
                        "isHidden": true
                      },
                      {
                        "id": "cbs-sports-hq",
                        "name": "CBS Sports HQ",
                        "channelNumber": "271",
                        "stationId": "108919",
                        "url": "https://www.cbs.com/live-tv/stream/sports/",
                        "isHidden": true
                      },
                      {
                        "id": "cbs-sports-golazo",
                        "name": "CBS Sports Golazo Network",
                        "channelNumber": "272",
                        "stationId": "133691",
                        "url": "https://www.cbs.com/live-tv/stream/golazo/",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, cbsDefaultChannels, new UTF8Encoding(false));
            }
			else if (isFox)
            {
                const string foxDefaultChannels = """
                    [
                      {
                        "id": "fox-fnc",
                        "name": "FOX News",
                        "channelNumber": "281",
                        "stationId": "60179",
                        "url": "https://www.fox.com/live/channels#charon_channel=FNC",
                        "isHidden": true
                      },
                      {
                        "id": "fox-fs1",
                        "name": "FS1",
                        "channelNumber": "282",
                        "stationId": "82547",
                        "url": "https://www.fox.com/live/channels#charon_channel=FS1",
                        "isHidden": true
                      },
                      {
                        "id": "fox-fs2",
                        "name": "FS2",
                        "channelNumber": "283",
                        "stationId": "59305",
                        "url": "https://www.fox.com/live/channels#charon_channel=FS2",
                        "isHidden": true
                      },
                      {
                        "id": "fox-btn",
                        "name": "Big Ten Network",
                        "channelNumber": "284",
                        "stationId": "58321",
                        "url": "https://www.fox.com/live/channels#charon_channel=BTN",
                        "isHidden": true
                      },
                      {
                        "id": "fox-fwx",
                        "name": "FOX Weather",
                        "channelNumber": "285",
                        "stationId": "121307",
                        "url": "https://www.fox.com/live/channels#charon_channel=FWX",
                        "isHidden": true
                      },
                      {
                        "id": "fox-fbn",
                        "name": "FOX Business",
                        "channelNumber": "286",
                        "stationId": "58718",
                        "url": "https://www.fox.com/live/channels#charon_channel=FBN",
                        "isHidden": true
                      }
                    ]
                    """;
                File.WriteAllText(channelsPath, foxDefaultChannels, new UTF8Encoding(false));
            }
            else
            {
                File.WriteAllText(channelsPath, "[]", new UTF8Encoding(false));
            }
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
                      if (typeof player.setPlaybackQualityRange === 'function') {
                        player.setPlaybackQualityRange('hd720', 'hd1080');
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
        
		const string spectrumStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                *:has(video) {
                  transform: none !important;
                  filter: none !important;
                  perspective: none !important;
                  contain: none !important;
                  isolation: auto !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                }
                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  object-fit: fill !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                  pointer-events: none !important;
                }
                body *:not(:has(video)):not(video) {
                  opacity: 0 !important;
                  visibility: hidden !important;
                  pointer-events: none !important;
                }
                header, nav, footer, aside, button, svg,
                .site-header, .player-controls, .control-bar, .mini-guide,
                .vjs-control-bar, .vjs-big-play-button, .vjs-loading-spinner, .vjs-text-track-display,
                [class*="control" i], [class*="overlay" i], [class*="header" i], [class*="guide" i],
                [class*="navbar" i], [class*="toolbar" i], [class*="scrim" i], [class*="gradient" i],
                [class*="banner" i], [class*="modal" i], [class*="tooltip" i], [role="toolbar"], [role="navigation"] {
                  display: none !important;
                  opacity: 0 !important;
                  visibility: hidden !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, spectrumStyleCss, new UTF8Encoding(false));
		}
		
		if (isXfinity)
        {
            const string xfinityStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                tv-player-nav, tv-player-rating, tv-player-mini-controls, tv-player-buffer-indicator,
                tv-player-notification, tv-player-network-alert, tv-player-debug-panel, tv-player-panel-loading,
                tv-player-panel-next-episode, tv-player-panel-caster, tv-overlay-player-info, tv-overlay-player-ooh-info,
                #playerControlPanel, #nextEpisodeControls, #playerLastWatchedPanel, #inner-cc-container {
                  display: none !important;
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                /* Kept in the DOM so tune.js can click Continue */
                tv-player-still-watching {
                  opacity: 0 !important;
                }
                """;
            File.WriteAllText(styleCssPath, xfinityStyleCss, new UTF8Encoding(false));

            const string xfinityTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      const lower = mimeType.toLowerCase();
                      return lower.includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  const dismissPlayerDialogs = () => {
                    try {
                      const errorPanel = document.querySelector('tv-player-panel-error#errorPanel');
                      if (errorPanel && !errorPanel.hidden) {
                        const page = errorPanel.querySelector('tv-page-error-content.iron-selected');
                        const title = page && page.querySelector('#title');
                        if (title && /paused playback/i.test(title.textContent)) {
                          const resume = page.querySelector('tv-button button');
                          if (resume) resume.click();
                        }
                      }
                      const stillWatching = document.getElementById('playerStillWatching');
                      if (stillWatching && getComputedStyle(stillWatching).display !== 'none') {
                        const cont = stillWatching.querySelector('#continueButton button');
                        if (cont) cont.click();
                        else if (typeof stillWatching._onContinue === 'function') stillWatching._onContinue();
                      }
                    } catch {}
                  };
                  dismissPlayerDialogs();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        tv-player-nav, tv-player-rating, tv-player-mini-controls, tv-player-buffer-indicator,
                        tv-player-notification, tv-player-network-alert, tv-player-debug-panel, tv-player-panel-loading,
                        tv-player-panel-next-episode, tv-player-panel-caster, tv-overlay-player-info, tv-overlay-player-ooh-info,
                        #playerControlPanel, #nextEpisodeControls, #playerLastWatchedPanel, #inner-cc-container {
                          display: none !important;
                        }
                        tv-player-still-watching { opacity: 0 !important; }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  const findMainVideo = () => document.querySelector('#viper-player-container video') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    dismissPlayerDialogs();
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPlayerDialogs();
                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;
                    for (const v of allVideos) {
                      if (v.muted) v.muted = false;
                      if (v.volume < 1.0) v.volume = 1.0;
                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        v.play().catch(() => {});
                      }
                    }
                    const readyVideos = allVideos
                      .filter((v) => v.readyState >= 2 && v.videoWidth > 0 && !v.paused)
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight));
                    const activeVideo = readyVideos[0];
                    if (!activeVideo) return;
                    for (const v of allVideos) {
                      if (v !== activeVideo && !v.paused) {
                        v.muted = true;
                        try { v.pause(); } catch {}
                      }
                    }
                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 999999 !important'
                      ].join('; ');
                    }
                  };
                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, xfinityTuneJs, new UTF8Encoding(false));
        }

        if (isSling)
        {
            const string slingStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Sling's metadata/controls overlay, the Bitmovin UI layer, and toast notifications */
                [data-testid="player-controls-container"],
                .player-controls,
                .bmpui-ui-uicontainer,
                .bitmovinplayer-poster,
                .Toastify {
                  display: none !important;
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, slingStyleCss, new UTF8Encoding(false));

            const string slingTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      const lower = mimeType.toLowerCase();
                      return lower.includes('av01');
                    };

                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }

                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }

                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }

                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}
                  
                  // The watch page embeds a hidden same-origin iframe of itself with a second Bitmovin player a few seconds
                  // off from the visible one. Tab capture records every frame's audio, so that player would be heard as a
                  // repeat of each moment (and its extra 1080p decode stalls the main player). Keep it muted and paused, and
                  // run everything below only in the top frame.
                  if (window.top !== window) {
                    if (!window.__charonSubframeSilenced) {
                      window.__charonSubframeSilenced = true;
                      setInterval(() => {
                        for (const v of document.querySelectorAll('video, audio')) {
                          if (!v.muted) v.muted = true;
                          if (!v.paused) { try { v.pause(); } catch {} }
                        }
                      }, 300);
                    }
                    return;
                  }

                  // Sling redirects every fresh page load to "Who's Watching?" (/switch_user_profile) unless
                  // "Don't Ask Again" was chosen. Remember the channel deep link before that redirect replaces it.
                  const watchPathRe = /^\/\d+\/channel\/[0-9a-f]{32}\/watch/i;
                  try {
                    if (watchPathRe.test(location.pathname)) sessionStorage.setItem('__charonSlingTarget', location.pathname);
                  } catch {}

                  // Picks the first profile on "Who's Watching?", which returns Sling to the deep link. If Sling lands
                  // somewhere else instead, reload the deep link once. Also answers "Are you still watching?" prompts
                  // (those selectors are generic; the prompt has not been observed live yet).
                  const dismissPrompts = () => {
                    try {
                      const path = location.pathname;
                      if (path.startsWith('/switch_user_profile')) {
                        const avatar = document.querySelector('[data-testid^="SwitchUserProfilesIcons-0"] .Card-Avatar-Interaction-Container');
                        if (avatar && Date.now() - (window.__charonSlingProfileClickAt || 0) > 3000) {
                          window.__charonSlingProfileClickAt = Date.now();
                          avatar.click();
                        }
                        window.__charonSlingOffSince = 0;
                        return;
                      }

                      if (watchPathRe.test(path)) {
                        window.__charonSlingOffSince = 0;
                      } else {
                        const target = sessionStorage.getItem('__charonSlingTarget');
                        if (target && !sessionStorage.getItem('__charonSlingReturned')) {
                          if (!window.__charonSlingOffSince) window.__charonSlingOffSince = Date.now();
                          else if (Date.now() - window.__charonSlingOffSince > 5000) {
                            sessionStorage.setItem('__charonSlingReturned', '1');
                            location.replace(target);
                            return;
                          }
                        }
                      }

                      const dialogs = document.querySelectorAll('[role="dialog"], [class*="modal" i], [class*="dialog" i], [class*="popup" i]');
                      for (const dlg of dialogs) {
                        if (!/still (?:watching|there)/i.test(dlg.textContent || '')) continue;
                        const btn = Array.from(dlg.querySelectorAll('button, [role="button"], [class*="button" i]'))
                          .find((b) => /continue|keep watching|still (?:here|watching)|^\s*yes\b|resume/i.test(b.textContent || ''));
                        if (btn) btn.click();
                      }
                    } catch {}
                  };
                  dismissPrompts();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        [data-testid="player-controls-container"], .player-controls, .bmpui-ui-uicontainer,
                        .bitmovinplayer-poster, .Toastify {
                          display: none !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  // Sling uses the Bitmovin player; its <video> id starts with "bitmovinplayer-video"
                  const findMainVideo = () => document.querySelector('video[id^="bitmovinplayer-video"]') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  // Called by ChromeTuner before it sends a trusted click to unpause the player.
                  window.__charonIsPaused = () => {
                    dismissPrompts();
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPrompts();

                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;

                    for (const v of allVideos) {
                      if (v.muted) v.muted = false;
                      if (v.volume < 1.0) v.volume = 1.0;
                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        v.play().catch(() => {});
                      }
                    }

                    const readyVideos = allVideos
                      .filter((v) => v.readyState >= 2 && v.videoWidth > 0 && !v.paused)
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight));

                    const activeVideo = readyVideos[0];
                    if (!activeVideo) return;

                    try { sessionStorage.removeItem('__charonSlingReturned'); } catch {}

                    for (const v of allVideos) {
                      if (v !== activeVideo && !v.paused) {
                        v.muted = true;
                        try { v.pause(); } catch {}
                      }
                    }

                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 999999 !important'
                      ].join('; ');
                    }
                  };

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, slingTuneJs, new UTF8Encoding(false));
        }
		
		if (isPhilo)
        {
            const string philoStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Philo's player overlays: title/metadata, playback controls, and the ads overlay.
                   Hidden with CSS only, so tune.js can still reach the JUMP TO LIVE button. */
                [class*="overlaysContainer"] {
                  display: none !important;
                  opacity: 0 !important;
                  pointer-events: none !important;
                }

                /* The video's container is centered with a translate(-50%, -50%) transform, which would make the
                   video's position: fixed relative to it; pin the container to the viewport instead. */
                [class*="videoElementContainer"] {
                  position: fixed !important;
                  transform: none !important;
                  left: 0 !important;
                  top: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                }
                """;
            File.WriteAllText(styleCssPath, philoStyleCss, new UTF8Encoding(false));

            const string philoTuneJs = """
                (function () {
                  try {
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }

                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  if (window.top !== window) return;

                  // Channel deep links (/player/player/channel/<id>) redirect to the airing's /player/player/broadcast/<id> page.
                  // Remember the channel link so the tuner can return to it if Philo navigates away from the player.
                  const channelPathRe = /^\/player\/player\/channel\//;
                  const playerPathRe = /^\/player\/player\//;
                  try {
                    if (channelPathRe.test(location.pathname)) sessionStorage.setItem('__charonPhiloTarget', location.pathname);
                  } catch {}

                  // Answers "Are you still watching?" style prompts (generic selectors; the prompt has not been observed live yet)
                  // and reloads the channel link once if Philo leaves the player page.
                  const dismissPrompts = () => {
                    try {
                      if (playerPathRe.test(location.pathname)) {
                        window.__charonPhiloOffSince = 0;
                      } else {
                        const target = sessionStorage.getItem('__charonPhiloTarget');
                        if (target && !sessionStorage.getItem('__charonPhiloReturned')) {
                          if (!window.__charonPhiloOffSince) window.__charonPhiloOffSince = Date.now();
                          else if (Date.now() - window.__charonPhiloOffSince > 5000) {
                            sessionStorage.setItem('__charonPhiloReturned', '1');
                            location.replace(target);
                            return;
                          }
                        }
                      }

                      const dialogs = document.querySelectorAll('[role="dialog"], [role="alertdialog"], [class*="modal" i], [class*="dialog" i], [class*="popup" i]');
                      for (const dlg of dialogs) {
                        if (!/still (?:watching|there)/i.test(dlg.textContent || '')) continue;
                        const btn = Array.from(dlg.querySelectorAll('button, [role="button"]'))
                          .find((b) => /continue|keep watching|still (?:here|watching)|^\s*yes\b|resume/i.test(b.textContent || ''));
                        if (btn) btn.click();
                      }
                    } catch {}
                  };
                  dismissPrompts();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        [class*="overlaysContainer"] { display: none !important; }
                        [class*="videoElementContainer"] {
                          position: fixed !important; transform: none !important; left: 0 !important; top: 0 !important;
                          width: 100vw !important; height: 100vh !important; margin: 0 !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  // Philo's web player has a single <video id="video">
                  const findMainVideo = () => document.getElementById('video') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  // Called by ChromeTuner before it sends a trusted click to unpause the player.
                  window.__charonIsPaused = () => {
                    dismissPrompts();
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  // Philo starts the current airing from its beginning. The player's controls (and its JUMP TO LIVE button) only
                  // mount while the mouse is moving, so wake them with a synthetic mousemove, then jump once per airing. The
                  // remaining-time label reads "LIVE" when the player is at the live edge.
                  const jumpToLive = () => {
                    const key = location.pathname;
                    if (window.__charonPhiloLiveFor === key) return;

                    const overlay = document.querySelector('[class*="playerOverlayContainer"]');
                    if (!overlay) return;
                    overlay.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 640, clientY: 360, view: window }));

                    const remaining = document.querySelector('[class*="remainingTimeContainer"]');
                    if (remaining && /^\s*live\s*$/i.test(remaining.textContent || '')) {
                      window.__charonPhiloLiveFor = key;
                      return;
                    }

                    const btn = document.querySelector('[class*="jtlButton"]') ||
                      Array.from(document.querySelectorAll('button')).find((b) => /jump to live/i.test(b.textContent || ''));
                    if (btn && Date.now() - (window.__charonPhiloJumpAt || 0) > 2000) {
                      window.__charonPhiloJumpAt = Date.now();
                      btn.click();
                    }
                  };

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPrompts();

                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;

                    for (const v of allVideos) {
                      if (v.muted) v.muted = false;
                      if (v.volume < 1.0) v.volume = 1.0;
                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        v.play().catch(() => {});
                      }
                    }

                    const activeVideo = findMainVideo();
                    if (!activeVideo || activeVideo.readyState < 2 || activeVideo.videoWidth === 0 || activeVideo.paused) return;

                    try { sessionStorage.removeItem('__charonPhiloReturned'); } catch {}
                    jumpToLive();

                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 999999 !important'
                      ].join('; ');
                    }
                  };

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, philoTuneJs, new UTF8Encoding(false));
        }
		
		if (isWbd)
        {
            const string wbdStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                #header, #footer, #portal-group,
                [class*="ControlsWrapper"],
                [class*="TopControlsContainerView"],
                [class*="BottomControlsContainerView"] {
                  opacity: 0 !important;
                }
                """;
            File.WriteAllText(styleCssPath, wbdStyleCss, new UTF8Encoding(false));

            const string wbdTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      const lower = mimeType.toLowerCase();
                      return lower.includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        #header, #footer, #portal-group,
                        [class*="ControlsWrapper"],
                        [class*="TopControlsContainerView"],
                        [class*="BottomControlsContainerView"] {
                          opacity: 0 !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };

                  const findMainVideo = () =>
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();

                    const v = findMainVideo();
                    if (!v) return;

                    if (v.muted) v.muted = false;
                    if (v.volume < 1.0) v.volume = 1.0;

                    if (v.paused) {
                      const playBtn = document.querySelector(
                        '[class*="playerBackground"] button[aria-label*="Play" i], ' +
                        '[class*="ControlsWrapper"] button[aria-label*="Play" i], ' +
                        'button[data-testid*="play" i]'
                      );
                      if (playBtn) playBtn.click();
                      v.play().catch(() => {});
                      return;
                    }

                    // Click WBD's top-right expand toggle once (matches IconExpandS path M20 25...)
                    if (!window.__charonWbdExpanded) {
                      const topBtn = document.querySelector('[class*="TopControlsContainerView"] button');
                      const pathD = topBtn?.querySelector('svg path')?.getAttribute('d') || '';
                      if (topBtn && pathD.startsWith('M20 25')) {
                        window.__charonWbdExpanded = true;
                        topBtn.click();
                      }
                    }

                    if (v.readyState >= 2 && v.videoWidth > 0 && !v.__charonStyled) {
                      v.__charonStyled = true;
                      v.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 999999 !important'
                      ].join('; ');
                    }
                  };

                  // Run once immediately so ChromeTuner's Runtime.evaluate (userGesture: true) executes play/expand with gesture privileges
                  checkPlayer();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, wbdTuneJs, new UTF8Encoding(false));
        }
		
		if (isCnn)
        {
            const string cnnStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                *:has(video) {
                  transform: none !important;
                  filter: none !important;
                  perspective: none !important;
                  contain: none !important;
                  isolation: auto !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                }
                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  max-width: none !important;
                  max-height: none !important;
                  margin: 0 !important;
                  object-fit: fill !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                header, footer, nav,
                .header, .footer,
                [class*="control-bar" i],
                [class*="controls" i],
                [data-testid*="controls" i],
                #onetrust-consent-sdk {
                  opacity: 0 !important;
                }
                """;
            File.WriteAllText(styleCssPath, cnnStyleCss, new UTF8Encoding(false));

            const string cnnTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      return mimeType.toLowerCase().includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        *:has(video) { transform: none !important; filter: none !important; perspective: none !important; contain: none !important; z-index: 2147483647 !important; background: #000 !important; }
                        video { position: fixed !important; inset: 0 !important; top: 0 !important; left: 0 !important; width: 100vw !important; height: 100vh !important; max-width: none !important; max-height: none !important; margin: 0 !important; object-fit: fill !important; z-index: 2147483647 !important; background: #000 !important; cursor: none !important; }
                        header, footer, nav,
                        .header, .footer,
                        [class*="control-bar" i],
                        [class*="controls" i],
                        [data-testid*="controls" i],
                        #onetrust-consent-sdk {
                          opacity: 0 !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };

                  const findMainVideo = () =>
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();

                    const allVideos = Array.from(document.querySelectorAll('video'));
                    if (allVideos.length === 0) return;

                    for (const v of allVideos) {
                      if (v.muted || v.volume === 0) {
                        const unmuteBtn = document.querySelector(
                          'button[aria-label*="Unmute" i], ' +
                          'button[title*="Unmute" i], ' +
                          '[class*="unmute" i], ' +
                          '[data-testid*="unmute" i]'
                        );
                        if (unmuteBtn) unmuteBtn.click();
                        v.muted = false;
                        v.volume = 1.0;
                      } else if (v.volume < 1.0) {
                        v.volume = 1.0;
                      }

                      if (v.paused && (v.src || v.currentSrc || v.readyState >= 1)) {
                        const playBtn = document.querySelector(
                          'button[aria-label*="Play" i], ' +
                          'button[title*="Play" i], ' +
                          '[data-testid*="play" i]'
                        );
                        if (playBtn) playBtn.click();
                        v.play().catch(() => {});
                      }
                    }

                    const activeVideo = findMainVideo();
                    if (!activeVideo) return;

                    for (const v of allVideos) {
                      if (v !== activeVideo && !v.paused) {
                        v.muted = true;
                        try { v.pause(); } catch {}
                      }
                    }

                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 2147483647 !important'
                      ].join('; ');
                    }
                  };

                  checkPlayer();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, cnnTuneJs, new UTF8Encoding(false));
        }
		
		if (isHulu)
        {
            const string huluStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Hulu's player controls and pause ads, and the "Ad" badge/countdown bar. Made invisible rather than
                   removed, so tune.js can still click their buttons. */
                #web-player-app .ControlsContainer,
                #web-player-app .AdUnitView,
                #web-player-app .PauseAd {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, huluStyleCss, new UTF8Encoding(false));

            const string huluTuneJs = """
                (function () {
                  try {
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }

                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  if (window.top !== window) return;

                  // A channel link (/watch/<channel id>) immediately redirects to the current airing (/watch/<airing id>).
                  // Remember the URL this page was loaded with, so the tuner can return to the channel if Hulu leaves the player.
                  const watchPathRe = /^\/watch\/[0-9a-f-]{36}/i;
                  try {
                    const nav = performance.getEntriesByType('navigation')[0];
                    const loadedPath = nav ? new URL(nav.name).pathname : location.pathname;
                    if (watchPathRe.test(loadedPath)) sessionStorage.setItem('__charonHuluTarget', loadedPath);
                  } catch {}

                  // Picks the first profile on "Who's watching?" (only shown on accounts with several profiles; that picker has not
                  // been observed live, so its selectors are generic), reloads the channel link once if Hulu leaves the player page,
                  // and answers Hulu's "Are you still watching?" panel.
                  const dismissPrompts = () => {
                    try {
                      const path = location.pathname;
                      if (path.startsWith('/profiles')) {
                        const candidates = Array.from(document.querySelectorAll(
                          '[data-automationid*="profile" i] button, [data-automationid*="profile" i] a, ' +
                          '[class*="profile" i] button, [class*="profile" i] a, [class*="Profile" i][role="button"]'))
                          .filter((el) => !/add|edit|manage|kids/i.test((el.textContent || '') + ' ' + (el.getAttribute('aria-label') || '')));
                        const pick = candidates[0];
                        if (pick && Date.now() - (window.__charonHuluProfileClickAt || 0) > 3000) {
                          window.__charonHuluProfileClickAt = Date.now();
                          pick.click();
                        }
                        window.__charonHuluOffSince = 0;
                        return;
                      }

                      if (watchPathRe.test(path)) {
                        window.__charonHuluOffSince = 0;
                      } else {
                        const target = sessionStorage.getItem('__charonHuluTarget');
                        if (target && !sessionStorage.getItem('__charonHuluReturned')) {
                          if (!window.__charonHuluOffSince) window.__charonHuluOffSince = Date.now();
                          else if (Date.now() - window.__charonHuluOffSince > 5000) {
                            sessionStorage.setItem('__charonHuluReturned', '1');
                            location.replace(target);
                            return;
                          }
                        }
                      }

                      // The panel (.long-time-tips-container) is always in the player's DOM, kept at display: none until Hulu shows it.
                      // Click only while it is visible: clicking its YES button at other times stops the live stream from starting.
                      // (Don't match it by text through a generic "modal" selector: Hulu puts class "player-modal" on <html>.)
                      const tips = document.querySelector('.long-time-tips-container');
                      if (tips && getComputedStyle(tips).display !== 'none' && Date.now() - (window.__charonHuluTipsClickAt || 0) > 3000) {
                        const yes = tips.querySelector('.long-time-tips__continue-button-container button');
                        if (yes) {
                          window.__charonHuluTipsClickAt = Date.now();
                          yes.click();
                        }
                      }
                    } catch {}
                  };
                  dismissPrompts();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  // Hulu's web player (#web-player-app) stacks ContentPlayer, AdPlayer and IntroPlayer, each with its own <video>,
                  // under ControlsContainer (controls, pause ads) and AdUnitView (the "Ad" badge and countdown bar).
                  // Overlays are made invisible rather than removed, so their buttons can still be clicked from script.
                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        #web-player-app .ControlsContainer, #web-player-app .AdUnitView, #web-player-app .PauseAd {
                          opacity: 0 !important; pointer-events: none !important;
                        }
                        #web-player-app .ContentPlayer, #web-player-app .AdPlayer:not(.AdPlayer--hidden) {
                          position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important;
                          transform: none !important; margin: 0 !important; background: #000 !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  // The ad player only plays client-side ad breaks; otherwise the content player is the one to show
                  const findMainVideo = () => {
                    const isLive = (v) => v && v.readyState >= 2 && v.videoWidth > 0 && !v.paused;
                    const content = document.getElementById('content-video-player');
                    const ad = document.getElementById('ad-video-player');
                    if (isLive(ad) && !(ad.closest('.AdPlayer--hidden'))) return ad;
                    if (content) return content;
                    return Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];
                  };

                  // Called by ChromeTuner before it sends a trusted click to unpause the player. Report "paused" only once the video
                  // has data: while Hulu is still loading the stream a click can't help, and a click on the player toggles pause.
                  window.__charonIsPaused = () => {
                    dismissPrompts();
                    const v = findMainVideo();
                    if (!v || v.readyState < 2) return false;
                    return v.paused;
                  };

                  const fullBleedCss = [
                    'background: black !important',
                    'cursor: none !important',
                    'display: block !important',
                    'height: 100vh !important',
                    'width: 100vw !important',
                    'left: 0 !important',
                    'top: 0 !important',
                    'margin: 0 !important',
                    'max-height: none !important',
                    'max-width: none !important',
                    'object-fit: fill !important',
                    'position: fixed !important',
                    // ContentPlayer__item--stretched centers the video with translate(-50%, -50%)
                    'transform: none !important',
                    'z-index: 999999 !important'
                  ].join('; ');

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPrompts();

                    const activeVideo = findMainVideo();
                    if (!activeVideo) return;

                    if (activeVideo.muted) activeVideo.muted = false;
                    if (activeVideo.volume < 1.0) activeVideo.volume = 1.0;
                    if (activeVideo.paused && (activeVideo.src || activeVideo.currentSrc || activeVideo.readyState >= 1)) {
                      activeVideo.play().catch(() => {});
                    }

                    if (activeVideo.readyState < 2 || activeVideo.videoWidth === 0 || activeVideo.paused) return;

                    try { sessionStorage.removeItem('__charonHuluReturned'); } catch {}

                    // Restyle whichever <video> is playing, and drop the previous one below it
                    if (window.__charonHuluActive !== activeVideo) {
                      const prev = window.__charonHuluActive;
                      if (prev && prev.__charonStyled) {
                        prev.__charonStyled = false;
                        prev.style.removeProperty('z-index');
                      }
                      window.__charonHuluActive = activeVideo;
                    }

                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = fullBleedCss;
                    }
                  };

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, huluTuneJs, new UTF8Encoding(false));
        }
		
		if (isFubo)
        {
            const string fuboStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Fubo's player controls (data-testid="player-*") and the feedback widget. Made invisible rather than removed,
                   so tune.js can still click their buttons. */
                [data-testid^="player-"],
                [data-testid="typeform-embed"] {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, fuboStyleCss, new UTF8Encoding(false));

            const string fuboTuneJs = """
                (function () {
                  try {
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }

                    if (window.HTMLVideoElement && !window.__charonQualityLocked) {
                      window.__charonQualityLocked = true;
                      const origGetQuality = window.HTMLVideoElement.prototype.getVideoPlaybackQuality;
                      if (origGetQuality) {
                        window.HTMLVideoElement.prototype.getVideoPlaybackQuality = function () {
                          const q = origGetQuality.call(this);
                          return {
                            creationTime: q.creationTime,
                            totalVideoFrames: q.totalVideoFrames,
                            droppedVideoFrames: 0,
                            corruptedVideoFrames: 0
                          };
                        };
                      }
                    }
                  } catch {}

                  if (window.top !== window) return;

                  // A channel link (link.fubo.tv/al1?...channel_id=<id>) redirects to www.fubo.tv/al/..., which the web app then
                  // routes to the player at /p/watch?channelId=<id>&type=live. A channel the account doesn't carry lands on /p/home.
                  // Remember the /al/ link this page was loaded with, so the tuner can retry it once if Fubo leaves the player.
                  const watchPathRe = /^\/p\/watch/i;
                  try {
                    const nav = performance.getEntriesByType('navigation')[0];
                    const loaded = nav ? new URL(nav.name) : new URL(location.href);
                    if (/^\/al\//i.test(loaded.pathname)) sessionStorage.setItem('__charonFuboTarget', loaded.pathname + loaded.search);
                  } catch {}

                  // Reloads the channel link once if Fubo leaves the player page, answers "Are you still watching?" prompts
                  // (generic selectors; that prompt has not been observed live yet), and presses "Try Again" on Fubo's
                  // output-protection error ("Your current video setup is not permitted"), at most every 15 seconds.
                  const dismissPrompts = () => {
                    try {
                      if (watchPathRe.test(location.pathname)) {
                        window.__charonFuboOffSince = 0;
                      } else {
                        const target = sessionStorage.getItem('__charonFuboTarget');
                        if (target && !sessionStorage.getItem('__charonFuboReturned')) {
                          if (!window.__charonFuboOffSince) window.__charonFuboOffSince = Date.now();
                          else if (Date.now() - window.__charonFuboOffSince > 8000) {
                            sessionStorage.setItem('__charonFuboReturned', '1');
                            location.replace(target);
                            return;
                          }
                        }
                      }

                      const dialogs = document.querySelectorAll('[role="alertdialog"], [role="dialog"], [data-testid="modal-overlay"]');
                      for (const dlg of dialogs) {
                        const text = dlg.textContent || '';
                        const buttons = Array.from(dlg.querySelectorAll('button, [role="button"]'));
                        if (/still (?:watching|there)/i.test(text)) {
                          const btn = buttons.find((b) => /continue|keep watching|still (?:here|watching)|^\s*yes\b|resume/i.test(b.textContent || ''));
                          if (btn) btn.click();
                        } else if (/not permitted/i.test(text) && Date.now() - (window.__charonFuboRetryAt || 0) > 15000) {
                          const btn = buttons.find((b) => /try again/i.test(b.textContent || ''));
                          if (btn) {
                            window.__charonFuboRetryAt = Date.now();
                            btn.click();
                          }
                        }
                      }
                    } catch {}
                  };
                  dismissPrompts();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  // The player renders into #portal: a fixed layer holding div#video (the stream's <video> plus
                  // #bumper-video-element for Fubo's intro bumper) under the controls and metadata overlay. Lift div#video
                  // above its siblings and hide the controls, which Fubo marks with data-testid="player-*".
                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        #portal #video {
                          position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important;
                          z-index: 2147483000 !important; background: #000 !important;
                        }
                        [data-testid^="player-"], [data-testid="typeform-embed"] { opacity: 0 !important; pointer-events: none !important; }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };
                  injectStaticCss();

                  const isPlaying = (v) => v && v.readyState >= 2 && v.videoWidth > 0 && !v.paused;

                  // Show the bumper only while it plays; otherwise the stream's <video> is the one to use
                  const findMainVideo = () => {
                    const bumper = document.getElementById('bumper-video-element');
                    if (isPlaying(bumper)) return bumper;
                    const main = document.querySelector('#video video:not(#bumper-video-element)');
                    if (main) return main;
                    return Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];
                  };

                  // Called by ChromeTuner before it sends a trusted click to unpause the player. Report "paused" only once the
                  // video has data: while Fubo is still loading the stream a click can't help.
                  window.__charonIsPaused = () => {
                    dismissPrompts();
                    const v = findMainVideo();
                    if (!v || v.readyState < 2) return false;
                    return v.paused;
                  };

                  const fullBleedCss = [
                    'background: black !important',
                    'cursor: none !important',
                    'display: block !important',
                    'height: 100vh !important',
                    'width: 100vw !important',
                    'left: 0 !important',
                    'top: 0 !important',
                    'margin: 0 !important',
                    'max-height: none !important',
                    'max-width: none !important',
                    'object-fit: fill !important',
                    'position: fixed !important',
                    'transform: none !important',
                    'z-index: 999999 !important'
                  ].join('; ');

                  const checkPlayer = () => {
                    injectStaticCss();
                    dismissPrompts();

                    const activeVideo = findMainVideo();
                    if (!activeVideo) return;

                    if (activeVideo.muted) activeVideo.muted = false;
                    if (activeVideo.volume < 1.0) activeVideo.volume = 1.0;
                    if (activeVideo.paused && (activeVideo.src || activeVideo.currentSrc || activeVideo.readyState >= 1)) {
                      activeVideo.play().catch(() => {});
                    }

                    if (!isPlaying(activeVideo)) return;

                    try { sessionStorage.removeItem('__charonFuboReturned'); } catch {}

                    // Restyle whichever <video> is playing, and drop the previous one below it
                    if (window.__charonFuboActive !== activeVideo) {
                      const prev = window.__charonFuboActive;
                      if (prev && prev.__charonStyled) {
                        prev.__charonStyled = false;
                        prev.style.removeProperty('z-index');
                      }
                      window.__charonFuboActive = activeVideo;
                    }

                    if (!activeVideo.__charonStyled) {
                      activeVideo.__charonStyled = true;
                      activeVideo.style.cssText = fullBleedCss;
                    }
                  };

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, fuboTuneJs, new UTF8Encoding(false));
        }
		
		if (isAbc)
        {
            const string abcStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                *:has(video) {
                  transform: none !important;
                  contain: none !important;
                }

                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                  max-width: none !important;
                  max-height: none !important;
                  object-fit: fill !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                  cursor: none !important;
                  opacity: 1 !important;
                }

                /* Hide ABC navigation, channel carousel, EPG grid, footer, and player UI with opacity: 0 */
                .navigation__skipnav__container,
                nav.navigation,
                nav.Breadcrumbs__wrapper,
                section.tilegroup,
                .tilegroup--featuredchannels,
                section.CategoryTabs,
                div.EPG,
                footer.footer,
                fitt-web-player-ui,
                .DxcOverlay,
                .FittWebPlayerContainer-loader,
                #onetrust-consent-sdk,
                #sdk-wrapper {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, abcStyleCss, new UTF8Encoding(false));

            const string abcTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      return mimeType.toLowerCase().includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                  } catch {}

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

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        *:has(video) { transform: none !important; contain: none !important; }
                        video { position: fixed !important; inset: 0 !important; top: 0 !important; left: 0 !important; width: 100vw !important; height: 100vh !important; margin: 0 !important; max-width: none !important; max-height: none !important; object-fit: fill !important; z-index: 2147483647 !important; background: #000 !important; cursor: none !important; opacity: 1 !important; }
                        .navigation__skipnav__container, nav.navigation, nav.Breadcrumbs__wrapper, section.tilegroup, .tilegroup--featuredchannels, section.CategoryTabs, div.EPG, footer.footer, fitt-web-player-ui, .DxcOverlay, .FittWebPlayerContainer-loader, #onetrust-consent-sdk, #sdk-wrapper {
                          opacity: 0 !important; pointer-events: none !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };

                  const findMainVideo = () =>
                    queryAllDeep('video')
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();

                    // Hide any Shadow DOM UI overlays inside fitt-web-player-ui or disney-web-player
                    for (const ui of queryAllDeep('fitt-web-player-ui, .DxcOverlay, [class*="controls" i], [class*="overlay" i]')) {
                      if (ui.tagName !== 'VIDEO' && !ui.querySelector?.('video') && ui.style) {
                        ui.style.setProperty('opacity', '0', 'important');
                        ui.style.setProperty('pointer-events', 'none', 'important');
                      }
                    }

                    const v = findMainVideo();
                    if (!v) return;

                    // 1. Play FIRST while muted so Chrome autoplay never blocks it
                    if (v.paused) {
                      const playBtn = queryAllDeep('button[aria-label*="Play" i], button[title*="Play" i]')[0];
                      if (playBtn) playBtn.click();
                      v.play().catch(() => {});
                    }

                    // 2. Unmute once playing
                    if (!v.paused && (v.muted || v.volume === 0)) {
                      const unmuteBtn = queryAllDeep('button[aria-label*="Unmute" i], button[title*="Unmute" i], button[aria-label*="Mute" i][aria-pressed="true"]')[0];
                      if (unmuteBtn) unmuteBtn.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (!v.paused && v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    if (!v.__charonStyled) {
                      v.__charonStyled = true;
                      v.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 2147483647 !important'
                      ].join('; ');
                    }
                  };

                  checkPlayer();
                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;
                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, abcTuneJs, new UTF8Encoding(false));
        }
		
		if (isCbs)
        {
            const string cbsStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                *:has(video) {
                  transform: none !important;
                  contain: none !important;
                }

                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                  max-width: none !important;
                  max-height: none !important;
                  object-fit: fill !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                  cursor: none !important;
                  opacity: 1 !important;
                }

                /* Hide MVPD "Currently connected to / Start Watching" splash & loading slate
                   with opacity: 0 so tune.js can still click Start Watching */
                #app:not(:has(video)),
                #main-aa-container,
                mvpd,
                #live-tv-providers,
                .tv__provider__selector__container,
                .slate,
                header#global-header-container,
                .schedule-breadcrumbs,
                .rows-container,
                .schedule-container,
                .video__metadata,
                .bottom-components,
                footer,
                cbs-avia-chrome,
                [class*="chrome" i],
                [class*="controls" i],
                #onetrust-consent-sdk {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, cbsStyleCss, new UTF8Encoding(false));

            const string cbsTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      return mimeType.toLowerCase().includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                  } catch {}

                  // Automatically dismiss the CBS MVPD "Start Watching" confirmation splash screen during tuning
                  const dismissSplash = () => {
                    try {
                      const buttons = Array.from(document.querySelectorAll('button, a, div[role="button"], span, .button'));
                      const startBtn = buttons.find(el => {
                        const t = (el.textContent || '').trim().toLowerCase();
                        return t === 'start watching' || t === 'start watching »' || el.getAttribute('aria-label')?.toLowerCase() === 'start watching';
                      });
                      if (startBtn && Date.now() - (window.__charonCbsClickAt || 0) > 2000) {
                        window.__charonCbsClickAt = Date.now();
                        startBtn.click();
                      }
                    } catch {}
                  };
                  dismissSplash();

                  const handleErrors = () => {
                    try {
                      const bodyText = document.body.innerText || '';
                      if (bodyText.includes('This local station is not supported')) {
                        const homeBtn = Array.from(document.querySelectorAll('a, button'))
                          .find(el => /go to home/i.test(el.textContent || ''));
                        if (homeBtn && Date.now() - (window.__charonCbsRetryAt || 0) > 5000) {
                          window.__charonCbsRetryAt = Date.now();
                          homeBtn.click();
                        } else if (!window.__charonCbsReloaded) {
                          window.__charonCbsReloaded = true;
                          setTimeout(() => location.reload(), 3000);
                        }
                      }
                    } catch {}
                  };
                  handleErrors();

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

                  const findMainVideo = () =>
                    queryAllDeep('video')
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    dismissSplash();
                    handleErrors();
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    dismissSplash();
                    handleErrors();
                    const v = findMainVideo();
                    if (!v) return;

                    if (v.paused) {
                      const playBtn = queryAllDeep('button[aria-label*="Play" i], avia-button[aria-label*="Play" i]')[0];
                      if (playBtn) playBtn.click();
                      v.play().catch(() => {});
                    }

                    if (!v.paused && (v.muted || v.volume === 0)) {
                      const unmuteBtn = queryAllDeep('button[aria-label*="Mute" i], avia-button[aria-label*="Mute" i]')[0];
                      if (unmuteBtn) unmuteBtn.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (!v.paused && v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    if (!v.__charonStyled) {
                      v.__charonStyled = true;
                      v.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 2147483647 !important'
                      ].join('; ');
                    }
                  };

                  checkPlayer();
                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;
                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, cbsTuneJs, new UTF8Encoding(false));
        }
		
		if (isFox)
        {
            const string foxStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Full-screen black curtain covering the entire viewport until playback starts */
                html::after {
                  content: "" !important;
                  position: fixed !important;
                  inset: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  background: #000 !important;
                  z-index: 2147483647 !important;
                  pointer-events: none !important;
                }

                html.charon-playing::after {
                  display: none !important;
                }

                *:has(video) {
                  transform: none !important;
                  filter: none !important;
                  perspective: none !important;
                  will-change: auto !important;
                  contain: none !important;
                }

                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                  max-width: none !important;
                  max-height: none !important;
                  object-fit: fill !important;
                  z-index: 2147483646 !important;
                  background: #000 !important;
                  cursor: none !important;
                  opacity: 1 !important;
                }

                header, nav, footer,
                [data-testid="ChannelsPage"],
                [data-testid="ChannelsScrollArea"],
                [class*="controls" i],
                [class*="overlay" i],
                #onetrust-consent-sdk {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, foxStyleCss, new UTF8Encoding(false));

            const string foxTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      return mimeType.toLowerCase().includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                  } catch {}

                  // Capture #charon_channel=CODE before FOX's SPA router strips the hash
                  try {
                    const hashMatch = location.hash.match(/charon_channel=([^&]+)/i);
                    if (hashMatch) {
                      sessionStorage.setItem('__charonFoxTarget', decodeURIComponent(hashMatch[1]));
                    }
                  } catch {}

                  const navigateFromGuide = () => {
                    if (!location.pathname.startsWith('/live')) return false;

                    let targetCode = '';
                    try { targetCode = sessionStorage.getItem('__charonFoxTarget') || ''; } catch {}
                    if (!targetCode) return false;

                    const now = Date.now();

                    // Step 1: Click the target channel logo in the guide
                    if (window.__charonFoxLogoClicked !== targetCode) {
                      const logoBtn = document.querySelector(`[data-testid="GuideChannelLogo"] button[aria-label="${targetCode}"]`)
                        || Array.from(document.querySelectorAll('[data-testid="GuideChannelLogo"] button'))
                             .find(b => (b.getAttribute('aria-label') || b.querySelector('img')?.getAttribute('alt') || '').toUpperCase() === targetCode.toUpperCase());

                      if (logoBtn) {
                        window.__charonFoxLogoClicked = targetCode;
                        window.__charonFoxLogoClickedAt = now;
                        logoBtn.click();
                      }
                      return true;
                    }

                    // Step 2: If clicking the logo only selected the channel in the hero banner, click "Watch Live"
                    if (now - (window.__charonFoxLogoClickedAt || 0) >= 500 && now - (window.__charonFoxWatchClickedAt || 0) > 2500) {
                      const watchLink = document.querySelector('a[href^="/watch/epg/"]');
                      if (watchLink) {
                        window.__charonFoxWatchClickedAt = now;
                        watchLink.click();
                      }
                    }
                    return true;
                  };

                  const findMainVideo = () =>
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    if (location.pathname.startsWith('/live')) return false;
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    if (navigateFromGuide()) return;

                    const v = findMainVideo();
                    if (!v) return;

                    if (v.paused) {
                      const playBtn = document.querySelector('button[aria-label*="Play" i], button[title*="Play" i]');
                      if (playBtn) playBtn.click();
                      v.play().catch(() => {});
                    }

                    if (!v.paused && (v.muted || v.volume === 0)) {
                      const unmuteBtn = document.querySelector('button[aria-label*="Unmute" i], button[title*="Unmute" i], button[aria-label*="Mute" i]');
                      if (unmuteBtn) unmuteBtn.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (!v.paused && v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    if (!v.__charonStyled && !v.paused && v.readyState >= 2 && v.videoWidth > 0 && v.currentTime > 1.0) {
                      v.__charonStyled = true;
                      document.documentElement.classList.add('charon-playing');
                      v.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'opacity: 1 !important',
                        'position: fixed !important',
                        'z-index: 2147483646 !important'
                      ].join('; ');
                    }
                  };

                  checkPlayer();
                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;
                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, foxTuneJs, new UTF8Encoding(false));
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
		
		if (isNbc)
        {
            const string nbcStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Keep the video player container and its ancestors free of transform/contain traps */
                *:has(video) {
                  transform: none !important;
                  contain: none !important;
                }

                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                  max-width: none !important;
                  max-height: none !important;
                  object-fit: fill !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                  cursor: none !important;
                  opacity: 1 !important;
                }

                /* Hide the lower guide grid, filter bar, footer, and player HUD with opacity: 0
                   so NBC's layout/measurement math runs at full speed while rendering pitch-black */
                section.links-selectable-group,
                .live-page__links-selectable-group,
                .livePage__filter,
                .live-guide__wrapper,
                .live-guide__nav,
                .live-guide__schedules,
                .live-guide__streams,
                .live-guide__inner,
                footer.footer,
                .footer,
                #onetrust-consent-sdk,
                .video-overlay,
                .brand-header,
                .video-player__controls,
                [class*="header" i],
                [class*="controls" i] {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, nbcStyleCss, new UTF8Encoding(false));

            const string nbcTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      return mimeType.toLowerCase().includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                  } catch {}

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        *:has(video) { transform: none !important; contain: none !important; }
                        video { position: fixed !important; inset: 0 !important; top: 0 !important; left: 0 !important; width: 100vw !important; height: 100vh !important; margin: 0 !important; max-width: none !important; max-height: none !important; object-fit: fill !important; z-index: 2147483647 !important; background: #000 !important; cursor: none !important; opacity: 1 !important; }
                        section.links-selectable-group, .live-page__links-selectable-group, .livePage__filter,
                        .live-guide__wrapper, .live-guide__nav, .live-guide__schedules, .live-guide__streams, .live-guide__inner,
                        footer.footer, .footer, #onetrust-consent-sdk, .video-overlay, .brand-header, .video-player__controls,
                        [class*="header" i], [class*="controls" i] {
                          opacity: 0 !important; pointer-events: none !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };

                  const checkPlayer = () => {
                    injectStaticCss();

                    const v = document.querySelector('video');
                    if (!v) return;

                    if (v.muted || v.volume === 0) {
                      const unmute = document.querySelector('button[aria-label*="Unmute" i], [class*="unmute" i]');
                      if (unmute) unmute.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    if (v.paused && v.readyState >= 1) {
                      const play = document.querySelector('button[aria-label*="Play" i]');
                      if (play) play.click();
                      v.play().catch(() => {});
                    }
                  };

                  checkPlayer();

                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;

                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, nbcTuneJs, new UTF8Encoding(false));
        }
		
		if (isAeFamily)
        {
            const string aeStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }
                *:has(video) {
                  transform: none !important;
                  contain: none !important;
                }
                video {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                  max-width: none !important;
                  max-height: none !important;
                  object-fit: fill !important;
                  z-index: 2147483647 !important;
                  background: #000 !important;
                  cursor: none !important;
                  opacity: 1 !important;
                }
                header, [class*="headerStack" i],
                [class*="SkipToMainContent" i],
                #engagement-wrapper,
                footer, [class*="footer" i],
                #onetrust-consent-sdk,
                [class*="controls" i],
                [class*="overlay" i] {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, aeStyleCss, new UTF8Encoding(false));

            const string aeTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      return mimeType.toLowerCase().includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                  } catch {}

                  const injectStaticCss = () => {
                    if (!document.getElementById('charon-static-css') && (document.head || document.documentElement)) {
                      const s = document.createElement('style');
                      s.id = 'charon-static-css';
                      s.textContent = `
                        html, body { background: #000 !important; overflow: hidden !important; margin: 0 !important; padding: 0 !important; cursor: none !important; }
                        *:has(video) { transform: none !important; contain: none !important; }
                        video { position: fixed !important; inset: 0 !important; top: 0 !important; left: 0 !important; width: 100vw !important; height: 100vh !important; margin: 0 !important; max-width: none !important; max-height: none !important; object-fit: fill !important; z-index: 2147483647 !important; background: #000 !important; cursor: none !important; opacity: 1 !important; }
                        header, [class*="headerStack" i], [class*="SkipToMainContent" i], #engagement-wrapper, footer, [class*="footer" i], #onetrust-consent-sdk, [class*="controls" i], [class*="overlay" i] {
                          opacity: 0 !important; pointer-events: none !important;
                        }
                      `;
                      (document.head || document.documentElement).appendChild(s);
                    }
                  };

                  const findMainVideo = () =>
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  window.__charonIsPaused = () => {
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    injectStaticCss();

                    const v = findMainVideo();
                    if (!v) return;

                    // 1. Play FIRST while muted so Chrome autoplay never blocks it
                    if (v.paused) {
                      const playBtn = document.querySelector(
                        'button[aria-label*="Play" i], button[title*="Play" i], [class*="play" i]'
                      );
                      if (playBtn) playBtn.click();
                      v.play().catch(() => {});
                    }

                    // 2. Unmute once playing
                    if (!v.paused && (v.muted || v.volume === 0)) {
                      const unmuteBtn = document.querySelector(
                        'button[aria-label*="Unmute" i], button[title*="Unmute" i], [class*="unmute" i]'
                      );
                      if (unmuteBtn) unmuteBtn.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (!v.paused && v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    if (!v.__charonStyled) {
                      v.__charonStyled = true;
                      v.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'position: fixed !important',
                        'z-index: 2147483647 !important'
                      ].join('; ');
                    }
                  };

                  checkPlayer();
                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;
                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, aeTuneJs, new UTF8Encoding(false));
        }
		if (isUsa)
        {
            const string usaStyleCss = """
                html, body {
                  margin: 0 !important;
                  padding: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  overflow: hidden !important;
                  background: #000 !important;
                  cursor: none !important;
                }

                /* Full-screen black curtain covering the guide & loader until playback starts */
                html::after {
                  content: "" !important;
                  position: fixed !important;
                  inset: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  background: #000 !important;
                  z-index: 2147483647 !important;
                  pointer-events: none !important;
                }

                html.charon-playing::after {
                  display: none !important;
                }

                *:has(video) {
                  transform: none !important;
                  filter: none !important;
                  perspective: none !important;
                  will-change: auto !important;
                  contain: none !important;
                }

                video, video#epg-player_html5_api, .vjs-tech {
                  position: fixed !important;
                  inset: 0 !important;
                  top: 0 !important;
                  left: 0 !important;
                  width: 100vw !important;
                  height: 100vh !important;
                  margin: 0 !important;
                  max-width: none !important;
                  max-height: none !important;
                  object-fit: fill !important;
                  z-index: 2147483646 !important;
                  background: #000 !important;
                  cursor: none !important;
                  opacity: 1 !important;
                }

                /* Hide non-player page modules, headers/footers, and Video.js overlays */
                .module-loader-wrapper:not(:has(video)),
                header, footer, nav,
                .vjs-control-bar,
                .vjs-big-play-button,
                .vjs-title-bar,
                .vjs-poster,
                .vjs-loading-spinner,
                .vjs-text-track-display,
                .vjs-text-track-display-ttml,
                .vjs-modal-dialog,
                .tvProviderImage,
                #onetrust-consent-sdk {
                  opacity: 0 !important;
                  pointer-events: none !important;
                }
                """;
            File.WriteAllText(styleCssPath, usaStyleCss, new UTF8Encoding(false));

            const string usaTuneJs = """
                (function () {
                  try {
                    const blockHeavyCodecs = (mimeType) => {
                      if (typeof mimeType !== 'string') return false;
                      return mimeType.toLowerCase().includes('av01');
                    };
                    if (window.MediaSource && !window.__charonCodecLocked) {
                      window.__charonCodecLocked = true;
                      const origIsTypeSupported = window.MediaSource.isTypeSupported.bind(window.MediaSource);
                      window.MediaSource.isTypeSupported = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return false;
                        return origIsTypeSupported(mimeType);
                      };
                    }
                    if (window.HTMLMediaElement && !window.__charonCanPlayLocked) {
                      window.__charonCanPlayLocked = true;
                      const origCanPlayType = window.HTMLMediaElement.prototype.canPlayType;
                      window.HTMLMediaElement.prototype.canPlayType = function (mimeType) {
                        if (blockHeavyCodecs(mimeType)) return '';
                        return origCanPlayType.call(this, mimeType);
                      };
                    }
                    if (navigator.userActivation) {
                      Object.defineProperty(navigator.userActivation, 'isActive', { get: () => true });
                      Object.defineProperty(navigator.userActivation, 'hasBeenActive', { get: () => true });
                    }
                  } catch {}

                  // Capture #charon_channel=TOKEN before the SPA router strips the hash
                  try {
                    const hashMatch = location.hash.match(/charon_channel=([^&]+)/i);
                    if (hashMatch) {
                      sessionStorage.setItem('__charonUsaTarget', decodeURIComponent(hashMatch[1]));
                    }
                  } catch {}

                  // Find the matching .epg-channel-feed-container row by aria-label prefix and click its first live program tile
                  const selectChannelFromGuide = () => {
                    let target = '';
                    try { target = sessionStorage.getItem('__charonUsaTarget') || ''; } catch {}
                    if (!target || window.__charonUsaChannelSelected === target) return false;

                    const rows = Array.from(document.querySelectorAll('.epg-channel-feed-container'));
                    if (rows.length === 0) return true; // Guide not hydrated yet

                    const prefix = target.trim().toLowerCase() + ' ';

                    for (const row of rows) {
                      const firstTile = row.querySelector('.epg-tile-container');
                      const info = firstTile?.querySelector('.tile-info') || row.querySelector('.tile-info');
                      const aria = (info?.getAttribute('aria-label') || '').trim().toLowerCase();

                      if (aria.startsWith(prefix)) {
                        window.__charonUsaChannelSelected = target;
                        window.__charonUsaSelectedAt = Date.now();
                        const clickTarget = firstTile?.querySelector('a, button') || info || firstTile;
                        if (clickTarget) clickTarget.click();
                        return true;
                      }
                    }

                    return true;
                  };

                  const findMainVideo = () =>
                    document.querySelector('video#epg-player_html5_api') ||
                    document.querySelector('video.vjs-tech') ||
                    Array.from(document.querySelectorAll('video'))
                      .sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0];

                  const getVjsPlayer = (v) => {
                    try {
                      if (window.videojs) {
                        const players = window.videojs.getPlayers && window.videojs.getPlayers();
                        if (players && players['epg-player']) return players['epg-player'];
                      }
                      const wrapper = v && v.closest('.video-js');
                      if (wrapper && wrapper.player) return wrapper.player;
                    } catch {}
                    return null;
                  };

                  // Replicates clicking Pause then Play in the browser using Video.js's own API + DOM button
                  const kickPausePlay = (v) => {
                    if (window.__charonUsaKicking) return;
                    window.__charonUsaKicking = true;

                    const vjs = getVjsPlayer(v);
                    const playBtn = document.querySelector('.vjs-play-control');

                    try {
                      if (vjs && typeof vjs.pause === 'function') vjs.pause();
                      else if (playBtn) playBtn.click();
                      else v.pause();
                    } catch {}

                    setTimeout(() => {
                      try {
                        if (vjs && typeof vjs.play === 'function') vjs.play();
                        else if (playBtn && v.paused) playBtn.click();
                        v.play().catch(() => {});
                      } catch {}
                      setTimeout(() => { window.__charonUsaKicking = false; }, 1000);
                    }, 400);
                  };

                  // Replicates clicking the LIVE button on the player to recover from mid-stream SSAI freezes
                  const kickSeekToLive = (v) => {
                    const vjs = getVjsPlayer(v);
                    const liveBtn = document.querySelector('.vjs-seek-to-live-control, button[title*="Live" i], .vjs-live-control');

                    let clicked = false;
                    try {
                      if (vjs && vjs.liveTracker && typeof vjs.liveTracker.seekToLiveEdge === 'function') {
                        vjs.liveTracker.seekToLiveEdge();
                        clicked = true;
                      }
                    } catch {}

                    if (liveBtn) {
                      try {
                        liveBtn.click();
                        clicked = true;
                      } catch {}
                    }

                    if (v.buffered && v.buffered.length > 0) {
                      try { v.currentTime = v.buffered.end(v.buffered.length - 1) - 0.5; } catch {}
                    }

                    if (!clicked || v.paused) {
                      kickPausePlay(v);
                    } else {
                      v.play().catch(() => {});
                    }
                  };

                  window.__charonIsPaused = () => {
                    if (window.__charonUsaKicking) return false;
                    const v = findMainVideo();
                    if (!v) return false;
                    return v.paused;
                  };

                  const checkPlayer = () => {
                    if (selectChannelFromGuide()) return;

                    // Give the player a brief moment after clicking the EPG tile before interacting or lifting the curtain
                    if (window.__charonUsaSelectedAt && Date.now() - window.__charonUsaSelectedAt < 1200) return;

                    const v = findMainVideo();
                    if (!v || window.__charonUsaKicking) return;

                    // 1. Play FIRST while muted so Chrome autoplay never blocks it
                    if (v.paused) {
                      const bigPlay = document.querySelector('button.vjs-big-play-button, .vjs-play-control.vjs-paused');
                      if (bigPlay) bigPlay.click();
                      v.play().catch(() => {});
                      return;
                    }

                    // 2. Unmute once playing
                    if (v.muted || v.volume === 0) {
                      const vjs = getVjsPlayer(v);
                      if (vjs && typeof vjs.muted === 'function') {
                        try { vjs.muted(false); vjs.volume(1); } catch {}
                      }
                      const muteBtn = document.querySelector('.vjs-mute-control.vjs-vol-0, button[title*="Unmute" i], button[aria-label*="Unmute" i]');
                      if (muteBtn) muteBtn.click();
                      v.muted = false;
                      v.volume = 1.0;
                    } else if (v.volume < 1.0) {
                      v.volume = 1.0;
                    }

                    // 3. Proactive Startup Kick: Once video has played for ~2 seconds, do a single
                    // Pause -> Unpause cycle automatically so the initial buffer never freezes
                    if (!window.__charonDidInitialKick && v.currentTime > 0 && v.videoWidth > 0) {
                      if (!window.__charonPlayStartTime) {
                        window.__charonPlayStartTime = Date.now();
                      } else if (Date.now() - window.__charonPlayStartTime > 2000) {
                        window.__charonDidInitialKick = true;
                        kickPausePlay(v);
                        return;
                      }
                    }

                    // 4. Ongoing Time & Frame Progress Watchdog:
                    const time = v.currentTime;
                    const frames = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality().totalVideoFrames : (v.webkitDecodedFrameCount || 0);

                    if (time > 0 && window.__charonDidInitialKick) {
                      if (!window.__charonLastCheckTime) {
                        window.__charonLastTime = time;
                        window.__charonLastFrames = frames;
                        window.__charonLastCheckTime = Date.now();
                        window.__charonStallTicks = 0;
                      } else {
                        const now = Date.now();
                        const elapsed = now - window.__charonLastCheckTime;

                        if (elapsed >= 2000) {
                          const timeAdvanced = time - window.__charonLastTime;
                          const framesStuck = frames > 0 && window.__charonLastFrames > 0 && (frames - window.__charonLastFrames) < 2;

                          if (timeAdvanced < 0.1 || framesStuck) {
                            window.__charonStallTicks = (window.__charonStallTicks || 0) + 1;
                            if (window.__charonStallTicks >= 1) {
                              window.__charonStallTicks = 0;
                              kickSeekToLive(v);
                            }
                          } else {
                            window.__charonStallTicks = 0;
                          }

                          window.__charonLastTime = time;
                          window.__charonLastFrames = frames;
                          window.__charonLastCheckTime = now;
                        }
                      }
                    }

                    if (!v.__charonStyled && !v.paused && v.readyState >= 2 && v.videoWidth > 0 && v.currentTime > 0.2) {
                      v.__charonStyled = true;
                      document.documentElement.classList.add('charon-playing');
                      v.style.cssText = [
                        'background: black !important',
                        'cursor: none !important',
                        'display: block !important',
                        'height: 100vh !important',
                        'width: 100vw !important',
                        'left: 0 !important',
                        'top: 0 !important',
                        'margin: 0 !important',
                        'max-height: none !important',
                        'max-width: none !important',
                        'object-fit: fill !important',
                        'opacity: 1 !important',
                        'position: fixed !important',
                        'z-index: 2147483646 !important'
                      ].join('; ');
                    }
                  };

                  checkPlayer();
                  if (window.__charonTunerActive) return;
                  window.__charonTunerActive = true;
                  setInterval(checkPlayer, 300);
                })();
                """;
            File.WriteAllText(tuneJsPath, usaTuneJs, new UTF8Encoding(false));
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
	
	public static bool CanScanLineup(string? providerId) =>
        providerId is not null &&
        (providerId.Equals("youtubetv", StringComparison.OrdinalIgnoreCase) ||
         providerId.Equals("spectrum", StringComparison.OrdinalIgnoreCase) ||
         providerId.Equals("xfinity", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Equals("philo", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Equals("hulu", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Equals("fubo", StringComparison.OrdinalIgnoreCase) ||
		 providerId.Equals("sling", StringComparison.OrdinalIgnoreCase) ||
         providerId.Equals("nbc", StringComparison.OrdinalIgnoreCase) ||
         providerId.Equals("abc", StringComparison.OrdinalIgnoreCase) ||
         providerId.Equals("cbs", StringComparison.OrdinalIgnoreCase) ||
         providerId.Equals("fox", StringComparison.OrdinalIgnoreCase) ||
         providerId.Equals("usa", StringComparison.OrdinalIgnoreCase));
		 
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

    /// <param name="providerFilter">
    /// Optional set of provider IDs to include (case-insensitive). Null or empty includes all enabled providers.
    /// </param>
    public string GenerateM3uPlaylist(CharonConfig config, IReadOnlySet<string>? providerFilter = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        string baseUrl = $"http://{config.ResolvedLanIp}:{config.ResolvedPort}";

        bool filterActive = providerFilter is { Count: > 0 };

        foreach (var provider in _providers.Where(p => p.Manifest.Enabled))
        {
            if (filterActive && !providerFilter!.Contains(provider.Manifest.Id))
                continue;

            string group = string.IsNullOrWhiteSpace(provider.Manifest.GroupTitle)
                ? provider.Manifest.Name
                : provider.Manifest.GroupTitle;

            foreach (var ch in provider.Channels)
            {
                if (ch.IsHidden)
                    continue;

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