using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Charon.Tuner;

public sealed class ProcessAudioTap : IAsyncDisposable
{
    public string PipeName { get; }
    public string FullPipePath => $@"\\.\pipe\{PipeName}";

    private readonly uint _targetProcessId;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pumpTask;

    public ProcessAudioTap(int chromeProcessId, int tunerId)
    {
        _targetProcessId = (uint)chromeProcessId;
        PipeName = $"charon_audio_tuner_{tunerId}_{Guid.NewGuid():N}";
    }

    public void Start()
    {
        _pumpTask = Task.Run(() => RunAudioPumpAsync(_cts.Token));
    }

    private async Task RunAudioPumpAsync(CancellationToken ct)
    {
        timeBeginPeriod(1);

        await using var pipeServer = new NamedPipeServerStream(
            PipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 65536);

        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr waveFormatPtr = IntPtr.Zero;

        // 1. Activate WASAPI Process Loopback BEFORE FFmpeg connects so there is zero delay on connection!
        try
        {
            audioClient = ActivateProcessLoopback(_targetProcessId);
            if (audioClient != null)
            {
                var format = new WAVEFORMATEX
                {
                    wFormatTag = 1, // WAVE_FORMAT_PCM
                    nChannels = 2,
                    nSamplesPerSec = 48000,
                    wBitsPerSample = 16,
                    nBlockAlign = 4,
                    nAvgBytesPerSec = 48000 * 4,
                    cbSize = 0
                };

                waveFormatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
                Marshal.StructureToPtr(format, waveFormatPtr, false);

                uint streamFlags = 0x88020000;
                int hr = audioClient.Initialize(0, streamFlags, 200_000, 0, waveFormatPtr, IntPtr.Zero);
                if (hr == 0)
                {
                    Guid captureClientGuid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                    audioClient.GetService(ref captureClientGuid, out object captureObj);
                    captureClient = (IAudioCaptureClient)captureObj;
                    audioClient.Start();
                }
            }
        }
        catch
        {
            captureClient = null;
        }

        // Clock-locked 10ms chunks (1920 bytes = 480 stereo samples @ 48kHz)
        const int bytesPerSecond = 192000;
        const int chunkBytes = 1920;
        byte[] silence10Ms = new byte[chunkBytes];
        byte[] captureTemp = new byte[32768];

        try
        {
            // 2. Wait for FFmpeg to open the audio pipe
            await pipeServer.WaitForConnectionAsync(ct);

            // 3. Immediately write 40ms (7680 bytes) of PCM silence the instant FFmpeg connects!
            // This satisfies FFmpeg's s16le probesize in <1ms so video buffers NEVER queue up waiting for audio!
            for (int i = 0; i < 4; i++)
            {
                await pipeServer.WriteAsync(silence10Ms, ct);
            }

            var sw = Stopwatch.StartNew();
            long totalBytesWritten = 0;

            // Drain any stale pre-connection packets
            if (captureClient != null)
            {
                while (captureClient.GetNextPacketSize(out uint staleSize) == 0 && staleSize > 0)
                {
                    if (captureClient.GetBuffer(out _, out uint staleFrames, out _, out _, out _) == 0)
                        captureClient.ReleaseBuffer(staleFrames);
                    else
                        break;
                }
            }

            while (!ct.IsCancellationRequested)
            {
                if (captureClient != null)
                {
                    while (captureClient.GetNextPacketSize(out uint packetSize) == 0 && packetSize > 0)
                    {
                        if (captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _) == 0)
                        {
                            int byteCount = (int)(numFrames * 4);
                            bool isSilent = (flags & 0x2) != 0;

                            if (byteCount > 0)
                            {
                                int toCopy = Math.Min(byteCount, captureTemp.Length);
                                if (isSilent)
                                    Array.Clear(captureTemp, 0, toCopy);
                                else
                                    Marshal.Copy(dataPtr, captureTemp, 0, toCopy);

                                captureClient.ReleaseBuffer(numFrames);

                                await pipeServer.WriteAsync(captureTemp.AsMemory(0, toCopy), ct);
                                totalBytesWritten += toCopy;
                            }
                            else
                            {
                                captureClient.ReleaseBuffer(numFrames);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // Keep totalBytesWritten locked to wall-clock time within 10ms!
                long expectedBytes = (long)(sw.Elapsed.TotalSeconds * bytesPerSecond);
                long deficit = expectedBytes - totalBytesWritten;

                while (deficit >= chunkBytes)
                {
                    await pipeServer.WriteAsync(silence10Ms, ct);
                    totalBytesWritten += chunkBytes;
                    deficit = expectedBytes - totalBytesWritten;
                }

                await Task.Delay(4, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            timeEndPeriod(1);
            try { audioClient?.Stop(); } catch { }
            if (waveFormatPtr != IntPtr.Zero) Marshal.FreeHGlobal(waveFormatPtr);
        }
    }

    private static IAudioClient? ActivateProcessLoopback(uint processId)
    {
        var loopbackParams = new AUDIOCLIENT_ActivationParams
        {
            ActivationType = 1,
            ProcessId = processId,
            ProcessLoopbackMode = 0
        };

        int size = Marshal.SizeOf<AUDIOCLIENT_ActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(loopbackParams, paramsPtr, false);
            var propVariant = new PROPVARIANT
            {
                vt = 65,
                cbSize = (uint)size,
                pBlobData = paramsPtr
            };

            var completionHandler = new ActivateAudioCompletionHandler();
            Guid iidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

            int hr = ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback",
                ref iidAudioClient,
                ref propVariant,
                completionHandler,
                out _);

            if (hr != 0) return null;
            return completionHandler.WaitForClient(1500);
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_pumpTask != null)
        {
            try { await _pumpTask; } catch { }
        }
        _cts.Dispose();
    }

    #region Windows 11 WASAPI & Timer Interop

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IntPtr activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ActivationParams
    {
        public int ActivationType;
        public uint ProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        int GetBufferSize(out uint numBufferFrames);
        int GetStreamLatency(out long phnsLatency);
        int GetCurrentPadding(out uint numPaddingFrames);
        int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        int GetMixFormat(out IntPtr ppDeviceFormat);
        int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        int ReleaseBuffer(uint numFramesRead);
        int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    private sealed class ActivateAudioCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<IAudioClient?> _tcs = new();

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object unk);
                _tcs.TrySetResult(hr == 0 ? (IAudioClient)unk : null);
            }
            catch
            {
                _tcs.TrySetResult(null);
            }
        }

        public IAudioClient? WaitForClient(int timeoutMs)
        {
            return _tcs.Task.Wait(timeoutMs) ? _tcs.Task.Result : null;
        }
    }

    #endregion
}