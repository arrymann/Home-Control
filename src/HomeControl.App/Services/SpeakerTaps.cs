using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeControl.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace HomeControl.Services;

/// <summary>
/// Follows what the PC plays on each of its speaker devices (WASAPI loopback, shared mode), so
/// that the clap detector can tell the speakers' claps from the user's. Nothing is played,
/// recorded or kept: like the microphone, each loopback packet is read in place (a mixed-down
/// piece lives in a small stack buffer that is wiped) and only the times of sharp sounds remain
/// in <see cref="Sounds"/>.
/// </summary>
/// <remarks>
/// <para>
/// Threading: the loopback streams have no thread of their own. The microphone's capture thread
/// calls <see cref="Drain"/> before analysing each microphone packet, so everything the speakers
/// have played up to that moment is analysed first and the detector, the speaker streams and
/// <see cref="Sounds"/> are only ever used on that one thread. <see cref="Reopen"/> and
/// <see cref="Dispose"/> (the microphone's loop thread, or this class's retry timer) open the
/// new streams first and swap them in under the lock <see cref="Drain"/> holds, so a stream is
/// never closed while it is being read and the capture thread never waits for COM calls.
/// </para>
/// <para>
/// Nothing playing on a device means its loopback stream delivers no packets (Windows only
/// copies what its audio engine mixes) - that is what "nothing playing" looks like here, and
/// why the streams are polled rather than waited on.
/// </para>
/// </remarks>
internal sealed class SpeakerTaps : IDisposable
{
    private const long BufferDuration = 10_000_000; // 1 s (100 ns units): read every ~100 ms, so never overflows
    private const int RetryMilliseconds = 5000;

    // PKEY_Device_EnumeratorName: the bus the device is on ("BTHENUM", "USB", "HDAUDIO", ...). UNVERIFIED that endpoints carry it.
    private static readonly PropertyKey EnumeratorName = new(new Guid(unchecked((int)0xa45c254e), unchecked((short)0xdf1c), 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 24);

    private readonly object _gate = new();      // the taps array: Drain (capture thread) and the swap
    private readonly object _reopening = new(); // one Reopen or Dispose at a time (loop thread, retry timer)
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Timer _retry;
    private Tap[] _taps = [];
    private bool _retryArmed; // under _gate: a failed stream already asked for a Reopen
    private volatile bool _disposed;

    public SpeakerTaps()
    {
        _retry = new Timer(_ => Retry(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>What the detector checks claps against (<see cref="ClapDetector.Speakers"/>).</summary>
    public SpeakerSounds Sounds { get; } = new();

    /// <summary>
    /// <see cref="SpeakerSounds.PlayingUnseen"/> changed (on the capture thread, the loop thread
    /// or the retry timer): claps stopped or started counting again.
    /// </summary>
    public event Action? UnseenChanged;

    /// <summary>For the status line: the devices followed, and any that can't be.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Opens a loopback stream on every speaker device that's plugged in (again: after a device
    /// was added, removed or changed). Headphones and headsets are left out: the room doesn't
    /// hear them. Slow (COM calls): call it off the capture thread. The new streams are opened
    /// before the lock is taken, so the capture thread only ever waits for the swap.
    /// </summary>
    public void Reopen()
    {
        lock (_reopening)
        {
            if (_disposed)
            {
                return;
            }

            var taps = new List<Tap>();
            var notes = new List<string>();
            var inUse = false;
            try
            {
                using var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (var i = 0; i < devices.Count; i++)
                {
                    Open(devices[i], taps, notes, ref inUse);
                }
            }
            catch (Exception ex)
            {
                // E.g. the audio service is restarting: the streams opened so far are kept.
                Log.Info($"Listing speakers: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }

            Tap[] old;
            lock (_gate)
            {
                old = _taps;
                _taps = taps.ToArray();
                _retryArmed = false;
                if (_disposed)
                {
                    (old, _taps) = (_taps, []);
                }

                // Fail closed: an exclusive-mode app may be playing anything.
                SetUnseen(inUse && !_disposed);
                Description = string.Join(", ", notes);

                // Under the lock, so a stream that fails right after the swap still gets its retry.
                if (!_disposed)
                {
                    _retry.Change(inUse ? RetryMilliseconds : Timeout.Infinite, Timeout.Infinite);
                }
            }

            // No Drain can be reading these any more: it only reads the array under the lock.
            foreach (var tap in old)
            {
                tap.Dispose();
            }
        }
    }

    /// <summary>Opens one device's stream into <paramref name="taps"/>, or notes why not; disposes the device unless kept.</summary>
    private void Open(MMDevice device, List<Tap> taps, List<string> notes, ref bool inUse)
    {
        var name = NameOf(device);
        var keep = false;
        try
        {
            var (audible, maxDelay) = Classify(device);
            if (!audible)
            {
                return;
            }

            taps.Add(new Tap(device, Sounds, maxDelay));
            keep = true;
            notes.Add(name);
        }
        catch (COMException ex) when (ex.HResult == AudioClientErrorCode.DeviceInUse)
        {
            // Another app plays on it in exclusive mode, out of the engine's (and our) sight
            // (documented for IAudioClient::Initialize; what an already open loopback stream
            // sees then is UNVERIFIED: an error, or simply no packets).
            inUse = true;
            notes.Add($"{name} (used exclusively by another app)");
        }
        catch (Exception ex)
        {
            Log.Info($"Following “{name}”: {ex.GetType().Name} 0x{ex.HResult:X8}");
            notes.Add($"{name} (can't be followed)");
        }
        finally
        {
            if (!keep)
            {
                device.Dispose();
            }
        }
    }

    private static string NameOf(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (COMException)
        {
            return "a speaker"; // unplugged while being looked at
        }
    }

    /// <summary>The retry timer (a thread-pool thread): nothing may escape it.</summary>
    private void Retry()
    {
        try
        {
            Reopen();
        }
        catch (Exception ex)
        {
            Log.Info($"Following the speakers again: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    /// <summary>
    /// The microphone's capture thread, before each microphone packet: analyses everything the
    /// speakers have played since the last call. Allocates nothing.
    /// </summary>
    public void Drain()
    {
        lock (_gate)
        {
            var failed = false;
            foreach (var tap in _taps)
            {
                failed |= !tap.Drain();
            }

            if (failed && !_retryArmed && !_disposed)
            {
                // A device went away or was taken exclusively: no claps until it's looked at again (off
                // this thread, once: the failed stream keeps failing until Reopen swaps it out).
                _retryArmed = true;
                SetUnseen(true);
                _retry.Change(500, Timeout.Infinite);
            }
        }
    }

    private void SetUnseen(bool unseen)
    {
        if (Sounds.PlayingUnseen != unseen)
        {
            Sounds.PlayingUnseen = unseen;
            UnseenChanged?.Invoke();
        }
    }

    /// <summary>Closes the streams; waits for a <see cref="Drain"/> in progress.</summary>
    public void Dispose()
    {
        lock (_reopening)
        {
            Tap[] old;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                old = _taps;
                _taps = [];
            }

            _retry.Dispose();
            foreach (var tap in old)
            {
                tap.Dispose();
            }

            Sounds.Reset(); // the detector is no longer used (its capture thread has ended)
            _enumerator.Dispose();
        }
    }

    /// <summary>Whether the room hears the device, and how late its sound may arrive at the microphone.</summary>
    private static (bool Audible, double MaxDelay) Classify(MMDevice device)
    {
        var formFactor = device.Properties.TryGetValue<uint>(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out var value) ? value : 10u;
        switch (formFactor)
        {
            case 3: // Headphones
            case 5: // Headset
            case 6: // Handset
                return (false, 0);
        }

        if (IsBluetooth(device))
        {
            return (true, SpeakerStream.BluetoothMaxDelay);
        }

        return formFactor switch
        {
            0 => (true, SpeakerStream.BluetoothMaxDelay), // RemoteNetworkDevice
            7 or 8 or 9 => (true, SpeakerStream.DisplayMaxDelay), // digital passthrough, S/PDIF, HDMI/DisplayPort
            _ => (true, SpeakerStream.WiredMaxDelay),
        };
    }

    /// <summary>Best effort (UNVERIFIED on real Bluetooth devices): the bus, or the KS filter the endpoint is wired to.</summary>
    private static bool IsBluetooth(MMDevice device)
    {
        try
        {
            if (device.Properties.TryGetValue<string>(EnumeratorName, out var bus) && bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var topology = device.DeviceTopology;
            if (topology.ConnectorCount > 0)
            {
                var filter = topology.GetConnector(0).ConnectedToDeviceId ?? string.Empty;
                return filter.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                       || filter.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                       || filter.Contains("BTHLE", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }

        return false;
    }

    /// <summary>One device's loopback stream, read on the microphone's capture thread.</summary>
    private sealed class Tap : IDisposable
    {
        private readonly MMDevice _device;
        private readonly AudioClient _client;
        private readonly AudioCaptureClient _capture;
        private readonly SpeakerStream _stream;
        private readonly PacketClock _clock;
        private readonly AudioEndpointVolume? _volume;
        private readonly bool _isFloat;
        private readonly int _channels;
        private readonly int _blockAlign;
        private volatile bool _silenced;
        private bool _failed;

        public Tap(MMDevice device, SpeakerSounds sounds, double maxDelay)
        {
            _device = device;
            _client = device.CreateAudioClient();
            try
            {
                // The engine's mix format (almost always 32-bit float, 2-8 channels at 44.1 or 48 kHz): no conversion asked for.
                var format = _client.MixFormat;
                _isFloat = format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (format is WaveFormatExtensible e && e.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT));
                var isPcm16 = format.BitsPerSample == 16 && (format.Encoding == WaveFormatEncoding.Pcm ||
                              (format is WaveFormatExtensible p && p.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM));
                if (!_isFloat && !isPcm16)
                {
                    throw new NotSupportedException($"{format.Encoding} {format.BitsPerSample}-bit");
                }

                _channels = Math.Max(1, format.Channels);
                _blockAlign = format.BlockAlign;

                // Loopback, shared mode (the only mode loopback exists in), polled: no EventCallback, no thread.
                _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, BufferDuration, 0, format, Guid.Empty);
                _capture = _client.AudioCaptureClient;
                _stream = new SpeakerStream(sounds, format.SampleRate, maxDelay);
                _clock = new PacketClock(format.SampleRate);
                _client.Start();
            }
            catch
            {
                _client.Dispose();
                throw;
            }

            // Muted or at zero, the room hears nothing. UNVERIFIED: whether the loopback stream is silent then anyway
            // (software volume is applied before the loopback point; hardware volume and mute may not be).
            try
            {
                _volume = device.AudioEndpointVolume;
                _silenced = _volume.Mute || _volume.MasterVolumeLevelScalar <= 0.001f;
                _volume.OnVolumeNotification += OnVolume;
            }
            catch (COMException)
            {
                _volume = null;
            }
        }

        /// <summary>Reads every packet waiting. False: the stream failed (device gone, or taken exclusively).</summary>
        public bool Drain()
        {
            if (_failed)
            {
                return false;
            }

            try
            {
                while (_capture.GetNextPacketSize() > 0)
                {
                    using var lease = _capture.GetBufferLease(_blockAlign);
                    var frames = lease.FrameCount;
                    if (frames == 0)
                    {
                        continue;
                    }

                    var time = _clock.TimeOf(lease.DevicePosition, lease.QPCPosition, lease.Flags, frames);
                    if (_silenced || (lease.Flags & (AudioClientBufferFlags.Silent | AudioClientBufferFlags.DataDiscontinuity)) != 0)
                    {
                        _stream.Gap(); // silence, or sound lost: what comes next rises from nothing
                        if (_silenced || (lease.Flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            continue;
                        }
                    }

                    MixDown(lease.Buffer, time);
                }

                return true;
            }
            catch (COMException)
            {
                _failed = true; // AUDCLNT_E_DEVICE_INVALIDATED, AUDCLNT_E_DEVICE_IN_USE, ...: for good
                return false;
            }
        }

        public void Dispose()
        {
            if (_volume is not null)
            {
                _volume.OnVolumeNotification -= OnVolume;
            }

            try
            {
                _client.Stop();
            }
            catch (COMException)
            {
                // Already gone.
            }

            _stream.Reset();
            _client.Dispose();
            _device.Dispose();
        }

        private void OnVolume(AudioVolumeNotificationData data) => _silenced = data.Muted || data.MasterVolume <= 0.001f;

        /// <summary>Averages the channels into a small stack buffer, a piece at a time, and wipes it.</summary>
        private void MixDown(ReadOnlySpan<byte> buffer, double time)
        {
            Span<float> mono = stackalloc float[256];
            try
            {
                var floats = _isFloat ? MemoryMarshal.Cast<byte, float>(buffer) : default;
                var shorts = _isFloat ? default : MemoryMarshal.Cast<byte, short>(buffer);
                var frames = (_isFloat ? floats.Length : shorts.Length) / _channels;
                var rate = (double)_stream.SampleRate;
                for (var start = 0; start < frames; start += mono.Length)
                {
                    var count = Math.Min(mono.Length, frames - start);
                    for (var i = 0; i < count; i++)
                    {
                        var first = (start + i) * _channels;
                        var sum = 0f;
                        for (var c = 0; c < _channels; c++)
                        {
                            sum += _isFloat ? floats[first + c] : shorts[first + c] / 32768f;
                        }

                        mono[i] = sum / _channels;
                    }

                    _stream.Process(mono[..count], time + start / rate);
                }
            }
            finally
            {
                mono.Clear();
            }
        }
    }
}

/// <summary>
/// When a capture packet's first frame was recorded, in seconds on the QPC clock both streams
/// share: the packet's own QPC position when Windows gives a sane one, else counted on from the
/// last sane one by device position, else (last resort) when the packet was read.
/// </summary>
internal sealed class PacketClock(int sampleRate)
{
    private double _anchorTime = double.NaN;
    private long _anchorPosition;

    public double TimeOf(long devicePosition, long qpcPosition, AudioClientBufferFlags flags, int frames)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; // QPC seconds, like qpcPosition / 10^7
        var stamped = qpcPosition * 1e-7;
        if ((flags & AudioClientBufferFlags.TimestampError) == 0 && qpcPosition > 0 && stamped <= now + 0.01 && stamped >= now - 1.5)
        {
            _anchorTime = stamped;
            _anchorPosition = devicePosition;
            return stamped;
        }

        var counted = devicePosition - _anchorPosition;
        if (!double.IsNaN(_anchorTime) && counted >= 0 && counted < 60L * sampleRate)
        {
            return _anchorTime + counted / (double)sampleRate;
        }

        return now - frames / (double)sampleRate;
    }
}
