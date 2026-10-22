namespace HomeControl.Core.Audio;

/// <summary>
/// What <see cref="ClapDetector"/> worked out over the last half minute, kept in memory so that a
/// clap event (claps heard, or claps the user says it missed) can be saved and looked at later:
/// every sound it judged and why, and for most of them their loudness every 10 ms while they
/// were judged. No sound.
/// </summary>
/// <remarks>
/// <para>
/// All it keeps are the detector's own figures. The detector reduces each 10 ms of sound to the
/// loudness of three bands (2–7 kHz, 500 Hz–1.2 kHz and below 2 kHz); this keeps those three
/// numbers for the last 64 blocks only, to cut out the blocks of each sound the detector
/// judges: from just before it rose until a little after its verdict (so a replay with other
/// thresholds, which may judge it a moment later, has what it needs), under half a second.
/// Sounds judged to be speech or music (they rose again, or lasted) keep their verdict and a
/// few figures, not their blocks. Every half second it also notes the room's background and
/// the loudest moment. That is far too little to play anything back or to make out speech.
/// </para>
/// <para>
/// The detector feeds it on its own thread; <see cref="Snapshot"/> may be called from any thread.
/// </para>
/// </remarks>
public sealed class ClapHistory
{
    /// <summary>How far back it keeps what the detector worked out.</summary>
    public const double KeepSeconds = 30;

    private const int RingSize = 64;      // blocks: more than the longest a sound is judged for
    private const int ContextBlocks = 50; // half a second
    private const int BeforeOnset = 3;    // blocks kept before a sound rose ...
    private const int AfterVerdict = 8;   // ... and after its verdict

    private readonly object _gate = new();
    private readonly double _keepSeconds;
    private readonly Entry[] _ring = new Entry[RingSize];
    private readonly List<ClapSegment> _segments = [];
    private readonly List<ClapSoundRecord> _sounds = [];
    private readonly List<ClapNoiseRecord> _noises = [];
    private readonly List<long> _interruptions = [];
    private readonly List<ClapPatternRecord> _patterns = [];
    private readonly List<ClapContextRecord> _context = [];
    private ClapContextRecord? _half;
    private double _blockSeconds = 0.01;
    private int _sampleRate;
    private long _keepBlocks = 3000;
    private long _first = -1;
    private long _last = -1;
    private long _keepUntil = -1; // blocks up to this one are kept as they come

    public ClapHistory()
        : this(KeepSeconds)
    {
    }

    /// <param name="keepSeconds">How far back to keep (replays keep everything).</param>
    internal ClapHistory(double keepSeconds)
    {
        _keepSeconds = keepSeconds;
        for (var i = 0; i < RingSize; i++)
        {
            _ring[i].Index = -1;
        }
    }

    /// <summary>Copies what it kept, from up to <see cref="KeepSeconds"/> ago until now.</summary>
    public ClapRecording Snapshot()
    {
        lock (_gate)
        {
            var from = Math.Max(_first, _last - _keepBlocks + 1);
            var segments = _segments.Where(s => s.End >= from).Select(s => s.Copy()).ToList();
            if (segments.Count > 0)
            {
                from = Math.Min(from, segments[0].Start);
            }

            var context = _context.Where(c => c.Start + ContextBlocks > from).Select(c => c.Copy()).ToList();
            if (_half is not null)
            {
                context.Add(_half.Copy());
            }

            return new ClapRecording
            {
                BlockSeconds = _blockSeconds,
                SampleRate = _sampleRate,
                FromBlock = Math.Max(0, from),
                ToBlock = _last,
                Segments = segments,
                Sounds = _sounds.Where(s => s.Block >= from).Select(s => s.Copy()).ToList(),
                Noises = _noises.Where(n => n.Block >= from).ToList(),
                Interruptions = _interruptions.Where(b => b >= from).ToList(),
                Patterns = _patterns.Where(p => p.Block >= from).ToList(),
                Context = context,
            };
        }
    }

    /// <summary>Every sound judged and every end of claps in a row it kept, whenever they were (for replays).</summary>
    internal (List<ClapSoundRecord> Sounds, List<ClapPatternRecord> Patterns) Everything()
    {
        lock (_gate)
        {
            return (_sounds.Select(s => s.Copy()).ToList(), [.. _patterns]);
        }
    }

    internal void Attach(double blockSeconds, int sampleRate)
    {
        lock (_gate)
        {
            _blockSeconds = blockSeconds;
            _sampleRate = sampleRate;
            _keepBlocks = (long)Math.Min(long.MaxValue / 2, Math.Round(_keepSeconds / blockSeconds));
        }
    }

    /// <summary>A block's three levels (dB), with the detector's state before it.</summary>
    /// <param name="speakerRise">The sharpest sound the speakers played that may be reaching the microphone now, if any.</param>
    internal void Block(long index, ClapDetectorState state, double high, double low, double body, bool silent, double? speakerRise)
    {
        lock (_gate)
        {
            if (_first < 0)
            {
                _first = index;
            }

            _last = index;
            _ring[Slot(index)] = new Entry { Index = index, State = state, High = high, Low = low, Body = body, Silent = silent };
            if (index <= _keepUntil)
            {
                Keep(index, index); // just after a sound's verdict
            }

            Summarize(index, state, high, low, body, silent, speakerRise);
            if (index % ContextBlocks == 0)
            {
                Prune(index - _keepBlocks);
            }
        }
    }

    /// <summary>
    /// The verdict on a sound that began at <paramref name="onset"/>, reached at <paramref name="block"/>.
    /// </summary>
    /// <param name="lowRise">How far the band below 2 kHz rose over its background.</param>
    /// <param name="speakerRise">The sharpest sound the speakers played that may have been reaching the microphone as it began.</param>
    internal void Judged(long onset, long block, ClapJudgement judgement, double lowRise, double? speakerRise)
    {
        lock (_gate)
        {
            // Speech and music (it rose again, it lasted) keep only their figures.
            var detail = judgement.Verdict is not (ClapVerdict.RoseAgain or ClapVerdict.Sustained) && Keep(onset - BeforeOnset, block);
            if (detail)
            {
                _keepUntil = Math.Max(_keepUntil, block + AfterVerdict);
            }

            _sounds.Add(new ClapSoundRecord
            {
                Onset = onset,
                Block = block,
                Verdict = judgement.Verdict,
                High = judgement.High,
                Low = lowRise,
                Body = judgement.Body,
                BodyOverHigh = judgement.BodyOverHigh,
                HighOverLow = judgement.HighOverLow,
                Drop = judgement.Drop,
                Peak = judgement.Peak,
                Rise = judgement.Rise,
                SpeakerRise = speakerRise,
                Detail = detail,
            });
        }
    }

    /// <summary>A rise of the band below 2 kHz alone (speech, a knock), dB over the background.</summary>
    internal void Noise(long block, double high, double low)
    {
        lock (_gate)
        {
            _noises.Add(new ClapNoiseRecord(block, high, low));
        }
    }

    /// <summary>The sound was interrupted (a gap in the stream) before this block.</summary>
    internal void Interrupted(long block)
    {
        lock (_gate)
        {
            _interruptions.Add(block);
        }
    }

    /// <summary>Claps in a row ended: <paramref name="reported"/> if that count was wanted.</summary>
    internal void Pattern(long block, int claps, bool reported)
    {
        lock (_gate)
        {
            _patterns.Add(new ClapPatternRecord(block, claps, reported));
        }
    }

    private static int Slot(long index) => (int)(index % RingSize);

    /// <summary>Copies blocks from the ring into the segments; false when they aren't all there.</summary>
    private bool Keep(long from, long to)
    {
        from = Math.Max(from, Math.Max(_first, to - RingSize + 1));
        if (from > to)
        {
            return false;
        }

        for (var b = from; b <= to; b++)
        {
            if (_ring[Slot(b)].Index != b)
            {
                return false; // a replay skipped it
            }
        }

        var segment = _segments.Count > 0 ? _segments[^1] : null;
        if (segment is null || segment.End < from - 1)
        {
            segment = new ClapSegment { Start = from, State = _ring[Slot(from)].State };
            _segments.Add(segment);
        }

        for (var b = Math.Max(from, segment.End + 1); b <= to; b++)
        {
            var entry = _ring[Slot(b)];
            if (entry.Silent)
            {
                segment.Silent.Add((int)(b - segment.Start));
            }

            segment.High.Add(entry.High);
            segment.Low.Add(entry.Low);
            segment.Body.Add(entry.Body);
        }

        return true;
    }

    private void Summarize(long index, in ClapDetectorState state, double high, double low, double body, bool silent, double? speakerRise)
    {
        var start = index - index % ContextBlocks;
        if (_half is null || _half.Start != start)
        {
            if (_half is not null)
            {
                _context.Add(_half);
            }

            _half = new ClapContextRecord { Start = start };
        }

        var half = _half;
        half.FloorHigh = state.FloorHigh;
        half.FloorLow = state.FloorLow;
        half.FloorBody = state.FloorBody;
        half.Jitter = state.Jitter;
        if (speakerRise is { } rise && (half.SpeakerRise is null || rise > half.SpeakerRise))
        {
            half.SpeakerRise = rise;
        }

        if (silent)
        {
            half.Silent++;
        }
        else if (!double.IsNaN(state.FloorHigh))
        {
            half.MaxHigh = Math.Max(half.MaxHigh ?? double.MinValue, high - state.FloorHigh);
            half.MaxLow = Math.Max(half.MaxLow ?? double.MinValue, low - state.FloorLow);
            half.MaxBody = Math.Max(half.MaxBody ?? double.MinValue, body - state.FloorBody);
            half.MaxRise = Math.Max(half.MaxRise ?? double.MinValue, high - Math.Max(state.High2, state.FloorHigh));
        }
    }

    private void Prune(long before)
    {
        _sounds.RemoveAll(s => s.Block < before);
        _noises.RemoveAll(n => n.Block < before);
        _interruptions.RemoveAll(b => b < before);
        _patterns.RemoveAll(p => p.Block < before);
        _context.RemoveAll(c => c.Start + ContextBlocks <= before);
        _segments.RemoveAll(s => s.End < before);
    }

    private struct Entry
    {
        public long Index;
        public ClapDetectorState State;
        public double High;
        public double Low;
        public double Body;
        public bool Silent;
    }
}
