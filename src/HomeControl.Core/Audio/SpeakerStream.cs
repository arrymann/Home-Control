namespace HomeControl.Core.Audio;

/// <summary>
/// Follows what Windows sends to one speaker device (its loopback stream) and marks in a
/// <see cref="SpeakerSounds"/> when each of its sharp sounds may reach the microphone.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="ClapDetector"/> it keeps no sound. Each sample goes through the same filters
/// and is added to a running sum, then forgotten; every 10 ms the sum becomes the loudness of
/// 2–7 kHz. All it remembers is the filters' state, the background level, the last two blocks'
/// loudness and the sound being judged.
/// </para>
/// <para>
/// A sharp sound is a rise in that band of at least 6 dB within 30 ms: gentler than any rise
/// the detector takes for a clap, since the way through the room can only blur a sound, not
/// sharpen it. It may reach the microphone from <see cref="MinDelay"/> to <see cref="MaxDelay"/>
/// after the loopback stream has it, and is marked with how sharply it rose, so that the user's
/// sharper claps over it still count.
/// </para>
/// <para>
/// Times are seconds on the clock the detector uses too (the loopback packet's QPC position).
/// Nothing playing means no packets: a jump in time, a silent packet or a lost one is
/// <see cref="Gap"/>, after which any sound is a sharp rise from silence.
/// </para>
/// </remarks>
public sealed class SpeakerStream
{
    /// <summary>Wired or USB speakers: output latency plus the way through the room, with margin.</summary>
    public const double WiredMaxDelay = 0.2;

    /// <summary>A monitor's or TV's speakers (HDMI, DisplayPort), which may add their own processing.</summary>
    public const double DisplayMaxDelay = 0.3;

    /// <summary>Bluetooth speakers, which buffer a lot.</summary>
    public const double BluetoothMaxDelay = 0.5;

    private const double SilentEnergy = 1e-15;
    private const double StartRise = 4;     // dB over the reference: maybe a sharp sound...
    private const double Rise = 6;          // ...which it is if its peak, within two more blocks, is this far over it
    private const double MinimumLevel = -80; // dB re full scale: anything quieter is inaudible
    private const double GapSeconds = 0.02; // a jump in time this big means nothing played (or sound was lost)

    private readonly SpeakerSounds _sounds;
    private readonly int _blockLength;
    private readonly double _floorRise;
    private readonly double _floorFall;
    private ClapDetector.Biquad _lowPass;
    private ClapDetector.Biquad _highPass;
    private ClapDetector.Biquad _highBand;
    private double _sumHigh;
    private int _samplesInBlock;
    private double _blockStart;
    private double _expected;
    private double _floor;
    private double _high1;
    private double _high2;
    private bool _tracking;
    private int _age;
    private double _onsetTime;
    private double _onsetReference;
    private double _peak;
    private double _minDelay = -0.03;
    private double _maxDelay;

    /// <param name="sounds">Where to mark the sharp sounds (shared by all speaker devices).</param>
    /// <param name="sampleRate">The loopback stream's rate.</param>
    /// <param name="maxDelay">See <see cref="MaxDelay"/>.</param>
    public SpeakerStream(SpeakerSounds sounds, int sampleRate, double maxDelay = WiredMaxDelay)
    {
        if (sampleRate < 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "At least 8 kHz is needed.");
        }

        _sounds = sounds;
        SampleRate = sampleRate;
        _blockLength = (int)Math.Round(sampleRate * 0.010);
        var blockSeconds = (double)_blockLength / sampleRate;
        _floorRise = 1 - Math.Exp(-blockSeconds / 2.0);
        _floorFall = 1 - Math.Exp(-blockSeconds / 0.2);
        _maxDelay = maxDelay;
        Reset();
    }

    public int SampleRate { get; }

    /// <summary>
    /// The soonest a sound may start at the microphone after it starts in the loopback stream
    /// (seconds). Below zero, for the timestamps' and the 10 ms blocks' uncertainty.
    /// </summary>
    public double MinDelay
    {
        get => _minDelay;
        set => _minDelay = value;
    }

    /// <summary>
    /// The latest (seconds): the device's output latency after the loopback point, the way
    /// through the room and the timestamps' uncertainty (<see cref="WiredMaxDelay"/>,
    /// <see cref="DisplayMaxDelay"/>, <see cref="BluetoothMaxDelay"/>).
    /// </summary>
    public double MaxDelay
    {
        get => _maxDelay;
        set => _maxDelay = value;
    }

    /// <summary>Analyses what the speakers play next (mono, -1 to 1). Nothing is kept of it.</summary>
    /// <param name="startTime">When the first sample was played, in seconds on the shared clock.</param>
    public void Process(ReadOnlySpan<float> samples, double startTime)
    {
        if (!(Math.Abs(startTime - _expected) <= GapSeconds))
        {
            Gap(); // nothing played in between (or the first packet)
        }

        var rate = (double)SampleRate;
        for (var i = 0; i < samples.Length; i++)
        {
            if (_samplesInBlock == 0)
            {
                _blockStart = startTime + i / rate;
            }

            var x = float.IsFinite(samples[i]) ? samples[i] : 0f;
            var high = _highBand.Step(_highPass.Step(_lowPass.Step(x)));
            _sumHigh += high * high;
            if (++_samplesInBlock == _blockLength)
            {
                EndBlock();
            }
        }

        _expected = startTime + samples.Length / rate;
    }

    /// <summary>
    /// The speakers played nothing for a while (no packets, a silent packet) or sound was lost:
    /// whatever plays next rises from silence.
    /// </summary>
    public void Gap()
    {
        ResetFilters();
        _sumHigh = 0;
        _samplesInBlock = 0;
        _tracking = false;
        _floor = -100;
        _high1 = _high2 = -200;
        _expected = double.NaN;
    }

    /// <summary>Forgets everything it heard (the delays, which describe the device, stay).</summary>
    public void Reset()
    {
        Gap();
        _blockStart = 0;
        _age = 0;
        _onsetTime = _onsetReference = _peak = 0;
    }

    private void ResetFilters()
    {
        var rate = (double)SampleRate;
        _lowPass = ClapDetector.Biquad.LowPass(rate, Math.Min(7000, 0.45 * rate));
        _highPass = ClapDetector.Biquad.HighPass(rate, 60);
        _highBand = ClapDetector.Biquad.HighPass(rate, 2000);
    }

    private void EndBlock()
    {
        var energy = _sumHigh / _samplesInBlock;
        _sumHigh = 0;
        _samplesInBlock = 0;
        var high = energy < SilentEnergy ? -200 : 10 * Math.Log10(energy);
        if (!double.IsFinite(high))
        {
            ResetFilters(); // overflowed
            high = -200;
        }

        var reference = Math.Max(_high2, _floor);
        if (_tracking)
        {
            _peak = Math.Max(_peak, high);
            if (++_age >= 2 || _peak - _onsetReference >= Rise)
            {
                _tracking = false;
                if (_peak - _onsetReference >= Rise && _peak >= MinimumLevel)
                {
                    _sounds.Add(_onsetTime + _minDelay, _onsetTime + _maxDelay, _peak - _onsetReference);
                }
            }
        }
        else if (high - reference >= StartRise && high >= MinimumLevel - Rise)
        {
            if (high - reference >= Rise && high >= MinimumLevel)
            {
                _sounds.Add(_blockStart + _minDelay, _blockStart + _maxDelay, high - reference);
            }
            else
            {
                _tracking = true;
                _age = 0;
                _onsetTime = _blockStart;
                _onsetReference = reference;
                _peak = high;
            }
        }

        if (!_tracking)
        {
            // The background (the music under a sharp sound) follows the quiet moments: it falls fast and rises slowly.
            _floor = Math.Max(-100, _floor + (high < _floor ? _floorFall : _floorRise) * (high - _floor));
        }

        _high2 = _high1;
        _high1 = high;
    }
}
