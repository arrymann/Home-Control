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
/// and loudness of the current clap, sequence and last other sounds. <see cref="Reset"/> clears them.
/// </para>
/// <para>
/// A clap is a sharp rise in the high band that dies away like a clap in a room: by 40 ms after
/// its peak it has dropped by a few dB (speech and music don't) but still has a loud room tail
/// (clicks next to the microphone don't), it has faded within about 150 ms, nothing rises again
/// during an 80 ms hold, and the high band isn't far quieter than the low band (knocks and
/// thumps are; claps with cupped hands can be 17 dB duller). Its high band also reaches at least
/// about the level of the room's own low-pitched noise, so the faint ticks and whirs of a machine
/// humming in the room (a 3D printer, a fan) don't count, however quiet its hiss is.
/// Claps in a row are 120–600 ms apart in a steady rhythm (no gap over 1.75 times another). A
/// sequence starts only after 0.8 s without claps (or right after a finished sequence, so a
/// pattern can be repeated) and 0.3 s without other sounds, any other sound that stands out
/// during it cancels it, and it is reported once it is clearly over, so three claps never also
/// count as two (a clap too late for the rhythm ends the sequence and may start the next). A try
/// that was blocked but was a whole pair lets the next try follow straight away. Sounds that
/// don't stand out from the room, and faint high ticks (the low band hardly rises and the high
/// band stays 10 dB or more below the claps), don't count as other sounds; voices, music, knocks
/// and anything about as loud as the claps do.
/// </para>
/// <para>
/// A clap that starts when a sound from the PC's own speakers may be reaching the microphone
/// (see <see cref="Speakers"/>) is taken for theirs, not the user's.
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
    private const double SilentEnergy = 1e-15; // -150 dB: no microphone is this quiet, only digital silence
    private const double HoldSeconds = 0.08;
    private const double TailCheckSeconds = 0.04;
    private const double MinGap = 0.12;
    private const double MaxGap = 0.60;
    private const double MaxRhythmRatio = 1.75;
    private const double NoiseGuard = 0.30;
    private const double ClapGuard = 0.80;
    private const double ClosingQuiet = 0.25;
    private const double OnsetSeconds = 0.04;  // a sound may still grow this long after it starts
    private const double MinOverRoom = -5;     // dB: high band peak against the room's low-band noise
    private const double TickLowRise = 6;     // dB: a sound whose low band rises less than this is a high tick ...
    private const double TickBelowClaps = 10; // dB: ... and a tick this far below the claps doesn't count
    private const double BlockedQuiet = 0.45; // s: a blocked try is over after this long without claps

    private readonly int _blockLength;
    private readonly double _blockSeconds;
    private readonly double _floorRise;
    private readonly double _floorFall;
    private volatile Thresholds _thresholds;
    private volatile int _counts;
    private volatile SpeakerSounds? _speakers;
    private ClapSensitivity _sensitivity;

    // Front end: band filters and this block's running energy.
    private Biquad _lowPass;
    private Biquad _highPass;
    private Biquad _highBand;
    private double _sumHigh;
    private double _sumLow;
    private int _samplesInBlock;
    private long _block;
    private long _learnUntil;

    // The shared clock (seconds) at sample 0, from the last Process call that gave one.
    private double _clockOffset;

    // Levels in dB: background noise per band (and as it was in the last steady block, before
    // any fade into silence), and the two blocks before this one.
    private double _floorHigh;
    private double _floorLow;
    private double _steadyFloorHigh;
    private double _steadyFloorLow;
    private bool _wasSilent;
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

    // The claps in a row so far, and the sounds around them (levels in dB above the background).
    private int _claps;
    private double _lastClap;
    private double _shortestGap;
    private double _longestGap;
    private double _quietestClap;
    private double _previousClap;
    private double _lastNoise;
    private double _lastTick;
    private double _lastTickHigh;
    private double _lockedUntil;
    private int _blockedClaps;
    private double _blockedLast;

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

    /// <summary>
    /// What the PC's own speakers play, as times on the clock given to
    /// <see cref="Process(ReadOnlySpan{float}, double)"/>: a clap that starts while a sound of
    /// theirs may be reaching the microphone is ignored (it counts as another sound). Null: none.
    /// </summary>
    public SpeakerSounds? Speakers
    {
        get => _speakers;
        set => _speakers = value;
    }

    /// <summary>Analyses the next samples (mono, -1 to 1). Nothing is kept of them.</summary>
    /// <param name="startTime">
    /// When the first of them was recorded, in seconds on the clock that <see cref="Speakers"/>
    /// uses (e.g. the capture packet's QPC position / 10^7).
    /// </param>
    public void Process(ReadOnlySpan<float> samples, double startTime)
    {
        _clockOffset = startTime - (double)(_block * _blockLength + _samplesInBlock) / SampleRate;
        Process(samples);
    }

    /// <summary>Analyses the next samples (mono, -1 to 1). Nothing is kept of them.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            var x = float.IsFinite(sample) ? sample : 0f; // a glitch mustn't poison the filters
            var full = _highPass.Step(_lowPass.Step(x));
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
        Noise(_block * _blockSeconds, double.PositiveInfinity, double.PositiveInfinity);
    }

    /// <summary>Forgets everything: the filters, the background level and any claps so far.</summary>
    public void Reset()
    {
        ResetFilters();
        _sumHigh = _sumLow = 0;
        _samplesInBlock = 0;
        _block = 0;
        _clockOffset = 0;
        _learnUntil = Blocks(WarmUpSeconds);
        _floorHigh = _floorLow = _steadyFloorHigh = _steadyFloorLow = double.NaN;
        _wasSilent = false;
        _high1 = _high2 = _low1 = _low2 = -200;
        _tracking = _decayed = false;
        _onsetBlock = _peakBlock = 0;
        _peakHigh = _peakLow = _onsetReference = 0;
        _claps = 0;
        _lastClap = _shortestGap = _longestGap = _quietestClap = _lastTickHigh = 0;
        _previousClap = _lastNoise = _lastTick = _lockedUntil = _blockedLast = double.NegativeInfinity;
        _blockedClaps = 0;
    }

    private void ResetFilters()
    {
        var rate = (double)SampleRate;
        _lowPass = Biquad.LowPass(rate, Math.Min(7000, 0.45 * rate));
        _highPass = Biquad.HighPass(rate, 60);
        _highBand = Biquad.HighPass(rate, 2000);
    }

    private static double Decibels(double energy) => 10 * Math.Log10(energy + 1e-20);

    private long Blocks(double seconds) => (long)Math.Round(seconds / _blockSeconds);

    private void EndBlock()
    {
        var energyHigh = _sumHigh / _samplesInBlock;
        var energyLow = _sumLow / _samplesInBlock;
        var high = Decibels(energyHigh);
        var low = Decibels(energyLow);
        _sumHigh = _sumLow = 0;
        _samplesInBlock = 0;

        // Digital silence (muted, a dropout, a noise gate, or a very quiet room through a 16-bit
        // microphone) says nothing about the room: it never moves the background level, which
        // would otherwise sink far below the room and let dry clicks pass for claps.
        var thresholds = _thresholds;
        var silent = energyHigh + energyLow < SilentEnergy;
        if (!double.IsFinite(high) || !double.IsFinite(low))
        {
            ResetFilters(); // overflowed: start the filters again, and drop what was being judged
            Interrupt();
            high = low = -200;
            silent = true;
        }

        if (silent && !_wasSilent && !double.IsNaN(_floorHigh) && !double.IsNaN(_steadyFloorHigh))
        {
            // The fade into silence (the filters ringing down) isn't the room either.
            _floorHigh = Math.Max(_floorHigh, _steadyFloorHigh);
            _floorLow = Math.Max(_floorLow, _steadyFloorLow);
        }

        _wasSilent = silent;
        if (double.IsNaN(_floorHigh) && !silent && _block >= _learnUntil)
        {
            _learnUntil = _block + Blocks(WarmUpSeconds); // the sound started silent: the room is heard only now
        }

        if (_block < _learnUntil || double.IsNaN(_floorHigh))
        {
            // Learn the background quickly before judging anything.
            if (!silent)
            {
                RememberSteadyFloors(high, low);
                _floorHigh = Math.Max(-120, double.IsNaN(_floorHigh) ? high : _floorHigh + 0.2 * (high - _floorHigh));
                _floorLow = Math.Max(-120, double.IsNaN(_floorLow) ? low : _floorLow + 0.2 * (low - _floorLow));
            }
        }
        else if (_tracking)
        {
            Track(high, low, thresholds);
        }
        else if (!silent)
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
        RememberSteadyFloors(high, low);
        _floorHigh = Math.Max(-120, _floorHigh + (high < _floorHigh ? _floorFall : _floorRise) * (high - _floorHigh));
        _floorLow = Math.Max(-120, _floorLow + (low < _floorLow ? _floorFall : _floorRise) * (low - _floorLow));
    }

    /// <summary>A block close to the background is ordinary room sound: remember the background as it is.</summary>
    private void RememberSteadyFloors(double high, double low)
    {
        if (!double.IsNaN(_floorHigh) && Math.Abs(high - _floorHigh) <= 6 && Math.Abs(low - _floorLow) <= 6)
        {
            _steadyFloorHigh = _floorHigh;
            _steadyFloorLow = _floorLow;
        }
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
            Noise(_block * _blockSeconds, high - _floorHigh, low - _floorLow);
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
                Reject(t); // too weak
            }

            return;
        }

        // Still growing a moment after it began (the hands didn't meet at once): the same sound,
        // with a later peak.
        if (age <= Blocks(OnsetSeconds) && high > _peakHigh + 3)
        {
            _peakHigh = high;
            _peakBlock = _block;
            _peakLow = Math.Max(_peakLow, low);
            return;
        }

        // It rises again (a plosive followed by a vowel, a second click): not a clap; maybe the
        // start of something new. (A rise from before the peak is the peak's own.)
        if (high > _peakHigh + 3 || low > _peakLow + 3
            || (_block - 2 > _peakBlock && high - Math.Max(_high2, _floorHigh) >= Math.Min(t.Rise, 10)))
        {
            Reject(t);
            TryStart(high, low, t);
            return;
        }

        var sincePeak = _block - _peakBlock;
        if (sincePeak == Blocks(TailCheckSeconds))
        {
            if (_peakHigh - high < t.MinDrop)
            {
                Reject(t); // sustained: speech, music
                return;
            }

            if (high - _floorHigh < t.MinTail || _peakHigh - high > t.MaxDrop)
            {
                Reject(t); // dry, no room tail: a keyboard or mouse click
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
                Reject(t); // doesn't fade like a clap
                return;
            }
        }

        if (_decayed && sincePeak >= Blocks(TailCheckSeconds) && age >= Blocks(HoldSeconds))
        {
            if (_peakHigh - _peakLow >= t.MinHighOverLow && _peakHigh - _floorLow >= MinOverRoom && !FromSpeakers())
            {
                _tracking = false;
                Clap(_onsetBlock * _blockSeconds, _peakHigh - _floorHigh);
            }
            else
            {
                Reject(t); // mostly low (a knock or a thump), faint next to the room's own noise, or the speakers'
            }
        }
    }

    /// <summary>Whether the sound being judged may have come from the PC's own speakers.</summary>
    private bool FromSpeakers() =>
        _speakers is { } speakers && speakers.MayExplain(_onsetBlock * _blockSeconds + _clockOffset);

    /// <summary>
    /// Not a clap. It interrupts claps only if it stood out from the room: the faint ticks and
    /// whirs of a machine, quiet next to the machine's own hum, don't.
    /// </summary>
    private void Reject(Thresholds t)
    {
        _tracking = false;
        var time = _onsetBlock * _blockSeconds;

        // Anything with a low part (a knock, a thump) always counts; the tick rules in Noise
        // decide about the rest.
        var stoodOut = (_peakHigh - _floorHigh >= t.AboveFloor && _peakHigh - _floorLow >= MinOverRoom)
                       || _peakLow - _floorLow >= TickLowRise;
        if (stoodOut)
        {
            Noise(time, _peakHigh - _floorHigh, _peakLow - _floorLow);
        }
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

    /// <param name="level">The clap's high band peak above the background (dB).</param>
    private void Clap(double time, double level)
    {
        ClapHeard?.Invoke();
        if (_claps >= MinCount && time - _lastClap > MaxRhythmRatio * _shortestGap)
        {
            // Too late to keep the rhythm, though the sequence wasn't reported yet: it is over,
            // and this clap may start the next one (two pairs a little apart are two pairs).
            Finish(time);
        }

        var previous = _previousClap;
        _previousClap = time;
        if (time < _lockedUntil)
        {
            _lockedUntil = time + MaxGap; // part of a burst that was rejected: wait for quiet
            Blocked(time);
            return;
        }

        if (_claps == 0)
        {
            // Only after a quiet moment, so the middle of applause or chatter doesn't start one.
            var noisy = time - _lastNoise < NoiseGuard || (time - _lastTick < NoiseGuard && _lastTickHigh > level - TickBelowClaps);
            if (noisy || time - previous < ClapGuard)
            {
                _lockedUntil = time + MaxGap;
                Blocked(time);
                return;
            }

            _blockedClaps = 0;
            _claps = 1;
            _lastClap = time;
            _shortestGap = double.MaxValue;
            _longestGap = 0;
            _quietestClap = level;
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
        _quietestClap = Math.Min(_quietestClap, level);
        if (_claps >= 3 && _longestGap > MaxRhythmRatio * _shortestGap)
        {
            Cancel(time); // no steady rhythm
        }
    }

    /// <summary>A sound that isn't a clap, <paramref name="high"/> and <paramref name="low"/> dB above the background at its peak.</summary>
    private void Noise(double time, double high, double low)
    {
        if (low >= TickLowRise)
        {
            _lastNoise = time;
        }
        else
        {
            // A high tick (a 3D printer, a fan, a click): it counts only if about as loud as the
            // claps, so remember the loudest of the last few.
            _lastTickHigh = time - _lastTick < NoiseGuard ? Math.Max(_lastTickHigh, high) : high;
            _lastTick = time;
        }

        if (_claps > 0 && (low >= TickLowRise || high > _quietestClap - TickBelowClaps))
        {
            Cancel(time);
        }
    }

    private void Cancel(double time)
    {
        _claps = 0;
        _lockedUntil = time + MaxGap;
    }

    /// <summary>A clap that couldn't start or join a sequence: remember the shape of that try.</summary>
    private void Blocked(double time)
    {
        var gap = time - _blockedLast;
        _blockedClaps = gap >= MinGap && gap <= MaxGap ? _blockedClaps + 1 : 1;
        _blockedLast = time;
    }

    /// <summary>The sequence is over: report it (if that count is wanted) and let the next one follow straight away.</summary>
    private void Finish(double now)
    {
        var claps = _claps;
        _claps = 0;
        _lockedUntil = now;
        if (claps >= MinCount)
        {
            _previousClap = double.NegativeInfinity;
        }

        if ((_counts & (1 << claps)) != 0)
        {
            PatternDetected?.Invoke(claps);
        }
    }

    /// <summary>After each block: report the sequence once no further clap can belong to it.</summary>
    private void Advance(double now)
    {
        if (_claps == 0)
        {
            if (_blockedClaps > 0 && !_tracking && now - _blockedLast > BlockedQuiet)
            {
                if (_blockedClaps == 2)
                {
                    // A try that the quiet rules blocked but that was a whole pair (one clap of
                    // the try before was lost, say): the user is trying again, so the next try
                    // may follow straight away.
                    _previousClap = double.NegativeInfinity;
                    _lockedUntil = now;
                }

                _blockedClaps = 0;
            }

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
            Finish(now);
        }
    }

    /// <summary>What a clap must measure up to (dB), per <see cref="ClapSensitivity"/>.</summary>
    /// <param name="AboveFloor">High band peak above the background.</param>
    /// <param name="Rise">High band peak above the level two blocks before.</param>
    /// <param name="MinimumLevel">High band peak level (re full scale).</param>
    /// <param name="MinHighOverLow">High band peak minus low band peak (cupped claps reach -17).</param>
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
        private static readonly Thresholds LowSensitivity = new(28, 18, -45, -21, 4, 12, 24, 12, 10, 120);
        private static readonly Thresholds MediumSensitivity = new(21, 14, -55, -22, 3, 10, 24, 10, 8, 150);
        private static readonly Thresholds HighSensitivity = new(15, 10, -65, -24, 2, 3, 24, 8, 6, 200);

        public static Thresholds For(ClapSensitivity sensitivity) => sensitivity switch
        {
            ClapSensitivity.Low => LowSensitivity,
            ClapSensitivity.High => HighSensitivity,
            _ => MediumSensitivity,
        };
    }

    /// <summary>A second-order Butterworth filter (RBJ cookbook, transposed direct form II).</summary>
    internal struct Biquad
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

            // On silence the state decays into denormal numbers, which are very slow to compute with.
            if (Math.Abs(_z1) < 1e-30)
            {
                _z1 = 0;
            }

            if (Math.Abs(_z2) < 1e-30)
            {
                _z2 = 0;
            }

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
