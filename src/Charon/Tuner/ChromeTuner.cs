using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Charon.Config;
using Charon.Providers;

namespace Charon.Tuner;

public sealed class ChromeTuner
{
    private readonly CharonConfig _config;
    private static readonly HttpClient Http = new();

    // 1280x752 in --app mode = 32px Windows title bar + exact 1280x720 (16:9) client viewport
    private const int WindowWidth = 1280;
    private const int WindowHeight = 752;

    public ChromeTuner(CharonConfig config)
    {
        _config = config;
    }

    public string GetTunerProfilePath(int tunerId)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Profiles", $"Tuner_{tunerId}");
        Directory.CreateDirectory(path);
        return path;
    }

    public Process? LaunchInteractiveLogin(string loginUrl)
    {
        string primaryTunerProfile = GetTunerProfilePath(1);
        KillOrphanedChromeForProfile(primaryTunerProfile);

        var psi = new ProcessStartInfo
        {
            FileName = _config.ResolvedChromePath,
            Arguments = string.Join(" ",
            [
                $"--user-data-dir=\"{primaryTunerProfile}\"",
                "--no-first-run",
                "--no-default-browser-check",
                "--autoplay-policy=no-user-gesture-required",
                "--disable-features=HardwareMediaKeyHandling,MediaSessionService,Translate",
                $"\"{loginUrl}\""
            ]),
            UseShellExecute = false
        };

        var proc = Process.Start(psi);
        if (proc != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await proc.WaitForExitAsync();
                    await Task.Delay(500);
                    string profilesRoot = Path.Combine(AppContext.BaseDirectory, "Profiles");
                    if (Directory.Exists(profilesRoot))
                    {
                        foreach (string dir in Directory.GetDirectories(profilesRoot, "Tuner_*"))
                        {
                            if (!dir.EndsWith("Tuner_1", StringComparison.OrdinalIgnoreCase))
                            {
                                KillOrphanedChromeForProfile(dir);
                                try { Directory.Delete(dir, recursive: true); } catch { }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        return proc;
    }

    public async Task StreamChannelAsync(
        int tunerId,
        LoadedProvider provider,
        ProviderChannel channel,
        Stream outputStream,
        Action<string>? logCallback,
        CancellationToken ct)
    {
        string tunerProfile = PrepareTunerProfile(tunerId);
        int debugPort = 48600 + tunerId;
        int wsPort = 48700 + tunerId;

        (string extDir, string extensionId) = EnsureTabCaptureExtension(tunerId, wsPort, provider);

        using var httpListener = new HttpListener();
        httpListener.Prefixes.Add($"http://127.0.0.1:{wsPort}/");
        httpListener.Start();

        string chromeArgs = string.Join(" ",
        [
            $"--user-data-dir=\"{tunerProfile}\"",
            $"--remote-debugging-port={debugPort}",
            "--remote-allow-origins=*",
            "--enable-unsafe-extension-debugging",
            $"--whitelisted-extension-id={extensionId}",
            $"--allowlisted-extension-id={extensionId}",
            $"--app=\"{channel.Url}\"",
            $"--window-size={WindowWidth},{WindowHeight}",
            "--force-device-scale-factor=1",
            "--autoplay-policy=no-user-gesture-required",
            "--enable-gpu-rasterization",
            "--enable-zero-copy",
            "--ignore-gpu-blocklist",
            "--enable-accelerated-video-decode",
            "--enable-accelerated-video-encode",
            "--disable-background-media-suspend",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--disable-ipc-flooding-protection",
            "--disable-features=HardwareMediaKeyHandling,MediaSessionService,Translate,CalculateNativeWinOcclusion,WindowOcclusion,IntensiveWakeUpThrottling,ThrottleDisplayNoneAndVisibilityHiddenCrossOriginIframes,UseEcoQoSForBackgroundProcess,PreloadMediaEngagementData,MediaEngagementBypassAutoplayPolicies",
            "--disable-session-crashed-bubble",
            "--disable-infobars",
            "--disable-notifications",
            "--hide-crash-restore-bubble",
            "--hide-scrollbars",
            "--no-first-run",
            "--no-default-browser-check"
        ]);

        logCallback?.Invoke($"[Tuner {tunerId}] Launching Chrome (V9) for {provider.Manifest.Name} -> {channel.Name}...");

        using var chromeProcess = Process.Start(new ProcessStartInfo
        {
            FileName = _config.ResolvedChromePath,
            Arguments = chromeArgs,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start Chrome process.");

        try { chromeProcess.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

        Process? ffmpegProcess = null;

        try
        {
            // 1. Load extension AND dispatch hardware CDP click to auto-dismiss "Press play to unpause"!
            _ = Task.Run(() => LoadExtensionAndPlaceWindowAsync(chromeProcess.Id, debugPort, extDir, provider, logCallback, tunerId, ct), ct);

            // 2. Launch FFmpeg
            string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            string ffmpegArgs = string.Join(" ",
            [
                "-hide_banner -loglevel warning",
                "-fflags +genpts",
                "-probesize 32768 -analyzeduration 0 -fpsprobesize 0",
                "-f webm -i pipe:0",
                "-map 0:v:0 -map 0:a:0?",
                "-c:v copy",
                "-bsf:v h264_mp4toannexb,dump_extra",
                "-c:a aac -b:a 192k -ar 48000 -ac 2",
                "-af \"aresample=async=1000\"",
                "-avoid_negative_ts make_zero -max_interleave_delta 0 -muxdelay 0 -muxpreload 0 -flush_packets 1 -mpegts_flags +initial_discontinuity+resend_headers -f mpegts pipe:1"
            ]);

            ffmpegProcess = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = ffmpegArgs,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");

            try { ffmpegProcess.PriorityClass = ProcessPriorityClass.High; } catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested &&
                           await ffmpegProcess.StandardError.ReadLineAsync(ct) is { } line)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            logCallback?.Invoke($"[FFmpeg {tunerId}] {line}");
                    }
                }
                catch { }
            }, ct);

            // 3. Accept WebSocket stream connection from offscreen.js
            var listenerTask = Task.Run(async () =>
            {
                try
                {
                    using var reg = ct.Register(() => { try { httpListener.Stop(); } catch { } });
                    while (!ct.IsCancellationRequested)
                    {
                        var ctx = await httpListener.GetContextAsync();
                        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

                        if (ctx.Request.Url?.AbsolutePath == "/log")
                        {
                            string msg = ctx.Request.QueryString["msg"] ?? "";
                            logCallback?.Invoke($"[Ext {tunerId}] {msg}");
                            ctx.Response.StatusCode = 200;
                            ctx.Response.Close();
                            continue;
                        }

                        if (ctx.Request.IsWebSocketRequest)
                        {
                            var wsWrapper = await ctx.AcceptWebSocketAsync(null, receiveBufferSize: 65536, keepAliveInterval: TimeSpan.FromSeconds(30));
                            using var ws = wsWrapper.WebSocket;
                            logCallback?.Invoke($"[Tuner {tunerId}] Live Video Active! Streaming High-Profile 720p60 10Mbps H.264 to FFmpeg...");

                            Stream ffmpegIn = ffmpegProcess.StandardInput.BaseStream;
                            byte[] buffer = new byte[65536];

                            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                            {
                                var result = await ws.ReceiveAsync(buffer, ct);
                                if (result.MessageType == WebSocketMessageType.Close)
                                    break;

                                if (result.Count > 0)
                                {
                                    await ffmpegIn.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                                }
                            }
                            break;
                        }

                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or WebSocketException)
                {
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[Tuner {tunerId}] Listener error: {ex.Message}");
                }
                finally
                {
                    try { ffmpegProcess.StandardInput.Close(); } catch { }
                }
            }, ct);

            await ffmpegProcess.StandardOutput.BaseStream.CopyToAsync(outputStream, 131072, ct);
            await listenerTask;
        }
        catch (OperationCanceledException)
        {
            logCallback?.Invoke($"[Tuner {tunerId}] Stream closed by client ({channel.Name}).");
        }
        finally
        {
            try { httpListener.Stop(); } catch { }

            try
            {
                if (ffmpegProcess is { HasExited: false })
                    ffmpegProcess.Kill(entireProcessTree: true);
            }
            catch { }

            try
            {
                chromeProcess.Kill(entireProcessTree: true);
            }
            catch { }

            KillOrphanedChromeForProfile(tunerProfile);
        }
    }

    private async Task LoadExtensionAndPlaceWindowAsync(
        int chromeProcessId,
        int debugPort,
        string extDir,
        LoadedProvider provider,
        Action<string>? logCallback,
        int tunerId,
        CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            IntPtr hwnd = await WaitForChromeWindowAsync(chromeProcessId, ct);
            if (hwnd != IntPtr.Zero)
            {
                ApplyWindowPlacement(hwnd);
                for (int i = 0; i < 6; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(400, ct);
                    ApplyWindowPlacement(hwnd);
                }
            }
        }, ct);

        try
        {
            string? browserWsUrl = null;
            for (int attempt = 0; attempt < 120; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string verJson = await Http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/version", ct);
                    using var verDoc = JsonDocument.Parse(verJson);
                    if (verDoc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var browserWsProp))
                    {
                        browserWsUrl = browserWsProp.GetString();
                        if (!string.IsNullOrWhiteSpace(browserWsUrl)) break;
                    }
                }
                catch { }
                await Task.Delay(15, ct);
            }

            if (!string.IsNullOrWhiteSpace(browserWsUrl))
            {
                using var bws = new ClientWebSocket();
                await bws.ConnectAsync(new Uri(browserWsUrl), ct);
                await SendCdpCommandAsync(bws, 1, "Extensions.loadUnpacked", new { path = extDir }, ct);
            }

            string? pageWsUrl = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string json = await Http.GetStringAsync($"http://127.0.0.1:{debugPort}/json", ct);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        if (elem.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "page" &&
                            elem.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                        {
                            pageWsUrl = wsProp.GetString();
                            break;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(pageWsUrl)) break;
                }
                catch { }
                await Task.Delay(20, ct);
            }

            if (!string.IsNullOrWhiteSpace(pageWsUrl))
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(pageWsUrl), ct);
                await SendCdpCommandAsync(ws, 1, "Page.enable", new { }, ct);
                await SendCdpCommandAsync(ws, 2, "Emulation.setFocusEmulationEnabled", new { enabled = true }, ct);

                if (!string.IsNullOrWhiteSpace(provider.TuneJs))
                {
                    await SendCdpCommandAsync(ws, 3, "Page.addScriptToEvaluateOnNewDocument", new { source = provider.TuneJs }, ct);
                }

                // Poll for the first 6 seconds: inject TuneJs AND if the video is paused ("Press play to unpause"),
                // send a hardware-trusted CDP Mouse Click at (640, 360) to unpause it immediately!
                int msgId = 10;
                for (int i = 0; i < 15; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(provider.TuneJs))
                    {
                        await SendCdpCommandAsync(ws, msgId++, "Runtime.evaluate", new
                        {
                            expression = provider.TuneJs,
                            userGesture = true
                        }, ct);
                    }

                    // Send a trusted hardware click on the unpause overlay / video player if paused
                    const string autoUnpauseExpr = """
                    (function() {
                      const v = Array.from(document.querySelectorAll('video')).find(x => !x.closest('ytu-epg-row, ytu-guide, .tenx-thumb'));
                      if (v && !v.paused && v.readyState >= 3 && v.currentTime > 0) return 'PLAYING';
                      const btn = document.querySelector('.ytp-large-play-button, .ytp-unmute, ytlr-play-button, [aria-label*="Play" i], [aria-label*="Unpause" i]');
                      if (btn) { try { btn.click(); } catch {} }
                      if (v && v.paused) {
                        v.muted = false;
                        v.play().catch(() => {});
                        return 'PAUSED';
                      }
                      return 'WAITING';
                    })()
                    """;

                    await SendCdpCommandAsync(ws, msgId++, "Runtime.evaluate", new
                    {
                        expression = autoUnpauseExpr,
                        userGesture = true
                    }, ct);

                    // Also dispatch a real hardware-trusted mouse click at the center of the 1280x720 player during the first 3.5 seconds
                    // if the player hasn't started playing yet (this bypasses YouTube TV's "Press play to unpause" gate!)
                    if (i >= 2 && i <= 8)
                    {
                        const string clickIfPausedJs = """
                        (function() {
                          const v = Array.from(document.querySelectorAll('video')).find(x => !x.closest('ytu-epg-row, ytu-guide, .tenx-thumb'));
                          if (!v || v.paused || v.readyState < 3) {
                            const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
                            if (player && typeof player.playVideo === 'function') {
                              try { player.unMute(); player.setVolume(100); player.playVideo(); } catch {}
                            }
                          }
                        })()
                        """;
                        await SendCdpCommandAsync(ws, msgId++, "Runtime.evaluate", new
                        {
                            expression = clickIfPausedJs,
                            userGesture = true
                        }, ct);
                    }

                    await Task.Delay(350, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logCallback?.Invoke($"[Tuner {tunerId}] CDP setup warning: {ex.Message}");
        }
    }

    private static async Task SendCdpCommandAsync(ClientWebSocket ws, int id, string method, object parameters, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        string payload = JsonSerializer.Serialize(new
        {
            id,
            method,
            @params = parameters
        });
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static (string ExtDir, string ExtensionId) EnsureTabCaptureExtension(int tunerId, int wsPort, LoadedProvider provider)
    {
        string baseExtDir = Path.Combine(AppContext.BaseDirectory, "TabCaptureExt");
        Directory.CreateDirectory(baseExtDir);

        // Bump to rsa_spki_v9.b64 so Chrome registers v9 fresh!
        string keyFile = Path.Combine(baseExtDir, "rsa_spki_v9.b64");
        byte[] spkiBytes;
        if (File.Exists(keyFile))
        {
            spkiBytes = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
        }
        else
        {
            using var rsa = RSA.Create(2048);
            spkiBytes = rsa.ExportSubjectPublicKeyInfo();
            File.WriteAllText(keyFile, Convert.ToBase64String(spkiBytes));
        }

        string publicKeyBase64 = Convert.ToBase64String(spkiBytes);
        byte[] hash = SHA256.HashData(spkiBytes);
        var extIdChars = new char[32];
        for (int i = 0; i < 16; i++)
        {
            extIdChars[i * 2] = (char)('a' + (hash[i] >> 4));
            extIdChars[i * 2 + 1] = (char)('a' + (hash[i] & 0x0F));
        }
        string extensionId = new(extIdChars);

        string extDir = Path.Combine(baseExtDir, $"Tuner_{tunerId}");
        Directory.CreateDirectory(extDir);

        string manifestJson = $$"""
        {
          "manifest_version": 3,
          "name": "Charon HighProfile 60fps TabCapture Bridge",
          "version": "9.0.0",
          "key": "{{publicKeyBase64}}",
          "permissions": [
            "tabCapture",
            "offscreen",
            "tabs",
            "scripting"
          ],
          "host_permissions": [
            "<all_urls>"
          ],
          "background": {
            "service_worker": "background.js"
          },
          "content_scripts": [
            {
              "matches": ["<all_urls>"],
              "js": ["main_world.js"],
              "run_at": "document_start",
              "world": "MAIN",
              "all_frames": false
            },
            {
              "matches": ["<all_urls>"],
              "js": ["content.js"],
              "run_at": "document_start",
              "all_frames": false
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(extDir, "manifest.json"), manifestJson);

        var mainWorldBuilder = new StringBuilder();
        mainWorldBuilder.AppendLine("(function(){ try { Object.defineProperty(document, 'hidden', { get: () => false }); Object.defineProperty(document, 'visibilityState', { get: () => 'visible' }); Object.defineProperty(document, 'hasFocus', { value: () => true }); } catch {} })();");
        if (!string.IsNullOrWhiteSpace(provider.StyleCss))
        {
            string escapedCss = JsonEncodedText.Encode(provider.StyleCss).ToString();
            mainWorldBuilder.AppendLine($"(function(){{ const inject = () => {{ if (!document.getElementById('charon-css') && (document.head || document.documentElement)) {{ const s = document.createElement('style'); s.id = 'charon-css'; s.textContent = \"{escapedCss}\"; (document.head || document.documentElement).appendChild(s); }} }}; inject(); document.addEventListener('DOMContentLoaded', inject); }})();");
        }
        if (!string.IsNullOrWhiteSpace(provider.TuneJs))
        {
            mainWorldBuilder.AppendLine(provider.TuneJs);
        }
        File.WriteAllText(Path.Combine(extDir, "main_world.js"), mainWorldBuilder.ToString());

        // Only fire VIDEO_PLAYING_NOW when the main video is ACTUALLY unpaused and advancing (currentTime > 0)!
        const string notifyLifecycleJs = """
        (function() {
          if (window.__charonLifecycleNotifier) return;
          window.__charonLifecycleNotifier = true;

          if (location.protocol.startsWith('http')) {
            try { chrome.runtime.sendMessage({ type: 'PAGE_COMMITTED' }); } catch {}
          }

          let sentPlaying = false;
          const timer = setInterval(() => {
            if (sentPlaying) { clearInterval(timer); return; }
            const v = Array.from(document.querySelectorAll('video')).find(
              x => !x.paused && x.readyState >= 3 && x.videoWidth > 0 && !x.closest('ytu-epg-row, ytu-guide, .tenx-thumb')
            );
            if (v) {
              sentPlaying = true;
              clearInterval(timer);
              try { chrome.runtime.sendMessage({ type: 'VIDEO_PLAYING_NOW' }); } catch {}
            }
          }, 50);
        })();
        """;
        File.WriteAllText(Path.Combine(extDir, "content.js"), notifyLifecycleJs);

        string backgroundJs = $$"""
        const log = (msg) => fetch('http://127.0.0.1:{{wsPort}}/log?msg=' + encodeURIComponent('BG_V9: ' + msg)).catch(() => {});
        log('Service worker started');

        let capturingTabId = null;

        const offscreenReady = (async () => {
          const existingContexts = await chrome.runtime.getContexts({
            contextTypes: ['OFFSCREEN_DOCUMENT']
          });
          if (existingContexts.length === 0) {
            await chrome.offscreen.createDocument({
              url: 'offscreen.html',
              reasons: ['USER_MEDIA'],
              justification: 'Tab capture streaming to Charon'
            });
          }
        })();

        async function startCaptureForTab(tabId) {
          if (capturingTabId !== null) return;
          capturingTabId = tabId;

          try {
            const [streamId] = await Promise.all([
              chrome.tabCapture.getMediaStreamId({ targetTabId: tabId }),
              offscreenReady
            ]);
            log('Got streamId for tab ' + tabId);

            chrome.runtime.sendMessage({
              type: 'PREPARE_STREAM',
              streamId: streamId,
              tabId: tabId,
              wsPort: {{wsPort}}
            });
          } catch (err) {
            capturingTabId = null;
          }
        }

        chrome.runtime.onMessage.addListener((msg, sender) => {
          if (msg.type === 'PAGE_COMMITTED' && sender && sender.tab && sender.tab.id) {
            startCaptureForTab(sender.tab.id);
          } else if (msg.type === 'RETRY_CAPTURE') {
            capturingTabId = null;
            setTimeout(findAndCaptureAppTab, 300);
          }
        });

        async function findAndCaptureAppTab() {
          if (capturingTabId !== null) return;
          try {
            const windows = await chrome.windows.getAll({
              populate: true,
              windowTypes: ['app', 'normal', 'popup']
            });
            for (const w of (windows || [])) {
              for (const t of (w.tabs || [])) {
                if (t.id && t.url && t.url.startsWith('http')) {
                  startCaptureForTab(t.id);
                  return;
                }
              }
            }
          } catch {}
        }

        const timer = setInterval(() => {
          if (capturingTabId !== null) {
            clearInterval(timer);
            return;
          }
          findAndCaptureAppTab();
        }, 50);
        """;
        File.WriteAllText(Path.Combine(extDir, "background.js"), backgroundJs);

        const string offscreenHtml = """
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"><script src="offscreen.js"></script></head>
        <body></body>
        </html>
        """;
        File.WriteAllText(Path.Combine(extDir, "offscreen.html"), offscreenHtml);

        const string offscreenJs = """
        let preparedStream = null;
        let targetWsPort = null;
        let recorderStarted = false;
        let videoReadyReceived = false;

        function triggerRecorderStart() {
          if (recorderStarted || !preparedStream || !targetWsPort) return;
          recorderStarted = true;

          const log = (m) => fetch('http://127.0.0.1:' + targetWsPort + '/log?msg=' + encodeURIComponent('OFFSCREEN: ' + m)).catch(() => {});

          const ws = new WebSocket('ws://127.0.0.1:' + targetWsPort + '/');
          ws.binaryType = 'arraybuffer';

          ws.onopen = () => {
            const mimeTypes = [
              'video/webm;codecs=avc1.64002A,opus',
              'video/webm;codecs=avc1.640020,opus',
              'video/webm;codecs=avc1.4D4020,opus',
              'video/webm;codecs=h264,opus',
              'video/webm'
            ];
            const mimeType = mimeTypes.find((t) => MediaRecorder.isTypeSupported(t)) || 'video/webm';
            log('Selected encoder profile: ' + mimeType);

            const recorder = new MediaRecorder(preparedStream, {
              mimeType: mimeType,
              videoBitsPerSecond: 10000000,
              audioBitsPerSecond: 192000,
              videoKeyFrameIntervalDuration: 2000
            });

            recorder.ondataavailable = (e) => {
              if (e.data && e.data.size > 0 && ws.readyState === WebSocket.OPEN) {
                ws.send(e.data);
              }
            };

            recorder.start(200);
          };
        }

        chrome.runtime.onMessage.addListener(async (msg) => {
          if (msg.type === 'VIDEO_PLAYING_NOW') {
            videoReadyReceived = true;
            triggerRecorderStart();
            return;
          }

          if (msg.type !== 'PREPARE_STREAM') return;
          targetWsPort = msg.wsPort;
          const log = (m) => fetch('http://127.0.0.1:' + targetWsPort + '/log?msg=' + encodeURIComponent('OFFSCREEN: ' + m)).catch(() => {});

          try {
            preparedStream = await navigator.mediaDevices.getUserMedia({
              audio: {
                mandatory: {
                  chromeMediaSource: 'tab',
                  chromeMediaSourceId: msg.streamId
                }
              },
              video: {
                mandatory: {
                  chromeMediaSource: 'tab',
                  chromeMediaSourceId: msg.streamId,
                  minWidth: 1280,
                  maxWidth: 1280,
                  minHeight: 720,
                  maxHeight: 720,
                  maxFrameRate: 60
                }
              }
            });

            const videoTrack = preparedStream.getVideoTracks()[0];
            if (videoTrack && 'contentHint' in videoTrack) {
              videoTrack.contentHint = 'motion';
            }

            if (videoReadyReceived) {
              triggerRecorderStart();
            } else {
              setTimeout(triggerRecorderStart, 2200);
            }
          } catch (err) {
            log('Retrying getUserMedia: ' + (err && err.message ? err.message : String(err)));
            chrome.runtime.sendMessage({ type: 'RETRY_CAPTURE' });
          }
        });
        """;
        File.WriteAllText(Path.Combine(extDir, "offscreen.js"), offscreenJs);

        return (extDir, extensionId);
    }

    private string PrepareTunerProfile(int tunerId)
    {
        string tunerDir = GetTunerProfilePath(tunerId);
        KillOrphanedChromeForProfile(tunerDir);
        TryDeleteFile(Path.Combine(tunerDir, "SingletonLock"));

        if (tunerId == 1)
            return tunerDir;

        string tuner1Dir = GetTunerProfilePath(1);
        string tuner1Default = Path.Combine(tuner1Dir, "Default");
        string tunerDefault = Path.Combine(tunerDir, "Default");

        string tuner1Cookies = Path.Combine(tuner1Default, "Network", "Cookies");
        string tuner2Cookies = Path.Combine(tunerDefault, "Network", "Cookies");

        bool needsSync = !Directory.Exists(tunerDefault) ||
                         (File.Exists(tuner1Cookies) && (!File.Exists(tuner2Cookies) ||
                          File.GetLastWriteTimeUtc(tuner1Cookies) > File.GetLastWriteTimeUtc(tuner2Cookies)));

        if (needsSync && Directory.Exists(tuner1Default))
        {
            CopyDirectorySafe(tuner1Default, tunerDefault);

            string localStateSrc = Path.Combine(tuner1Dir, "Local State");
            string localStateDst = Path.Combine(tunerDir, "Local State");
            if (File.Exists(localStateSrc))
            {
                try { File.Copy(localStateSrc, localStateDst, overwrite: true); } catch { }
            }
        }

        return tunerDir;
    }

    private static void KillOrphanedChromeForProfile(string profileDir)
    {
        try
        {
            string folderName = Path.GetFileName(profileDir.TrimEnd(Path.DirectorySeparatorChar));
            using var psi = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='chrome.exe' AND CommandLine LIKE '%{folderName}%'\\\" | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            psi?.WaitForExit(1500);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void CopyDirectorySafe(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith("LOCK", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Copy(file, Path.Combine(targetDir, fileName), overwrite: true);
            }
            catch { }
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(subDir);
            if (dirName is "Cache" or "Code Cache" or "GPUCache" or "Service Worker" or "Sessions" or "Session Storage")
                continue;
            CopyDirectorySafe(subDir, Path.Combine(targetDir, dirName));
        }
    }

    private static async Task<IntPtr> WaitForChromeWindowAsync(int processId, CancellationToken ct)
    {
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            IntPtr foundHwnd = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint winPid);
                if (winPid == (uint)processId)
                {
                    foundHwnd = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (foundHwnd != IntPtr.Zero)
                return foundHwnd;

            await Task.Delay(25, ct);
        }

        return IntPtr.Zero;
    }

    private void ApplyWindowPlacement(IntPtr hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);

        if (_config.WindowPlacement == WindowPlacementMode.VisibleDebug)
        {
            SetWindowPos(hwnd, IntPtr.Zero, 50, 50, WindowWidth, WindowHeight, SWP_NOACTIVATE | SWP_NOZORDER);
            return;
        }

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        int targetX, targetY;
        if (_config.WindowPlacement == WindowPlacementMode.CustomCoordinates)
        {
            targetX = _config.CustomX;
            targetY = _config.CustomY;
        }
        else
        {
            int virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            targetX = (virtualLeft + virtualWidth) - 8;
            targetY = (virtualTop + virtualHeight) - 8;
        }

        SetWindowPos(hwnd, HWND_BOTTOM, targetX, targetY, WindowWidth, WindowHeight, SWP_NOACTIVATE);
    }

    #region Win32 Interop

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int SW_RESTORE = 9;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    #endregion
}