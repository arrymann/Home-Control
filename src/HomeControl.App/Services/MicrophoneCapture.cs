using System.Runtime.InteropServices;
using System.Threading.Channels;
using HomeControl.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace HomeControl.Services;

/// <summary>What the microphone is doing.</summary>
internal enum MicrophoneState
{
    Off,
    Starting,
    Listening,

    /// <summary>Windows' privacy settings don't let desktop apps use the microphone.</summary>
    Blocked,
    NoMicrophone,

    /// <summary>It failed (unplugged, used by another app…) and will be tried again.</summary>
    Retrying,

    /// <summary>Not opened: the session is used over Remote Desktop, where the microphone would be the remote one.</summary>
    RemoteSession,
}

/// <summary>
/// Opens the microphone (WASAPI, shared mode) and feeds it to a <see cref="ClapDetector"/>.
/// </summary>
/// <remarks>
/// <para>
/// No sound is kept. Windows hands over each packet in its own capture buffer; the detector
/// reads it in place (a mixed-down copy, when one is needed, lives in a small stack buffer that
/// is wiped) and the buffer goes back to Windows when the callback returns. Nothing is written
/// to disk, logged or sent; only clap counts leave this class.
/// </para>
/// <para>
/// All device work happens on one background loop, one step at a time; callbacks from Windows
/// only queue work. It follows the default recording device (or uses the chosen one), and
/// recovers from unplugging, sleep and other apps taking the device, with back-off.
/// </para>
/// </remarks>
internal sealed class MicrophoneCapture : IDisposable
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int SmRemoteSession = 0x1000;
    private const int RequestedSampleRate = 48000;
    private static readonly int[] RetrySeconds = [1, 2, 5, 10, 30, 60];

    private readonly Action<int> _patternDetected;
    private readonly Action _clapHeard;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private readonly Timer _watchdog;

    // Read on the capture thread; set before a recorder starts (or swapped atomically).
    private volatile ClapDetector? _detector;
    private volatile int _sensitivity = (int)ClapSensitivity.Medium;
    private volatile int[] _counts = [];
    private bool _isFloat;
    private int _channels;
    private long _lastPacket;
    private bool _reportedError;
    private Exception? _stopError;

    // Loop-only state.
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private MMDevice? _device;
    private WasapiRecorder? _recorder;
    private int _generation;
    private bool _wanted;
    private string? _deviceId;
    private int _failures;
    private long _startedAt;
    private CancellationTokenSource? _retry;
    private MicrophoneState _reported = MicrophoneState.Off;

    /// <param name="patternDetected">Claps in a row; called on the capture thread.</param>
    /// <param name="clapHeard">A single clap; called on the capture thread.</param>
    public MicrophoneCapture(Action<int> patternDetected, Action clapHeard)
    {
        _patternDetected = patternDetected;
        _clapHeard = clapHeard;
        _loop = Task.Run(RunLoopAsync);
        _watchdog = new Timer(_ => Post(CheckStalled), null, 2000, 2000);
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action<MicrophoneState, string?>? StateChanged;

    /// <summary>Starts listening (or restarts on another microphone). Null: the default recording device.</summary>
    public void Start(string? deviceId, ClapSensitivity sensitivity, IReadOnlyList<int> counts)
    {
        Update(sensitivity, counts);
        Post(() =>
        {
            _wanted = true;
            _deviceId = deviceId;
            _failures = 0;
            return RestartAsync();
        });
    }

    /// <summary>Applies new detection settings without reopening the microphone.</summary>
    public void Update(ClapSensitivity sensitivity, IReadOnlyList<int> counts)
    {
        _sensitivity = (int)sensitivity;
        _counts = counts.ToArray();
        if (_detector is { } detector)
        {
            detector.Sensitivity = sensitivity;
            detector.Counts = counts;
        }
    }

    public void Stop() => Post(() =>
    {
        _wanted = false;
        CancelRetry();
        TearDown();
        Report(MicrophoneState.Off, null);
        return Task.CompletedTask;
    });

    /// <summary>Tries again now (after waking up, or when the user asks).</summary>
    public void Retry() => Post(() =>
    {
        _failures = 0;
        return RestartAsync();
    });

    /// <summary>The recording devices that are plugged in (id, name). Slow: call it off the UI thread.</summary>
    public static IReadOnlyList<(string Id, string Name)> ListMicrophones()
    {
        var list = new List<(string, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    list.Add((device.ID, device.FriendlyName));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Listing microphones: {ex.GetType().Name} 0x{ex.HResult:X8}"); // e.g. no audio service
        }

        return list;
    }

    /// <summary>Closes the microphone; waits briefly for that (the app is exiting).</summary>
    public void Dispose()
    {
        _watchdog.Dispose();
        Post(() =>
        {
            _wanted = false;
            CancelRetry();
            TearDown();
            _notifications?.Dispose(); // unregister first, and never from inside a notification
            _notifications = null;
            _enumerator?.Dispose();
            _enumerator = null;
            _queue.Writer.TryComplete();
            return Task.CompletedTask;
        });
        _loop.Wait(TimeSpan.FromSeconds(2));
    }

    private void Post(Func<Task> work) => _queue.Writer.TryWrite(work);

    private async Task RunLoopAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Log.Error("Microphone", ex);
            }
        }
    }

    private Task RestartAsync() => RestartAsync(quiet: false);

    /// <param name="quiet">An automatic retry: keep showing the problem (not "Starting") until it works.</param>
    private Task RestartAsync(bool quiet)
    {
        CancelRetry();
        TearDown();
        if (!_wanted)
        {
            return Task.CompletedTask;
        }

        if (GetSystemMetrics(SmRemoteSession) != 0)
        {
            // Over Remote Desktop the default microphone is the remote one; the listener resumes
            // when the session is back at the console.
            Report(MicrophoneState.RemoteSession, null);
            return Task.CompletedTask;
        }

        if (!quiet || _reported is not (MicrophoneState.Blocked or MicrophoneState.Retrying or MicrophoneState.NoMicrophone))
        {
            Report(MicrophoneState.Starting, null);
        }

        try
        {
            EnsureNotifications();
            _device = ResolveDevice();
            if (_device is null)
            {
                Report(MicrophoneState.NoMicrophone, _deviceId is null ? null : "The chosen microphone isn't connected");
                return Task.CompletedTask; // a device notification tries again
            }

            _recorder = StartWithFallbacks(_device, _generation);
            _startedAt = Environment.TickCount64;
            Log.Info($"Listening for claps with “{_device.FriendlyName}”");
            Report(MicrophoneState.Listening, _device.FriendlyName);
        }
        catch (Exception ex)
        {
            TearDown();
            HandleFailure(ex);
        }

        return Task.CompletedTask;
    }

    private void EnsureNotifications()
    {
        _enumerator ??= new MMDeviceEnumerator();
        if (_notifications is not null)
        {
            return;
        }

        // Raised on a Windows audio thread: don't block and don't touch the recorder there.
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Capture && e.Role == Role.Console)
            {
                var id = e.DeviceId;
                Post(() => OnDevicesChanged(id, defaultChanged: true));
            }
        };
        _notifications.DeviceStateChanged += (_, _) => Post(() => OnDevicesChanged(null, defaultChanged: false));
        _notifications.DeviceAdded += (_, _) => Post(() => OnDevicesChanged(null, defaultChanged: false));
        _notifications.DeviceRemoved += (_, _) => Post(() => OnDevicesChanged(null, defaultChanged: false));
    }

    private Task OnDevicesChanged(string? newDefaultId, bool defaultChanged)
    {
        if (!_wanted)
        {
            return Task.CompletedTask;
        }

        if (_recorder is null)
        {
            ScheduleRetry(TimeSpan.FromMilliseconds(500)); // waiting for a microphone: maybe this is it
        }
        else if (defaultChanged && _deviceId is null && newDefaultId != _recorder.DeviceId)
        {
            ScheduleRetry(TimeSpan.FromMilliseconds(500)); // follow the new default
        }

        return Task.CompletedTask; // if ours went away, RecordingStopped says so
    }

    private MMDevice? ResolveDevice()
    {
        if (_deviceId is null)
        {
            return _enumerator!.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var device) ? device : null;
        }

        try
        {
            var device = _enumerator!.GetDevice(_deviceId);
            if (device.State == DeviceState.Active)
            {
                return device;
            }

            device.Dispose();
        }
        catch (COMException)
        {
            // Not found.
        }

        return null;
    }

    private WasapiRecorder StartWithFallbacks(MMDevice device, int generation)
    {
        var monoFloat = WaveFormat.CreateIeeeFloatWaveFormat(RequestedSampleRate, 1);
        try
        {
            // Raw: without Windows' voice processing (noise suppression, automatic gain), which flattens claps.
            return StartRecorder(device, monoFloat, raw: true, generation);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex.HResult == AudioClientErrorCode.RawModeUnsupported)
        {
        }

        try
        {
            return StartRecorder(device, monoFloat, raw: false, generation);
        }
        catch (AudioFormatNotSupportedException)
        {
        }

        return StartRecorder(device, format: null, raw: false, generation); // the device's own format, mixed down here
    }

    private WasapiRecorder StartRecorder(MMDevice device, WaveFormat? format, bool raw, int generation)
    {
        var builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithPollingSync() // about 10 wake-ups a second
            .WithBufferLength(200);
        if (format is not null)
        {
            builder.WithFormat(format); // shared mode converts: Windows mixes down to mono for us
        }

        if (raw)
        {
            builder.WithRawMode();
        }

        var recorder = builder.Build(); // may fail with E_ACCESSDENIED
        var started = false;
        try
        {
            var waveFormat = recorder.WaveFormat;
            _isFloat = IsFloat32(waveFormat);
            _channels = Math.Max(1, waveFormat.Channels);
            if (!_isFloat && !IsPcm16(waveFormat))
            {
                throw new NotSupportedException($"The microphone's format ({waveFormat.Encoding}, {waveFormat.BitsPerSample}-bit) isn't supported.");
            }

            var detector = new ClapDetector(waveFormat.SampleRate, (ClapSensitivity)_sensitivity) { Counts = _counts };
            detector.PatternDetected += _patternDetected;
            detector.ClapHeard += _clapHeard;
            _detector = detector;
            _reportedError = false;

            recorder.DataAvailable += OnData;
            recorder.RecordingStopped += (_, e) =>
            {
                var error = e.Exception; // on the capture thread: only queue (disposing here would deadlock)
                Volatile.Write(ref _stopError, error);
                Post(() => OnStopped(generation, error));
            };
            Volatile.Write(ref _stopError, null);
            recorder.StartRecording(); // may fail: access denied, in use, …
            started = true;

            // Hand the recorder over only once it is capturing (see StopAndDispose).
            var deadline = Environment.TickCount64 + 5000;
            while (recorder.CaptureState == CaptureState.Starting && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
            }

            if (recorder.CaptureState != CaptureState.Capturing)
            {
                throw Volatile.Read(ref _stopError) ?? new TimeoutException("The microphone didn't start");
            }

            Volatile.Write(ref _lastPacket, Environment.TickCount64);
            return recorder;
        }
        catch
        {
            recorder.DataAvailable -= OnData;
            if (started)
            {
                StopAndDispose(recorder);
            }
            else
            {
                recorder.Dispose(); // single use; no capture thread yet
            }

            ForgetDetector();
            throw;
        }
    }

    /// <summary>
    /// Stops a recorder for sure and disposes it. NAudio 3.1.0 loses a stop that arrives while
    /// the device is still starting (its capture thread then sets "capturing" anyway), and its
    /// Dispose waits for that thread without a limit, so the stop is repeated until it takes.
    /// A driver that doesn't respond is left to a background thread, so this loop never hangs.
    /// </summary>
    private static void StopAndDispose(WasapiRecorder recorder)
    {
        if (TryStop(recorder, TimeSpan.FromSeconds(2)))
        {
            recorder.Dispose(); // the capture thread has ended
            return;
        }

        Log.Info("The microphone isn't responding; closing it in the background.");
        new Thread(() =>
        {
            while (!TryStop(recorder, TimeSpan.FromSeconds(10)))
            {
            }

            recorder.Dispose();
        })
        { IsBackground = true, Name = "Closing the microphone" }.Start();
    }

    private static bool TryStop(WasapiRecorder recorder, TimeSpan limit)
    {
        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (recorder.CaptureState != CaptureState.Stopped)
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            recorder.StopRecording();
            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>
    /// The capture thread. <paramref name="buffer"/> is Windows' own capture buffer, handed back
    /// when this returns; it is read in place and never copied out.
    /// </summary>
    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        Volatile.Write(ref _lastPacket, Environment.TickCount64);
        if (_detector is not { } detector)
        {
            return;
        }

        try
        {
            if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
            {
                detector.Interrupt(); // a glitch: don't let it complete or break a pattern
            }

            if (_isFloat && _channels == 1)
            {
                detector.Process(MemoryMarshal.Cast<byte, float>(buffer));
                return;
            }

            MixDown(buffer, detector);
        }
        catch (Exception ex)
        {
            if (!_reportedError)
            {
                _reportedError = true;
                Log.Error("Analysing microphone sound", ex);
            }
        }
    }

    /// <summary>Averages the channels into a small stack buffer, a piece at a time, and wipes it.</summary>
    private void MixDown(ReadOnlySpan<byte> buffer, ClapDetector detector)
    {
        Span<float> mono = stackalloc float[256];
        try
        {
            var channels = _channels;
            var floats = _isFloat ? MemoryMarshal.Cast<byte, float>(buffer) : default;
            var shorts = _isFloat ? default : MemoryMarshal.Cast<byte, short>(buffer);
            var frames = (_isFloat ? floats.Length : shorts.Length) / channels;
            for (var start = 0; start < frames; start += mono.Length)
            {
                var count = Math.Min(mono.Length, frames - start);
                for (var i = 0; i < count; i++)
                {
                    var first = (start + i) * channels;
                    var sum = 0f;
                    for (var c = 0; c < channels; c++)
                    {
                        sum += _isFloat ? floats[first + c] : shorts[first + c] / 32768f;
                    }

                    mono[i] = sum / channels;
                }

                detector.Process(mono[..count]);
            }
        }
        finally
        {
            mono.Clear();
        }
    }

    private Task OnStopped(int generation, Exception? error)
    {
        if (generation != _generation || !_wanted)
        {
            return Task.CompletedTask; // an old recorder, or stopped on purpose
        }

        if (Environment.TickCount64 - _startedAt > 60_000)
        {
            _failures = 0;
        }

        TearDown();
        HandleFailure(error ?? new InvalidOperationException("The microphone stopped."));
        return Task.CompletedTask;
    }

    private Task CheckStalled()
    {
        // A capture stream delivers packets (silent ones too) all the time; none for 3 s means it is stuck.
        var now = Environment.TickCount64;
        if (_wanted && _recorder is { CaptureState: CaptureState.Capturing } &&
            now - _startedAt > 3000 && now - Volatile.Read(ref _lastPacket) > 3000)
        {
            if (now - _startedAt > 60_000)
            {
                _failures = 0;
            }

            TearDown();
            HandleFailure(new TimeoutException("The microphone stopped delivering sound"));
        }

        return Task.CompletedTask;
    }

    private void HandleFailure(Exception ex)
    {
        switch (ex.HResult)
        {
            case AccessDenied:
                Log.Info("The microphone is blocked by Windows privacy settings.");
                Report(MicrophoneState.Blocked, null);
                ScheduleRetry(TimeSpan.FromMinutes(1));
                return;
            case AudioClientErrorCode.DeviceInvalidated:
                Report(MicrophoneState.Retrying, "The microphone went away");
                ScheduleRetry(TimeSpan.FromMilliseconds(500));
                return;
            case AudioClientErrorCode.ServiceNotRunning:
                // Windows Audio is restarting: start over with everything.
                _notifications?.Dispose();
                _notifications = null;
                _enumerator?.Dispose();
                _enumerator = null;
                break;
        }

        var delay = TimeSpan.FromSeconds(RetrySeconds[Math.Min(_failures++, RetrySeconds.Length - 1)]);
        var reason = ex.HResult == AudioClientErrorCode.DeviceInUse ? "Another app is using the microphone exclusively"
            : ex is NotSupportedException or TimeoutException ? ex.Message
            : $"Microphone error 0x{ex.HResult:X8}";
        Log.Info($"Microphone: {reason} ({ex.GetType().Name}); trying again in {delay.TotalSeconds:0} s.");
        Report(MicrophoneState.Retrying, reason);
        ScheduleRetry(delay);
    }

    private void ScheduleRetry(TimeSpan delay)
    {
        CancelRetry();
        var cts = _retry = new CancellationTokenSource();
        _ = Task.Delay(delay, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    Post(() => RestartAsync(quiet: true));
                }
            },
            TaskScheduler.Default);
    }

    private void CancelRetry()
    {
        _retry?.Cancel();
        _retry?.Dispose();
        _retry = null;
    }

    private void TearDown()
    {
        _generation++; // events from the recorder being closed are old from now on
        var recorder = _recorder;
        _recorder = null;
        if (recorder is not null)
        {
            recorder.DataAvailable -= OnData; // from now on nothing more is analysed
            StopAndDispose(recorder);
            Log.Info("Stopped listening for claps.");
        }

        _device?.Dispose();
        _device = null;
        ForgetDetector();
    }

    /// <summary>The capture thread has ended: drop the detector and wipe what it remembered.</summary>
    private void ForgetDetector()
    {
        if (_detector is { } detector)
        {
            _detector = null;
            detector.PatternDetected -= _patternDetected;
            detector.ClapHeard -= _clapHeard;
            detector.Reset();
        }
    }

    private void Report(MicrophoneState state, string? detail)
    {
        _reported = state;
        StateChanged?.Invoke(state, detail);
    }

    private static bool IsFloat32(WaveFormat format) => format.BitsPerSample == 32 &&
        (format.Encoding == WaveFormatEncoding.IeeeFloat ||
         (format is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT));

    private static bool IsPcm16(WaveFormat format) => format.BitsPerSample == 16 &&
        (format.Encoding == WaveFormatEncoding.Pcm ||
         (format is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM));

    // ------------------------------------------------------------------ interop

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
