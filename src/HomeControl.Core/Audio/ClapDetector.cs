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

/// <summary>What <see cref="ClapDetector"/> made of a sound it judged.</summary>
public enum ClapVerdict
{
    /// <summary>A clap, counted towards claps in a row.</summary>
    Clap,

    /// <summary>A clap, but it couldn't start or continue claps in a row: other sounds or claps just before it, or out of step.</summary>
    ClapNotCounted,

    /// <summary>It didn't rise far enough over the room's own sound.</summary>
    TooQuiet,

    /// <summary>It rose again soon after (two sounds in a row, or speech).</summary>
    RoseAgain,

    /// <summary>It lasted (speech, music).</summary>
    Sustained,

    /// <summary>It stopped dead, with no room echo (a click next to the microphone).</summary>
    NoRoomTail,

    /// <summary>It didn't fade away like a clap.</summary>
    DidntFade,

    /// <summary>Mostly low-pitched (a knock or a thump).</summary>
    TooLowPitched,

    /// <summary>Dull, with more body than high band, and not loud (a footstep, something put down).</summary>
    Thud,

    /// <summary>Only high-pitched, with no lower body (a click or a machine's tick).</summary>
    NoBody,

    /// <summary>The PC's own speakers had just played a sound as sharp.</summary>
    FromSpeakers,
}

/// <summary>
/// A few numbers about a sound the detector judged, to show why it did or didn't count (no
/// sound). Levels in dB.
/// </summary>
/// <param name="Verdict">What it made of it.</param>
/// <param name="High">How far its high band (2–7 kHz) rose over the room's background.</param>
/// <param name="Body">How far its body (500 Hz–1.2 kHz) rose over the room's background.</param>
/// <param name="BodyOverHigh">Its own body against its own high band (the room's share taken out of both).</param>
/// <param name="HighOverLow">Its own high band against its own sound below 2 kHz.</param>
/// <param name="Drop">How much it had dropped 40 ms after its peak (NaN if judged before then).</param>
/// <param name="Peak">Its high band's peak, re full scale.</param>
/// <param name="Rise">How sharply its high band rose over what came just before (what the speakers' sounds are compared with).</param>
public readonly record struct ClapJudgement(
    ClapVerdict Verdict, double High, double Body, double BodyOverHigh, double HighOverLow, double Drop, double Peak, double Rise);

/// <summary>
/// <see cref="ClapDetector"/>'s working state at the start of a 10 ms block, enough to carry on
/// judging from there (part of a saved clap event). Levels in dB; ages in blocks before it.
/// </summary>
/// <param name="FloorHigh">The room's background in the high band (2–7 kHz); NaN until it is learnt.</param>
/// <param name="FloorLow">... below 2 kHz.</param>
/// <param name="FloorBody">... in the body band (500 Hz–1.2 kHz).</param>
/// <param name="SteadyHigh">The high band's background as it was in the last steady block (before a fade into silence).</param>
/// <param name="SteadyLow">... below 2 kHz.</param>
/// <param name="SteadyBody">... in the body band.</param>
/// <param name="WasSilent">The block before was digital silence.</param>
/// <param name="Jitter">How much the body band's background moves from block to block.</param>
/// <param name="High1">The high band one block before.</param>
/// <param name="High2">... two blocks before.</param>
/// <param name="Low1">Below 2 kHz one block before.</param>
/// <param name="Low2">... two blocks before.</param>
/// <param name="Body1">The body band one block before.</param>
/// <param name="LearnFor">Blocks left of learning the background.</param>
/// <param name="Tracking">A sound is being judged.</param>
/// <param name="Decayed">It has faded.</param>
/// <param name="OnsetAge">How long ago it began.</param>
/// <param name="PeakAge">How long ago it peaked.</param>
/// <param name="PeakHigh">Its peak in the high band.</param>
/// <param name="PeakLow">... below 2 kHz.</param>
/// <param name="PeakBody">... in the body band.</param>
/// <param name="OnsetReference">What its rise is measured from.</param>
/// <param name="Drop">How much it had dropped 40 ms after its peak (NaN before then).</param>
public readonly record struct ClapDetectorState(
    double FloorHigh, double FloorLow, double FloorBody, double SteadyHigh, double SteadyLow, double SteadyBody, bool WasSilent, double Jitter,
    double High1, double High2, double Low1, double Low2, double Body1, long LearnFor,
    bool Tracking, bool Decayed, long OnsetAge, long PeakAge, double PeakHigh, double PeakLow, double PeakBody, double OnsetReference, double Drop);

/// <summary>
/// Hears claps in microphone sound and recognises 2, 3 or 4 claps in a row.
/// </summary>
/// <remarks>
/// <para>
/// It keeps no sound. Each sample goes through a few filters and is added to three running
/// sums, then forgotten; every 10 ms the sums become three loudness figures (2–7 kHz, the body
/// band 500 Hz–1.2 kHz, and below 2 kHz) and start again. All it remembers is a handful of
/// numbers: the filters' state (made from the last two samples), the background noise level,
/// the last two blocks' loudness and the timing and loudness of the current clap, sequence and
/// last other sounds. <see cref="Reset"/> clears them.
/// </para>
/// <para>
/// A clap is a sharp rise in the high band that dies away like a clap in a room: by 40 ms after
/// its peak it has dropped by a few dB (speech and music don't) but still has a loud room tail
/// (clicks next to the microphone don't), it has faded within about 150 ms, nothing rises again
/// during an 80 ms hold, and the high band isn't far quieter than the low band (knocks and
/// thumps are; claps with cupped hands can be 17 dB duller, but only loud ones close by are
/// also bodied: a quieter dull sound whose body outweighs its high band is a thud, a footstep
/// or something put down). It also has a body: hands meeting sound from about 500 Hz up, so
/// the body band rises over its background, clearly more than that background moves by
/// itself, and isn't far quieter than the high band, while clicks, taps and a machine's ticks
/// (a 3D printer's) are only high-pitched. The balances between bands are of the sound's own
/// energy, with the room's share taken out, so a loud hum or whine doesn't make claps look
/// dull or bodiless.
/// Claps in a row are 200–450 ms apart in a steady rhythm (no gap over 1.75 times another). A
/// sequence starts only after 0.8 s without claps (or right after a finished sequence, so a
/// pattern can be repeated), 0.3 s without other sounds and a second without other sharp
/// sounds within 12 dB of its first clap (taps, clicks, things put down: deliberate claps stand
/// out), any other sound that stands out during it cancels it, and it is reported once it is
/// clearly over, so three claps never also count as two (a clap too late for the rhythm ends
/// the sequence and may start the next). A try that was blocked but was a whole pair lets the
/// next try follow straight away. Sounds that don't stand out from the room, faint high ticks
/// (the low band hardly rises and the high band stays 10 dB or more below the claps), sounds
/// with no body that stay a few dB below the claps, and voices, music or knocks 18 dB or more
/// below them don't count as other sounds; anything louder does.
/// </para>
/// <para>
/// A clap that starts when a sound from the PC's own speakers may be reaching the microphone,
/// and rises no more sharply than that sound did (see <see cref="Speakers"/>), is taken for
/// theirs, not the user's.
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
    private const double MinGap = 0.20;    // s: claps in a row are this far apart at least (quicker: a bounce, a double click) ...
    private const double MaxGap = 0.45;    // ... and at most (slower: two sounds, not a double clap)
    private const double MaxRhythmRatio = 1.75;
    private const double NoiseGuard = 0.30;
    private const double BusySeconds = 1.0;    // s: a sharp sound this little before a clap ...
    private const double BusyBelowClaps = 12;  // dB: ... and no further below it than this makes it doubtful (taps, clicks, things put down)
    private const double SoundBelowClaps = 18; // dB: a sound with a low part (speech, music, a knock) this far below the claps doesn't count
    private const double DryTail = 8;          // dB: a sound that drops very fast is still a clap if its tail stays this loud ...
    private const double DryDropMargin = 4;    // dB: ... and it drops no more than this much faster than a clap may
    private const double DullHighOverLow = -10; // dB: a sound whose high band is this far below its low part ...
    private const double ThudBodyOverHigh = 3;  // ... and whose body is this much louder than its high band is a thud ...
    private const double ThudAbove = 36;        // ... unless it rose this far over the room (cupped claps close by are as dull)
    private const double ClapGuard = 0.80;
    private const double ClosingQuiet = 0.25;
    private const double OnsetSeconds = 0.04;  // a sound may still grow this long after it starts
    private const double BodyFrom = 500;      // Hz: the body of a clap (the hands' hollow, the slap of palms), above most hum ...
    private const double BodyTo = 1200;       // ... and below the ring of clicks and machine ticks
    private const double TickLowRise = 6;     // dB: a sound whose low band rises less than this is a high tick ...
    private const double TickBelowClaps = 10; // dB: ... and a tick this far below the claps doesn't count ...
    private const double FaintBelowClaps = 3; // dB: ... nor one with no body this far below
    private const double BlockedQuiet = 0.45; // s: a blocked try is over after this long without claps

    private readonly int _blockLength;
    private readonly double _blockSeconds;
    private readonly double _floorRise;
    private readonly double _floorFall;
    private volatile Thresholds _thresholds;
    private volatile int _counts;
    private volatile SpeakerSounds? _speakers;
    private volatile ClapHistory? _history;
    private ClapSensitivity _sensitivity;

    // Front end: band filters and this block's running energy.
    private Biquad _lowPass;
    private Biquad _highPass;
    private Biquad _highBand;
    private Biquad _bodyHighPass1;
    private Biquad _bodyHighPass2;
    private Biquad _bodyLowPass1;
    private Biquad _bodyLowPass2;
    private double _sumHigh;
    private double _sumLow;
    private double _sumBody;
    private int _samplesInBlock;
    private long _block;
    private long _learnUntil;

    // The shared clock (seconds) at sample 0, from the last Process call that gave one.
    private double _clockOffset;

    // Levels in dB: background noise per band (and as it was in the last steady block, before
    // any fade into silence), and the two blocks before this one.
    private double _floorHigh;
    private double _floorLow;
    private double _floorBody;
    private double _steadyFloorHigh;
    private double _steadyFloorLow;
    private double _steadyFloorBody;
    private bool _wasSilent;
    private double _high1;
    private double _high2;
    private double _low1;
    private double _low2;
    private double _body1;
    private double _bodyJitter; // how much the body band's background moves from block to block (dB)

    // The sound being judged.
    private bool _tracking;
    private bool _decayed;
    private long _onsetBlock;
    private long _peakBlock;
    private double _peakHigh;
    private double _peakLow;
    private double _peakBody;
    private double _onsetReference;
    private double _drop;

    // The claps in a row so far, and the sounds around them (levels in dB above the background).
    private int _claps;
    private double _lastClap;
    private double _shortestGap;
    private double _longestGap;
    private double _quietestClap;
    private double _previousClap;
    private double _lastNoise;
    private double _lastSoundLevel;
    private double _lastTick;
    private double _lastTickHigh;
    private RecentImpulses _impulses;
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

    /// <summary>
    /// Every sound it judged, clap or not, with a few numbers about it (no sound): to show the
    /// user why a clap did or didn't count.
    /// </summary>
    public event Action<ClapJudgement>? SoundJudged;

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

    /// <summary>
    /// Keeps what it worked out over the last half minute, for the clap event log (no sound).
    /// Null: nothing is kept. Set it before the first <see cref="Process(ReadOnlySpan{float})"/>.
    /// </summary>
    public ClapHistory? History
    {
        get => _history;
        set
        {
            value?.Attach(_blockSeconds, SampleRate);
            _history = value;
        }
    }

    /// <summary>Replays only: whether the speakers explain a sound that started at this block, rising this sharply.</summary>
    internal Func<long, double, bool>? SpeakerCheck { get; set; }

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
            var body = _bodyLowPass2.Step(_bodyLowPass1.Step(_bodyHighPass2.Step(_bodyHighPass1.Step(full))));
            _sumHigh += high * high;
            _sumLow += low * low;
            _sumBody += body * body;
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
        _history?.Interrupted(_block);
        _tracking = false;
        Noise(_block * _blockSeconds, double.PositiveInfinity, double.PositiveInfinity);
    }

    /// <summary>Forgets everything: the filters, the background level and any claps so far.</summary>
    public void Reset()
    {
        ResetFilters();
        _sumHigh = _sumLow = _sumBody = 0;
        _samplesInBlock = 0;
        _block = 0;
        _clockOffset = 0;
        _learnUntil = Blocks(WarmUpSeconds);
        _floorHigh = _floorLow = _floorBody = _steadyFloorHigh = _steadyFloorLow = _steadyFloorBody = double.NaN;
        _wasSilent = false;
        _high1 = _high2 = _low1 = _low2 = _body1 = -200;
        _bodyJitter = double.NaN;
        _tracking = _decayed = false;
        _onsetBlock = _peakBlock = 0;
        _peakHigh = _peakLow = _peakBody = _onsetReference = 0;
        _drop = double.NaN;
        _claps = 0;
        _lastClap = _shortestGap = _longestGap = _quietestClap = _lastTickHigh = _lastSoundLevel = 0;
        _previousClap = _lastNoise = _lastTick = _lockedUntil = _blockedLast = double.NegativeInfinity;
        _impulses = RecentImpulses.None;
        _blockedClaps = 0;
    }

    private void ResetFilters()
    {
        var rate = (double)SampleRate;
        _lowPass = Biquad.LowPass(rate, Math.Min(7000, 0.45 * rate));
        _highPass = Biquad.HighPass(rate, 60);
        _highBand = Biquad.HighPass(rate, 2000);
        _bodyHighPass1 = _bodyHighPass2 = Biquad.HighPass(rate, BodyFrom);
        _bodyLowPass1 = _bodyLowPass2 = Biquad.LowPass(rate, BodyTo);
    }

    private static double Decibels(double energy) => 10 * Math.Log10(energy + 1e-20);

    private long Blocks(double seconds) => (long)Math.Round(seconds / _blockSeconds);

    private void EndBlock()
    {
        var energyHigh = _sumHigh / _samplesInBlock;
        var energyLow = _sumLow / _samplesInBlock;
        var high = Decibels(energyHigh);
        var low = Decibels(energyLow);
        var body = Decibels(_sumBody / _samplesInBlock);
        _sumHigh = _sumLow = _sumBody = 0;
        _samplesInBlock = 0;

        // Digital silence (muted, a dropout, a noise gate, or a very quiet room through a 16-bit
        // microphone) says nothing about the room: it never moves the background level, which
        // would otherwise sink far below the room and let dry clicks pass for claps.
        var silent = energyHigh + energyLow < SilentEnergy;
        if (!double.IsFinite(high) || !double.IsFinite(low) || !double.IsFinite(body))
        {
            ResetFilters(); // overflowed: start the filters again, and drop what was being judged
            Interrupt();
            high = low = body = -200;
            silent = true;
        }

        ProcessLevels(high, low, body, silent);
    }

    /// <summary>
    /// Judges one 10 ms block from its three loudness figures (dB): all the detector does after
    /// its filters. Replays of saved clap events feed their blocks here.
    /// </summary>
    internal void ProcessLevels(double high, double low, double body, bool silent)
    {
        _history?.Block(_block, State(), high, low, body, silent, _speakers?.RiseAt(_block * _blockSeconds + _clockOffset));
        var thresholds = _thresholds;
        if (silent && !_wasSilent && !double.IsNaN(_floorHigh) && !double.IsNaN(_steadyFloorHigh))
        {
            // The fade into silence (the filters ringing down) isn't the room either.
            _floorHigh = Math.Max(_floorHigh, _steadyFloorHigh);
            _floorLow = Math.Max(_floorLow, _steadyFloorLow);
            _floorBody = Math.Max(_floorBody, _steadyFloorBody);
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
                RememberSteadyFloors(high, low, body);
                _floorHigh = Math.Max(-120, double.IsNaN(_floorHigh) ? high : _floorHigh + 0.2 * (high - _floorHigh));
                _floorLow = Math.Max(-120, double.IsNaN(_floorLow) ? low : _floorLow + 0.2 * (low - _floorLow));
                _floorBody = Math.Max(-120, double.IsNaN(_floorBody) ? body : _floorBody + 0.2 * (body - _floorBody));
            }
        }
        else if (_tracking)
        {
            Track(high, low, body, thresholds);
        }
        else if (!silent)
        {
            TryStart(high, low, body, thresholds);
        }

        if (!silent && !_tracking && _body1 > -200)
        {
            // How much the body band's background moves by itself: a body must rise clearly more.
            // (Steady hum or whine hardly moves; noise moves about 2 dB from one 10 ms to the next.)
            var moved = Math.Min(6, Math.Abs(body - _body1));
            _bodyJitter = double.IsNaN(_bodyJitter) ? moved : _bodyJitter + 0.02 * (moved - _bodyJitter);
        }

        _high2 = _high1;
        _high1 = high;
        _low2 = _low1;
        _low1 = low;
        _body1 = silent ? -200 : body;
        _block++;
        Advance(_block * _blockSeconds);
    }

    /// <summary>Background levels follow the quiet moments: they fall fast and rise slowly.</summary>
    private void UpdateFloors(double high, double low, double body)
    {
        RememberSteadyFloors(high, low, body);
        _floorHigh = Math.Max(-120, _floorHigh + (high < _floorHigh ? _floorFall : _floorRise) * (high - _floorHigh));
        _floorLow = Math.Max(-120, _floorLow + (low < _floorLow ? _floorFall : _floorRise) * (low - _floorLow));
        _floorBody = Math.Max(-120, _floorBody + (body < _floorBody ? _floorFall : _floorRise) * (body - _floorBody));
    }

    /// <summary>A block close to the background is ordinary room sound: remember the background as it is.</summary>
    private void RememberSteadyFloors(double high, double low, double body)
    {
        if (!double.IsNaN(_floorHigh) && Math.Abs(high - _floorHigh) <= 6 && Math.Abs(low - _floorLow) <= 6)
        {
            _steadyFloorHigh = _floorHigh;
            _steadyFloorLow = _floorLow;
            _steadyFloorBody = _floorBody;
        }
    }

    private void TryStart(double high, double low, double body, Thresholds t)
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
            _peakBody = body;
            _onsetReference = reference;
            _drop = double.NaN;
            return;
        }

        // A rise in the low band alone (speech, a knock, a thump) is a sound but not a clap.
        if (low - _floorLow >= t.AboveFloor && low - Math.Max(_low2, _floorLow) >= t.Rise)
        {
            _history?.Noise(_block, high - _floorHigh, low - _floorLow);
            Noise(_block * _blockSeconds, high - _floorHigh, low - _floorLow);
        }

        UpdateFloors(high, low, body);
    }

    private void Track(double high, double low, double body, Thresholds t)
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
            _peakBody = Math.Max(_peakBody, body);
            if (age == 2 && !(_peakHigh - _floorHigh >= t.AboveFloor && _peakHigh - _onsetReference >= t.Rise && _peakHigh >= t.MinimumLevel))
            {
                Reject(t, ClapVerdict.TooQuiet);
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
            _peakBody = Math.Max(_peakBody, body);
            return;
        }

        // It rises again (a plosive followed by a vowel, a second click): not a clap; maybe the
        // start of something new. (A rise from before the peak is the peak's own.)
        if (high > _peakHigh + 3 || (low > _peakLow + 3 && low - _floorLow >= TickLowRise)
            || (_block - 2 > _peakBlock && high - Math.Max(_high2, _floorHigh) >= Math.Min(t.Rise, 10)))
        {
            Reject(t, ClapVerdict.RoseAgain);
            TryStart(high, low, body, t);
            return;
        }

        var sincePeak = _block - _peakBlock;
        if (sincePeak == Blocks(TailCheckSeconds))
        {
            _drop = _peakHigh - high;
            if (_drop < t.MinDrop)
            {
                Reject(t, ClapVerdict.Sustained); // speech, music
                return;
            }

            // A sharp drop alone isn't a click: a loud clap close to the microphone drops a little
            // faster than a clap may, but its room tail stays well above the background.
            var dry = _drop > t.MaxDrop && (high - _floorHigh < DryTail || _drop > t.MaxDrop + DryDropMargin);
            if (high - _floorHigh < t.MinTail || dry)
            {
                Reject(t, ClapVerdict.NoRoomTail); // a keyboard or mouse click
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
                Reject(t, ClapVerdict.DidntFade);
                return;
            }
        }

        if (_decayed && sincePeak >= Blocks(TailCheckSeconds) && age >= Blocks(HoldSeconds))
        {
            if (HighOverLow() < t.MinHighOverLow)
            {
                Reject(t, ClapVerdict.TooLowPitched); // a knock or a thump
            }
            else if (HighOverLow() <= DullHighOverLow && BodyOverHigh() >= ThudBodyOverHigh && _peakHigh - _floorHigh < ThudAbove)
            {
                Reject(t, ClapVerdict.Thud); // a footstep, something put down
            }
            else if (_peakBody - _floorBody < MinBodyRise(t) || BodyOverHigh() < t.MinBodyOverHigh)
            {
                Reject(t, ClapVerdict.NoBody); // a click, a machine's tick
            }
            else if (FromSpeakers())
            {
                Reject(t, ClapVerdict.FromSpeakers);
            }
            else
            {
                _tracking = false;
                Clap(_onsetBlock * _blockSeconds, _peakHigh - _floorHigh);
            }
        }
    }

    /// <summary>
    /// The sound's own level in a band: its peak less the background's share (dB), so a loud
    /// hum or music in that band doesn't count as the sound's. A sound that hardly rose has
    /// little of its own.
    /// </summary>
    private static double Excess(double peak, double floor) =>
        peak - floor < 0.05 ? peak - 20 : peak + 10 * Math.Log10(1 - Math.Pow(10, (floor - peak) / 10));

    /// <summary>How far the body band must rise to be the sound's own and not the background moving (dB).</summary>
    private double MinBodyRise(Thresholds t) => Math.Max(t.MinBody, t.BodyOverJitter * (double.IsNaN(_bodyJitter) ? 2 : _bodyJitter));

    /// <summary>The sound's own body against its own high band (dB).</summary>
    private double BodyOverHigh() => Excess(_peakBody, _floorBody) - Excess(_peakHigh, _floorHigh);

    /// <summary>The sound's own high band against its own sound below 2 kHz (dB).</summary>
    private double HighOverLow() => Excess(_peakHigh, _floorHigh) - Excess(_peakLow, _floorLow);

    /// <summary>Whether the sound being judged may have come from the PC's own speakers (as sharply as it rose).</summary>
    private bool FromSpeakers() =>
        SpeakerCheck is { } check
            ? check(_onsetBlock, _peakHigh - _onsetReference)
            : _speakers is { } speakers && speakers.MayExplain(_onsetBlock * _blockSeconds + _clockOffset, _peakHigh - _onsetReference);

    /// <summary>
    /// Not a clap. It interrupts claps only if it stood out from the room: the faint ticks and
    /// whirs of a machine, quiet next to the machine's own hum, don't.
    /// </summary>
    private void Reject(Thresholds t, ClapVerdict why)
    {
        _tracking = false;
        Judged(why);
        Rejected(t, why, _onsetBlock * _blockSeconds, _peakHigh - _floorHigh, _peakLow - _floorLow);
    }

    /// <summary>
    /// What a sound that isn't a clap does to claps in a row, from its verdict and how far its
    /// high band and the band below 2 kHz rose over the background (dB).
    /// </summary>
    private void Rejected(Thresholds t, ClapVerdict why, double time, double high, double low)
    {
        // Anything with a low part (a knock, a thump) always counts; the tick rules in Noise
        // decide about the rest. A sound with no body is a tick whatever its low band did, and
        // has to be about as loud as the claps to spoil them.
        if (high >= t.AboveFloor || low >= TickLowRise)
        {
            Noise(time, high, low, faint: why == ClapVerdict.NoBody);
        }

        // Any sharp sound (a tap, a click, something put down; not speech or music, nor what the
        // speakers played) is remembered for a moment, quiet or not: claps among sounds like them
        // are doubtful. One with no body has to be about as loud as the claps to count.
        if (why is not (ClapVerdict.RoseAgain or ClapVerdict.Sustained or ClapVerdict.FromSpeakers))
        {
            var level = Math.Max(high, low);
            _impulses.Add(time, why == ClapVerdict.NoBody ? level - (BusyBelowClaps - FaintBelowClaps) : level);
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

    private void Judged(ClapVerdict verdict)
    {
        var judgement = new ClapJudgement(verdict, _peakHigh - _floorHigh, _peakBody - _floorBody, BodyOverHigh(), HighOverLow(),
            _drop, _peakHigh, _peakHigh - _onsetReference);
        _history?.Judged(_onsetBlock, _block, judgement, _peakLow - _floorLow, _speakers?.RiseAt(_onsetBlock * _blockSeconds + _clockOffset));
        SoundJudged?.Invoke(judgement);
    }

    /// <param name="level">The clap's high band peak above the background (dB).</param>
    private void Clap(double time, double level)
    {
        ClapHeard?.Invoke();
        Judged(CountClap(time, level) ? ClapVerdict.Clap : ClapVerdict.ClapNotCounted);
    }

    /// <summary>Adds a clap to claps in a row: false if it couldn't start or continue them.</summary>
    private bool CountClap(double time, double level)
    {
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
            return false;
        }

        // Deliberate claps stand out: a sharp sound in the second before, not far below this clap,
        // makes it one of many such sounds (typing, tidying up, a machine), not a clap. For the
        // next claps, only the sounds since the last one count: those before it were weighed then.
        var busy = _impulses.Loudest(_claps == 0 ? time - BusySeconds : _lastClap + _blockSeconds / 2) >= level - BusyBelowClaps;
        if (_claps == 0)
        {
            // Only after a quiet moment, so the middle of applause or chatter doesn't start one.
            var noisy = (time - _lastNoise < NoiseGuard && _lastSoundLevel > level - SoundBelowClaps)
                        || (time - _lastTick < NoiseGuard && _lastTickHigh > level - TickBelowClaps);
            if (noisy || busy || time - previous < ClapGuard)
            {
                _lockedUntil = time + MaxGap;
                Blocked(time);
                return false;
            }

            _blockedClaps = 0;
            _claps = 1;
            _lastClap = time;
            _shortestGap = double.MaxValue;
            _longestGap = 0;
            _quietestClap = level;
            return true;
        }

        var gap = time - _lastClap;
        if (!InStep(gap) || _claps >= Largest() || busy)
        {
            Cancel(time);
            return false;
        }

        _claps++;
        _lastClap = time;
        _shortestGap = Math.Min(_shortestGap, gap);
        _longestGap = Math.Max(_longestGap, gap);
        _quietestClap = Math.Min(_quietestClap, level);
        if (_claps >= 3 && _longestGap > MaxRhythmRatio * _shortestGap)
        {
            Cancel(time); // no steady rhythm
            return false;
        }

        return true;
    }

    /// <summary>
    /// A sound that isn't a clap, <paramref name="high"/> and <paramref name="low"/> dB above the
    /// background at its peak. <paramref name="faint"/>: a tick whatever its low band did (it has
    /// no body), which has to be about as loud as the claps to count, not merely within 10 dB.
    /// </summary>
    private void Noise(double time, double high, double low, bool faint = false)
    {
        var sound = !faint && low >= TickLowRise;
        if (sound)
        {
            // Speech, music, a knock: it counts unless it is far quieter than the claps (music
            // playing softly doesn't stop them), so remember the loudest of the last few.
            var loudness = Math.Max(high, low);
            _lastSoundLevel = time - _lastNoise < NoiseGuard ? Math.Max(_lastSoundLevel, loudness) : loudness;
            _lastNoise = time;
        }
        else
        {
            // A high tick (a 3D printer, a fan, a click): it counts only if about as loud as the
            // claps, so remember the loudest of the last few.
            if (faint)
            {
                high -= TickBelowClaps - FaintBelowClaps;
            }

            _lastTickHigh = time - _lastTick < NoiseGuard ? Math.Max(_lastTickHigh, high) : high;
            _lastTick = time;
        }

        if (_claps > 0 && (sound ? Math.Max(high, low) > _quietestClap - SoundBelowClaps : high > _quietestClap - TickBelowClaps))
        {
            Cancel(time);
        }
    }

    /// <summary>Whether claps this far apart can be claps in a row (to the nearest half block: times come in 10 ms blocks).</summary>
    private bool InStep(double gap) => gap >= MinGap - _blockSeconds / 2 && gap <= MaxGap + _blockSeconds / 2;

    private void Cancel(double time)
    {
        _claps = 0;
        _lockedUntil = time + MaxGap;
    }

    /// <summary>A clap that couldn't start or join a sequence: remember the shape of that try.</summary>
    private void Blocked(double time)
    {
        var gap = time - _blockedLast;
        _blockedClaps = InStep(gap) ? _blockedClaps + 1 : 1;
        _blockedLast = time;
    }

    /// <summary>The sequence is over: report it (if that count is wanted) and let the next one follow straight away.</summary>
    private void Finish(double now)
    {
        var claps = _claps;
        var wanted = (_counts & (1 << claps)) != 0;
        _claps = 0;
        _lockedUntil = now;
        if (claps >= MinCount)
        {
            _previousClap = double.NegativeInfinity;
            _history?.Pattern(_block, claps, wanted);
        }

        if (wanted)
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

    // ---------------------------------------------------------------- saved clap events

    /// <summary>Its working state now, at the start of the next block.</summary>
    internal ClapDetectorState State() => new(
        _floorHigh, _floorLow, _floorBody, _steadyFloorHigh, _steadyFloorLow, _steadyFloorBody, _wasSilent, _bodyJitter,
        _high1, _high2, _low1, _low2, _body1, _learnUntil - _block,
        _tracking, _decayed, _block - _onsetBlock, _block - _peakBlock, _peakHigh, _peakLow, _peakBody, _onsetReference, _drop);

    /// <summary>Replays: starts afresh at a block, as if it had been listening up to then with nothing heard.</summary>
    internal void StartAt(long block)
    {
        Reset();
        _block = block;
        _learnUntil = block + Blocks(WarmUpSeconds);
    }

    /// <summary>Replays: takes up the state the detector had at the start of this block (not the claps in a row so far).</summary>
    internal void Restore(ClapDetectorState state)
    {
        (_floorHigh, _floorLow, _floorBody) = (state.FloorHigh, state.FloorLow, state.FloorBody);
        (_steadyFloorHigh, _steadyFloorLow, _steadyFloorBody) = (state.SteadyHigh, state.SteadyLow, state.SteadyBody);
        _wasSilent = state.WasSilent;
        _bodyJitter = state.Jitter;
        (_high1, _high2, _low1, _low2, _body1) = (state.High1, state.High2, state.Low1, state.Low2, state.Body1);
        _learnUntil = _block + state.LearnFor;
        _tracking = state.Tracking;
        _decayed = state.Decayed;
        _onsetBlock = _block - state.OnsetAge;
        _peakBlock = _block - state.PeakAge;
        (_peakHigh, _peakLow, _peakBody, _onsetReference, _drop) = (state.PeakHigh, state.PeakLow, state.PeakBody, state.OnsetReference, state.Drop);
    }

    /// <summary>Replays: the block a sound being judged began at; null when none is.</summary>
    internal long? Tracking => _tracking ? _onsetBlock : null;

    /// <summary>Replays: stops judging a sound whose blocks ran out, without a verdict.</summary>
    internal void ReplayAbandon() => _tracking = false;

    /// <summary>Replays: a block whose levels weren't kept goes by (claps in a row still end on time).</summary>
    internal void SkipBlock()
    {
        _block++;
        Advance(_block * _blockSeconds);
    }

    /// <summary>Replays: a sound whose blocks weren't kept begins to be judged at this block.</summary>
    internal void ReplayOnset()
    {
        _tracking = true;
        _decayed = false;
        _onsetBlock = _peakBlock = _block;
        _drop = double.NaN;
    }

    /// <summary>Replays: the verdict on a sound whose blocks weren't kept, with what it does to claps in a row.</summary>
    internal void ReplayVerdict(long onsetBlock, ClapJudgement judgement, double lowRise)
    {
        _tracking = false;
        _onsetBlock = onsetBlock;
        var time = onsetBlock * _blockSeconds;
        if (judgement.Verdict is ClapVerdict.Clap or ClapVerdict.ClapNotCounted)
        {
            ClapHeard?.Invoke();
            var counted = CountClap(time, judgement.High);
            var verdict = counted ? ClapVerdict.Clap : ClapVerdict.ClapNotCounted;
            _history?.Judged(onsetBlock, _block, judgement with { Verdict = verdict }, lowRise, null);
            SoundJudged?.Invoke(judgement with { Verdict = verdict });
            return;
        }

        _history?.Judged(onsetBlock, _block, judgement, lowRise, null);
        SoundJudged?.Invoke(judgement);
        Rejected(_thresholds, judgement.Verdict, time, judgement.High, lowRise);
    }

    /// <summary>Replays: a rise of the low band alone at this block (speech, a knock), whose blocks weren't kept.</summary>
    internal void ReplayNoise(double high, double low)
    {
        _history?.Noise(_block, high, low);
        Noise(_block * _blockSeconds, high, low);
    }

    /// <summary>What a clap must measure up to (dB), per <see cref="ClapSensitivity"/>.</summary>
    /// <param name="AboveFloor">High band peak above the background.</param>
    /// <param name="Rise">High band peak above the level two blocks before.</param>
    /// <param name="MinimumLevel">High band peak level (re full scale).</param>
    /// <param name="MinHighOverLow">High band peak minus low band peak (cupped claps reach -17).</param>
    /// <param name="MinBody">Body band (500 Hz–1.2 kHz) peak above its background, at least (less: a click or a tick, not a clap).</param>
    /// <param name="BodyOverJitter">... and at least this many times how much that background moves by itself.</param>
    /// <param name="MinBodyOverHigh">The sound's own body minus its own high band (claps reach -14; clicks and ticks, -22 and less).</param>
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
        double MinBody,
        double BodyOverJitter,
        double MinBodyOverHigh,
        double MinDrop,
        double MinTail,
        double MaxDrop,
        double DecayHigh,
        double DecayLow,
        double DecayWindowMs)
    {
        private static readonly Thresholds LowSensitivity = new(28, 18, -55, -21, 5, 5.0, -18, 4, 12, 24, 12, 10, 120);
        private static readonly Thresholds MediumSensitivity = new(21, 14, -62, -22, 3, 3.5, -19, 3, 10, 24, 10, 8, 150);
        private static readonly Thresholds HighSensitivity = new(15, 10, -70, -24, 2.5, 3.5, -20, 2, 3, 24, 8, 6, 200);

        public static Thresholds For(ClapSensitivity sensitivity) => sensitivity switch
        {
            ClapSensitivity.Low => LowSensitivity,
            ClapSensitivity.High => HighSensitivity,
            _ => MediumSensitivity,
        };
    }

    /// <summary>
    /// The last four sharp sounds that weren't claps: when (s) and how loud (dB over the
    /// background). Single numbers only, like everything else the detector keeps.
    /// </summary>
    internal struct RecentImpulses
    {
        public static readonly RecentImpulses None = new()
        {
            _time0 = double.NegativeInfinity,
            _time1 = double.NegativeInfinity,
            _time2 = double.NegativeInfinity,
            _time3 = double.NegativeInfinity,
        };

        private double _time0, _time1, _time2, _time3;
        private double _level0, _level1, _level2, _level3;
        private int _next;

        public void Add(double time, double level)
        {
            switch (_next)
            {
                case 0: (_time0, _level0) = (time, level); break;
                case 1: (_time1, _level1) = (time, level); break;
                case 2: (_time2, _level2) = (time, level); break;
                default: (_time3, _level3) = (time, level); break;
            }

            _next = (_next + 1) % 4;
        }

        /// <summary>The loudest since <paramref name="from"/>; minus infinity if none.</summary>
        public readonly double Loudest(double from) => Math.Max(
            Math.Max(_time0 >= from ? _level0 : double.NegativeInfinity, _time1 >= from ? _level1 : double.NegativeInfinity),
            Math.Max(_time2 >= from ? _level2 : double.NegativeInfinity, _time3 >= from ? _level3 : double.NegativeInfinity));
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
