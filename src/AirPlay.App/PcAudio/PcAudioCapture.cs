using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace AirPlay.App.PcAudio;

/// <summary>
/// Captures what the PC is playing as 48 kHz 16-bit stereo PCM.
///   Preferred: Windows 10 2004+ "process loopback" with this program EXCLUDED, so the iPhone's own
///   sound (which we play on the PC while mirroring) isn't sent back to it (no echo loop).
///   Fallback: ordinary loopback of the default output device (everything, including us).
/// </summary>
public sealed class PcAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly Action<string> _log;
    private IDisposable? _active;

    /// <summary>PCM chunk (16-bit stereo interleaved); the buffer is reused after the call.</summary>
    public event Action<ReadOnlyMemory<byte>>? Data;

    /// <summary>True when this program's own sound is excluded from the capture.</summary>
    public bool ExcludesSelf { get; private set; }

    public PcAudioCapture(Action<string> log) => _log = log;

    public async Task StartAsync()
    {
        try
        {
            _active = await ProcessLoopback.StartAsync(Environment.ProcessId, bytes => Data?.Invoke(bytes));
            ExcludesSelf = true;
            _log("電腦聲音擷取：已排除本程式自己的聲音（不會回音）");
        }
        catch (Exception ex)
        {
            _log($"電腦聲音擷取：不支援排除本程式（{ex.Message}），改擷取全部聲音");
            _active = SystemLoopback.Start(bytes => Data?.Invoke(bytes));
            ExcludesSelf = false;
        }
    }

    public void Dispose()
    {
        _active?.Dispose();
        _active = null;
    }

    // ---- process loopback (exclude this process) ---------------------------------------

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>Completion callback for ActivateAudioInterfaceAsync; must be agile (free-threaded).</summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            activateOperation.GetActivateResult(out var hr, out var activated);
            if (hr < 0) Result.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("activation failed", hr));
            else Result.TrySetResult((IAudioClient)activated);
        }
    }

    private sealed class ProcessLoopback : IDisposable
    {
        private const string VirtualDevice = @"VAD\Process_Loopback";
        private static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        private readonly AudioClient _client;
        private readonly AutoResetEvent _ready = new(false);
        private readonly Thread _thread;
        private volatile bool _stop;

        private ProcessLoopback(AudioClient client, Action<ReadOnlyMemory<byte>> onData)
        {
            _client = client;
            _thread = new Thread(() => Loop(onData)) { IsBackground = true, Name = "PcAudioCapture" };
        }

        public static async Task<ProcessLoopback> StartAsync(int excludedProcessId, Action<ReadOnlyMemory<byte>> onData)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK(1), { pid, EXCLUDE_TARGET_PROCESS_TREE(1) } }
            var parameters = Marshal.AllocHGlobal(12);
            // PROPVARIANT { vt = VT_BLOB(65), blob = { cbSize = 12, pBlobData } } (24 bytes on x64)
            var propVariant = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(parameters, 0, 1);
                Marshal.WriteInt32(parameters, 4, excludedProcessId);
                Marshal.WriteInt32(parameters, 8, 1);
                for (var i = 0; i < 24; i += 8) Marshal.WriteInt64(propVariant, i, 0);
                Marshal.WriteInt16(propVariant, 0, 65);
                Marshal.WriteInt32(propVariant, 8, 12);
                Marshal.WriteIntPtr(propVariant, 16, parameters);

                var handler = new ActivationHandler();
                ActivateAudioInterfaceAsync(VirtualDevice, IAudioClientId, propVariant, handler, out _);
                var audioClient = await handler.Result.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var client = new AudioClient(audioClient);
                // Process loopback has no mix format of its own: ask for exactly what we want.
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                    200_000, 0, new WaveFormat(SampleRate, 16, Channels), Guid.Empty); // 20 ms buffer
                var capture = new ProcessLoopback(client, onData);
                client.SetEventHandle(capture._ready.SafeWaitHandle.DangerousGetHandle());
                client.Start();
                capture._thread.Start();
                return capture;
            }
            finally
            {
                Marshal.FreeHGlobal(propVariant);
                Marshal.FreeHGlobal(parameters);
            }
        }

        private void Loop(Action<ReadOnlyMemory<byte>> onData)
        {
            var capture = _client.AudioCaptureClient;
            var buffer = new byte[48000 * 4];
            while (!_stop)
            {
                if (!_ready.WaitOne(100)) continue;
                while (!_stop && capture.GetNextPacketSize() > 0)
                {
                    var data = capture.GetBuffer(out var frames, out var flags);
                    var bytes = frames * Channels * 2;
                    if (bytes > buffer.Length) buffer = new byte[bytes];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    capture.ReleaseBuffer(frames);
                    onData(buffer.AsMemory(0, bytes));
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(500);
            try { _client.Stop(); } catch (COMException) { }
            _client.Dispose();
            _ready.Dispose();
        }
    }

    // ---- fallback: everything the default device plays ---------------------------------

    private sealed class SystemLoopback : IDisposable
    {
        private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        private readonly WasapiLoopbackCapture _capture = new();
        private byte[] _out = [];

        public static SystemLoopback Start(Action<ReadOnlyMemory<byte>> onData)
        {
            var s = new SystemLoopback();
            s._capture.DataAvailable += (_, e) => onData(s.Convert(e.Buffer, e.BytesRecorded));
            s._capture.StartRecording();
            return s;
        }

        /// <summary>Device mix format (usually 48 kHz float, n channels) → 16-bit stereo.</summary>
        private ReadOnlyMemory<byte> Convert(byte[] data, int count)
        {
            var format = _capture.WaveFormat;
            var channels = format.Channels;
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                          (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
            var bytesPerSample = format.BitsPerSample / 8;
            var frames = count / (bytesPerSample * channels);
            if (_out.Length < frames * 4) _out = new byte[frames * 4];
            for (var f = 0; f < frames; f++)
                for (var c = 0; c < 2; c++)
                {
                    var src = (f * channels + Math.Min(c, channels - 1)) * bytesPerSample;
                    var v = isFloat ? BitConverter.ToSingle(data, src)
                        : bytesPerSample == 2 ? BitConverter.ToInt16(data, src) / 32768f
                        : BitConverter.ToInt32(data, src) / 2147483648f;
                    var s = (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue);
                    _out[(f * 2 + c) * 2] = (byte)s;
                    _out[(f * 2 + c) * 2 + 1] = (byte)(s >> 8);
                }
            return _out.AsMemory(0, frames * 4);
        }

        public void Dispose()
        {
            _capture.StopRecording();
            _capture.Dispose();
        }
    }
}
