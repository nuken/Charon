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

        // If someone placed channels.json directly in Providers\ root instead of Providers\YouTubeTV\, move/copy it into Providers\YouTubeTV\
        string rootChannelsFile = Path.Combine(_providersRoot, "channels.json");
        string defaultYttvDir = Path.Combine(_providersRoot, "YouTubeTV");
        if (File.Exists(rootChannelsFile))
        {
            Directory.CreateDirectory(defaultYttvDir);
            string targetYttvChannels = Path.Combine(defaultYttvDir, "channels.json");
            if (!File.Exists(targetYttvChannels))
                File.Copy(rootChannelsFile, targetYttvChannels);
        }

        // Scan all subdirectories in Providers\
        foreach (var dir in Directory.GetDirectories(_providersRoot))
        {
            LoadOrRepairProviderDirectory(dir);
        }

        // Guarantee at least YouTubeTV exists and is loaded if no valid providers were found
        if (_providers.Count == 0)
        {
            Directory.CreateDirectory(defaultYttvDir);
            LoadOrRepairProviderDirectory(defaultYttvDir);
        }
    }

    private void LoadOrRepairProviderDirectory(string dir)
    {
        string folderName = Path.GetFileName(dir);
        string manifestPath = Path.Combine(dir, "provider.json");
        string altManifestPath = Path.Combine(dir, "manifest.json");
        string channelsPath = Path.Combine(dir, "channels.json");

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
                Name = folderName.Equals("YouTubeTV", StringComparison.OrdinalIgnoreCase) ? "YouTube TV" : folderName,
                GroupTitle = folderName.Equals("YouTubeTV", StringComparison.OrdinalIgnoreCase) ? "YouTube TV" : folderName,
                Enabled = true,
                LoginUrl = "https://tv.youtube.com",
                TuneScriptDelayMs = 1500
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(autoManifest, JsonOptions), new UTF8Encoding(false));
        }

        if (!File.Exists(channelsPath))
        {
            File.WriteAllText(channelsPath, "[]", new UTF8Encoding(false));
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
                Name = folderName.Equals("YouTubeTV", StringComparison.OrdinalIgnoreCase) ? "YouTube TV" : folderName,
                GroupTitle = folderName.Equals("YouTubeTV", StringComparison.OrdinalIgnoreCase) ? "YouTube TV" : folderName,
                Enabled = true,
                LoginUrl = "https://tv.youtube.com",
                TuneScriptDelayMs = 1500
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        }

        List<ProviderChannel> channels = [];
        try
        {
            channels = JsonSerializer.Deserialize<List<ProviderChannel>>(File.ReadAllText(channelsPath), JsonOptions) ?? [];
        }
        catch { }

        string tuneJsPath = Path.Combine(dir, "tune.js");
        string styleCssPath = Path.Combine(dir, "style.css");

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