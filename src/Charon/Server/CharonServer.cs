using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Charon.Config;
using Charon.Providers;
using Charon.Tuner;

namespace Charon.Server;

public sealed class CharonServer : IAsyncDisposable
{
    private readonly CharonConfig _config;
    private readonly ProviderManager _providers;
    private readonly ChromeTuner _tuner;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, string> _activeTuners = new();
    private WebApplication? _app;

    public IReadOnlyDictionary<int, string> ActiveTuners => _activeTuners;
    public event Action? TunersChanged;

    public CharonServer(CharonConfig config, ProviderManager providers, ChromeTuner tuner, Action<string> log)
    {
        _config = config;
        _providers = providers;
        _tuner = tuner;
        _log = log;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.ListenAnyIP(_config.ResolvedPort);
        });
        builder.Logging.ClearProviders();

        _app = builder.Build();

        // 1. Status & Root health check
        _app.MapGet("/", () => Results.Ok(new
        {
            application = "Charon",
            lanIp = _config.ResolvedLanIp,
            port = _config.ResolvedPort,
            playlistUrl = $"http://{_config.ResolvedLanIp}:{_config.ResolvedPort}/playlist.m3u",
            activeTuners = _activeTuners
        }));

        // 2. Channels DVR M3U Playlist Endpoint
        _app.MapGet("/playlist.m3u", (HttpContext ctx) =>
        {
            _providers.ReloadProviders();
            string m3u = _providers.GenerateM3uPlaylist(_config);
            ctx.Response.ContentType = "audio/x-mpegurl; charset=utf-8";
            return ctx.Response.WriteAsync(m3u);
        });

        // 3. Live MPEG-TS Stream Endpoint
        _app.MapGet("/stream/{providerId}/{channelId}", async (string providerId, string channelId, HttpContext ctx) =>
        {
            var (provider, channel) = _providers.FindChannel(providerId, channelId);
            if (provider is null || channel is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync($"Channel '{providerId}/{channelId}' not found.");
                return;
            }

            int tunerId = AcquireTunerSlot($"{provider.Manifest.Name}: {channel.Name}");
            if (tunerId == -1)
            {
                _log($"[Server] All {_config.MaxConcurrentTuners} tuner slots are busy. Rejecting tune for {channel.Name}.");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("All tuner slots are currently in use.");
                return;
            }

            try
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers.CacheControl = "no-cache, no-store";

                await _tuner.StreamChannelAsync(
                    tunerId,
                    provider,
                    channel,
                    ctx.Response.Body,
                    _log,
                    ctx.RequestAborted);
            }
            finally
            {
                ReleaseTunerSlot(tunerId);
            }
        });

        await _app.StartAsync();
        _log($"[Server] Charon listening on http://{_config.ResolvedLanIp}:{_config.ResolvedPort}");
        _log($"[Server] M3U URL: http://{_config.ResolvedLanIp}:{_config.ResolvedPort}/playlist.m3u");
    }

    private int AcquireTunerSlot(string description)
    {
        for (int i = 1; i <= _config.MaxConcurrentTuners; i++)
        {
            if (_activeTuners.TryAdd(i, description))
            {
                TunersChanged?.Invoke();
                return i;
            }
        }
        return -1;
    }

    private void ReleaseTunerSlot(int tunerId)
    {
        if (_activeTuners.TryRemove(tunerId, out _))
        {
            TunersChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}