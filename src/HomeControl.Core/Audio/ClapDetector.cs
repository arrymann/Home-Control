namespace HomeControl.Core.Audio;

/// <summary>How readily <see cref="ClapDetector"/> takes a sound for a clap.</summary>
public enum ClapSensitivity
{
    /// <summary>Only loud, clear claps: fewer mistakes in a noisy room, but quiet claps are missed.</summary>
    Low,
    Medium,

    /// <summary>Quieter claps from further away too, but more sounds are mistaken for claps.</summary>
    High,
}

/// <summary>
/// Hears claps in microphone sound and recognises 2, 3 or 4 claps in a row.
/// </summary>
/// <remarks>
/// <para>
/// It keeps no sound. Each sample goes through three filters and is added to two running sums,
/// then forgotten; every 10 ms the sums become two loudness figures (2–7 kHz, and below 2 kHz)
/// and start again. All it remembers is a handful of numbers: the filters' state (made from the
/// last two samples), the background noise level, the last two blocks' loudness and the timing
/// of the current clap and sequence. <see cref="Reset"/> clears them.
/// </para>
/// <para>
/// A clap is a sharp rise in the high band that dies away like a clap in a room: by 40 ms after
/// its peak it has dropped by a few dB (speech and music don't) but still has a room tail
/// (keyboard clicks don't), it has faded within about 150 ms, nothing rises again during an 80 ms
/// hold, and the high band isn't much quieter than the low band (knocks and thumps are).
/// Claps in a row are 120–600 ms apart with a steady rhythm. A sequence starts only after 0.8 s
/// without claps and 0.3 s without other sounds, any other sound during it cancels it, and it is
/// reported once it is clearly over, so three claps never also count as two.
/// </para>
/// <para>
/// Times come from the number of samples processed, not the clock, so the result depends only
/// on the sound. Call <see cref="Process"/> from one thread at a time; <see cref="Counts"/> and
/// <see cref="Sensitivity"/> may be changed from any thread. The events are raised on the
/// thread that calls <see cref="Process"/>.
/// </para>
/// </remarks>
public sealed class ClapDetector
{
    public const int MinCount = 2;
    public const int MaxCount = 4;

    private const double WarmUpSeconds = 0.5;
    private const double HoldSeconds = 0.08;
    private const double TailCheckSeconds = 0.04;
    private const double MinGap = 0.12;
    private const double MaxGap = 0.60;
    private const double MaxRhythmRatio = 2.0;
    private const double NoiseGuard = 0.30;
    private const double ClapGuard = 0.80;
    private const double ClosingQuiet = 0.25;

    private readonly int _blockLength;
    private readonly double _blockSeconds;
    private readonly double _floorRise;
    private readonly double _floorFall;
    private volatile Thresholds _thresholds;
    private volatile int _counts;
    private ClapSensitivity _sensitivity;

    // Front end: band filters and this block's running energy.
    private Biquad _lowPass;
    private Biquad _highPass;
    private Biquad _highBand;
    private double _sumHigh;
    private double _sumLow;
    private int _samplesInBlock;
    private long _block;

    // Levels in dB: background noise per band, and the two blocks before this one.
    private double _floorHigh;
    private double _floorLow;
    private double _high1;
    private double _high2;
    private double _low1;
    private double _low2;

    // The sound being judged.
    private bool _tracking;
    private bool _decayed;
    private long _onsetBlock;
    private long _peakBlock;
    private double _peakHigh;
    private double _peakLow;
    private double _onsetReference;

    // The claps in a row so far.
    private int _claps;
    private double _lastClap;
    private double _shortestGap;
    private double _longestGap;
    private double _previousClap;
    private double _lastNoise;
    private double _lockedUntil;

    public ClapDetector(int sampleRate, ClapSensitivity sensitivity = ClapSensitivity.Medium)
    {
        if (sampleRate < 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "At least 8 kHz is needed.");
        }

        SampleRate = sampleRate;
        _blockLength = (int)Math.Round(sampleRate * 0.010);
        _blockSeconds = (double)_blockLength / sampleRate;
        _floorRise = 1 - Math.Exp(-_blockSeconds / 2.0);
        _floorFall = 1 - Math.Exp(-_blockSeconds / 0.2);
        _sensitivity = sensitivity;
        _thresholds = Thresholds.For(sensitivity);
        Reset();
    }

    /// <summary>A pattern of this many claps was heard (one of <see cref="Counts"/>).</summary>
    public event Action<int>? PatternDetected;

    /// <summary>A single clap was heard (whether or not it becomes a pattern), e.g. to show that claps are heard.</summary>
    public event Action? ClapHeard;

    public int SampleRate { get; }

    public ClapSensitivity Sensitivity
    {
        get => _sensitivity;
        set
        {
            _sensitivity = value;
            _thresholds = Thresholds.For(value);
        }
    }

    /// <summary>The numbers of claps in a row that are reported (2 to 4; others are ignored).</summary>
    public IReadOnlyList<int> Counts
    {
        get
        {
            var mask = _counts;
            return Enumerable.Range(MinCount, MaxCount - MinCount + 1).Where(n => (mask & (1 << n)) != 0).ToList();
        }
        set
        {
            var mask = 0;
            foreach (var count in value)
            {
                if (count is >= MinCount and <= MaxCount)
                {
                    mask |= 1 << count;
                }
            }

            _counts = mask;
        }
    }

    /// <summary>Analyses the next samples (mono, -1 to 1). Nothing is kept of them.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            var full = _highPass.Step(_lowPass.Step(sample));
            var high = _highBand.Step(full);
            var low = full - high;
            _sumHigh += high * high;
            _sumLow += low * low;
            if (++_samplesInBlock == _blockLength)
            {
                EndBlock();
            }
        }
    }

    /// <summary>
    /// Something interrupted the sound (a gap in the stream): whatever was being judged is
    /// dropped and a sequence in progress is cancelled.
    /// </summary>
    public void Interrupt()
    {
        _tracking = false;
        Noise(_block * _blockSeconds);
    }

    /// <summary>Forgets everything: the filters, the background level and any claps so far.</summary>
    public void Reset()
    {
        var rate = (double)SampleRate;
        _lowPass = Biquad.LowPass(rate, Math.Min(7000, 0.45 * rate));
        _highPass = Biquad.HighPass(rate, 60);
        _highBand = Biquad.HighPass(rate, 2000);
        _sumHigh = _sumLow = 0;
        _samplesInBlock = 0;
        _block = 0;
        _floorHigh = _floorLow = double.NaN;
        _high1 = _high2 = _low1 = _low2 = -200;
        _tracking = _decayed = false;
        _onsetBlock = _peakBlock = 0;
        _peakHigh = _peakLow = _onsetReference = 0;
        _claps = 0;
        _lastClap = _shortestGap = _longestGap = 0;
        _previousClap = _lastNoise = _lockedUntil = double.NegativeInfinity;
    }

    private static double Decibels(double energy) => 10 * Math.Log10(energy + 1e-20);

    private long Blocks(double seconds) => (long)Math.Round(seconds / _blockSeconds);

    private void EndBlock()
    {
        var high = Decibels(_sumHigh / _samplesInBlock);
        var low = Decibels(_sumLow / _samplesInBlock);
        _sumHigh = _sumLow = 0;
        _samplesInBlock = 0;

        var thresholds = _thresholds;
        if (_block * _blockSeconds < WarmUpSeconds)
        {
            // Learn the background quickly before judging anything.
            _floorHigh = double.IsNaN(_floorHigh) ? high : _floorHigh + 0.2 * (high - _floorHigh);
            _floorLow = double.IsNaN(_floorLow) ? low : _floorLow + 0.2 * (low - _floorLow);
        }
        else if (_tracking)
        {
            Track(high, low, thresholds);
        }
        else
        {
            TryStart(high, low, thresholds);
        }

        _high2 = _high1;
        _high1 = high;
        _low2 = _low1;
        _low1 = low;
        _block++;
        Advance(_block * _blockSeconds);
    }

    /// <summary>Background levels follow the quiet moments: they fall fast and rise slowly.</summary>
    private void UpdateFloors(double high, double low)
    {
        _floorHigh = Math.Max(-120, _floorHigh + (high < _floorHigh ? _floorFall : _floorRise) * (high - _floorHigh));
        _floorLow = Math.Max(-120, _floorLow + (low < _floorLow ? _floorFall : _floorRise) * (low - _floorLow));
    }

    private void TryStart(double high, double low, Thresholds t)
    {
        // Compared with two blocks back, so a clap split across two blocks still counts as a rise.
        var reference = Math.Max(_high2, _floorHigh);
        if (high - _floorHigh >= Math.Max(10, t.AboveFloor - 10) && high - reference >= Math.Max(6, t.Rise - 6))
        {
            // Judge it on its peak over the next blocks, against the full thresholds.
            _tracking = true;
            _decayed = false;
            _onsetBlock = _peakBlock = _block;
            _peakHigh = high;
            _peakLow = low;
            _onsetReference = reference;
            return;
        }

        // A rise in the low band alone (speech, a knock, a thump) is a sound but not a clap.
        if (low - _floorLow >= t.AboveFloor && low - Math.Max(_low2, _floorLow) >= t.Rise)
        {
            Noise(_block * _blockSeconds);
        }

        UpdateFloors(high, low);
    }

    private void Track(double high, double low, Thresholds t)
    {
        var age = _block - _onsetBlock;
        if (age <= 2)
        {
            if (high > _peakHigh)
            {
                _peakHigh = high;
                _peakBlock = _block;
            }

            _peakLow = Math.Max(_peakLow, low);
            if (age == 2 && !(_peakHigh - _floorHigh >= t.AboveFloor && _peakHigh - _onsetReference >= t.Rise && _peakHigh >= t.MinimumLevel))
            {
                Reject(); // too weak
            }

            return;
        }

        // It rises again (a plosive followed by a vowel, a second click): not a clap; maybe the
        // start of something new.
        if (high > _peakHigh + 3 || low > _peakLow + 3 || high - Math.Max(_high2, _floorHigh) >= Math.Min(t.Rise, 10))
        {
            Reject();
            TryStart(high, low, t);
            return;
        }

        var sincePeak = _block - _peakBlock;
        if (sincePeak == Blocks(TailCheckSeconds))
        {
            if (_peakHigh - high < t.MinDrop)
            {
                Reject(); // sustained: speech, music
                return;
            }

            if (high - _floorHigh < t.MinTail || _peakHigh - high > t.MaxDrop)
            {
                Reject(); // dry, no room tail: a keyboard or mouse click
                return;
            }
        }

        if (!_decayed)
        {
            var highDone = _peakHigh - high >= t.DecayHigh || high - _floorHigh <= 3;
            var lowDone = _peakLow - low >= t.DecayLow || low - _floorLow <= 3;
            if (highDone && lowDone)
            {
                _decayed = true;
            }
            else if (sincePeak * _blockSeconds * 1000 > t.DecayWindowMs)
            {
                Reject(); // doesn't fade like a clap
                return;
            }
        }

        if (_decayed && sincePeak >= Blocks(TailCheckSeconds) && age >= Blocks(HoldSeconds))
        {
            _tracking = false;
            if (_peakHigh - _peakLow >= t.MinHighOverLow)
            {
                Clap(_onsetBlock * _blockSeconds);
            }
            else
            {
                Noise(_onsetBlock * _blockSeconds); // mostly low: a knock or a thump
            }
        }
    }

    private void Reject()
    {
        _tracking = false;
        Noise(_onsetBlock * _blockSeconds);
    }

    // ---------------------------------------------------------------- claps in a row

    private int Largest()
    {
        var mask = _counts;
        for (var n = MaxCount; n >= MinCount; n--)
        {
            if ((mask & (1 << n)) != 0)
            {
                return n;
            }
        }

        return MaxCount;
    }

    private void Clap(double time)
    {
        ClapHeard?.Invoke();
        var previous = _previousClap;
        _previousClap = time;
        if (time < _lockedUntil)
        {
            _lockedUntil = time + MaxGap; // part of a burst that was rejected: wait for quiet
            return;
        }

        if (_claps == 0)
        {
            // Only after a quiet moment, so the middle of applause or chatter doesn't start one.
            if (time - _lastNoise < NoiseGuard || time - previous < ClapGuard)
            {
                _lockedUntil = time + MaxGap;
                return;
            }

            _claps = 1;
            _lastClap = time;
            _shortestGap = double.MaxValue;
            _longestGap = 0;
            return;
        }

        var gap = time - _lastClap;
        if (gap < MinGap || gap > MaxGap || _claps >= Largest())
        {
            Cancel(time);
            return;
        }

        _claps++;
        _lastClap = time;
        _shortestGap = Math.Min(_shortestGap, gap);
        _longestGap = Math.Max(_longestGap, gap);
        if (_claps >= 3 && _longestGap > MaxRhythmRatio * _shortestGap)
        {
            Cancel(time); // no steady rhythm
        }
    }

    private void Noise(double time)
    {
        _lastNoise = time;
        if (_claps > 0)
        {
            Cancel(time);
        }
    }

    private void Cancel(double time)
    {
        _claps = 0;
        _lockedUntil = time + MaxGap;
    }

    /// <summary>After each block: report the sequence once no further clap can belong to it.</summary>
    private void Advance(double now)
    {
        if (_claps == 0)
        {
            return;
        }

        var largest = Largest();
        var wait = _claps == 1 ? MaxGap
            : _claps >= largest ? Math.Min(MaxGap, Math.Max(ClosingQuiet, 1.5 * _shortestGap)) // only one too many can follow
            : Math.Min(MaxGap, MaxRhythmRatio * _shortestGap + 0.03); // the next clap would keep the rhythm

        // A sound inside the window is still being judged: wait for its verdict.
        if (_tracking && _onsetBlock * _blockSeconds - _lastClap <= wait)
        {
            return;
        }

        if (now - _lastClap > wait)
        {
            var claps = _claps;
            _claps = 0;
            _lockedUntil = now;
            if ((_counts & (1 << claps)) != 0)
            {
                PatternDetected?.Invoke(claps);
            }
        }
    }

    /// <summary>What a clap must measure up to (dB), per <see cref="ClapSensitivity"/>.</summary>
    /// <param name="AboveFloor">High band peak above the background.</param>
    /// <param name="Rise">High band peak above the level two blocks before.</param>
    /// <param name="MinimumLevel">High band peak level (re full scale).</param>
    /// <param name="MinHighOverLow">High band peak minus low band peak.</param>
    /// <param name="MinDrop">Least drop 40 ms after the peak (less: sustained).</param>
    /// <param name="MinTail">Least level above the background 40 ms after the peak (less: dry).</param>
    /// <param name="MaxDrop">Most drop 40 ms after the peak (more: dry).</param>
    /// <param name="DecayHigh">Drop of the high band that counts as faded.</param>
    /// <param name="DecayLow">Drop of the low band that counts as faded.</param>
    /// <param name="DecayWindowMs">Time after the peak to fade in.</param>
    private sealed record Thresholds(
        double AboveFloor,
        double Rise,
        double MinimumLevel,
        double MinHighOverLow,
        double MinDrop,
        double MinTail,
        double MaxDrop,
        double DecayHigh,
        double DecayLow,
        double DecayWindowMs)
    {
        private static readonly Thresholds LowSensitivity = new(28, 18, -45, -5, 4, 8, 18, 12, 10, 120);
        private static readonly Thresholds MediumSensitivity = new(21, 14, -55, -8, 3, 6, 20, 10, 8, 150);
        private static readonly Thresholds HighSensitivity = new(15, 10, -65, -12, 2, 3, 24, 8, 6, 200);

        public static Thresholds For(ClapSensitivity sensitivity) => sensitivity switch
        {
            ClapSensitivity.Low => LowSensitivity,
            ClapSensitivity.High => HighSensitivity,
            _ => MediumSensitivity,
        };
    }

    /// <summary>A second-order Butterworth filter (RBJ cookbook, transposed direct form II).</summary>
    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public static Biquad HighPass(double rate, double frequency)
        {
            var (cos, alpha, a0) = Prepare(rate, frequency);
            return new Biquad
            {
                _b0 = (1 + cos) / 2 / a0,
                _b1 = -(1 + cos) / a0,
                _b2 = (1 + cos) / 2 / a0,
                _a1 = -2 * cos / a0,
                _a2 = (1 - alpha) / a0,
            };
        }

        public static Biquad LowPass(double rate, double frequency)
        {
            var (cos, alpha, a0) = Prepare(rate, frequency);
            return new Biquad
            {
                _b0 = (1 - cos) / 2 / a0,
                _b1 = (1 - cos) / a0,
                _b2 = (1 - cos) / 2 / a0,
                _a1 = -2 * cos / a0,
                _a2 = (1 - alpha) / a0,
            };
        }

        public double Step(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        private static (double Cos, double Alpha, double A0) Prepare(double rate, double frequency)
        {
            var w = 2 * Math.PI * frequency / rate;
            var alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            return (Math.Cos(w), alpha, 1 + alpha);
        }
    }
}
