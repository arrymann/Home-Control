using System.Text.Json.Serialization;

namespace HomeControl.Core.Audio;

/// <summary>
/// What a <see cref="ClapHistory"/> kept up to a moment: the detector's figures over the last
/// half minute, no sound. Saved in a <see cref="ClapEvent"/>; <see cref="ClapReplay"/> runs it
/// through the detector again. Blocks are the detector's 10 ms blocks, counted from when it
/// started listening.
/// </summary>
public sealed class ClapRecording
{
    public double BlockSeconds { get; set; } = 0.01;

    public int SampleRate { get; set; }

    /// <summary>The first block it covers.</summary>
    public long FromBlock { get; set; }

    /// <summary>The last block it covers: when it was taken.</summary>
    public long ToBlock { get; set; }

    /// <summary>Runs of blocks kept in full (the sounds that were judged, except speech and music).</summary>
    public List<ClapSegment> Segments { get; set; } = [];

    /// <summary>Every sound the detector judged, in order.</summary>
    public List<ClapSoundRecord> Sounds { get; set; } = [];

    /// <summary>Rises of the band below 2 kHz alone (speech, knocks): sounds, but never claps.</summary>
    public List<ClapNoiseRecord> Noises { get; set; } = [];

    /// <summary>Blocks before which the sound was interrupted (a gap in the stream).</summary>
    public List<long> Interruptions { get; set; } = [];

    /// <summary>Claps in a row, as they ended.</summary>
    public List<ClapPatternRecord> Patterns { get; set; } = [];

    /// <summary>Every half second: the room's background and the loudest moment.</summary>
    public List<ClapContextRecord> Context { get; set; } = [];

    /// <summary>Seconds from the end of the recording (negative) to a block.</summary>
    public double SecondsBefore(long block) => (block - ToBlock) * BlockSeconds;
}

/// <summary>Blocks kept in full: the three bands' levels (dB re full scale) from <see cref="Start"/> on.</summary>
public sealed class ClapSegment
{
    public long Start { get; set; }

    /// <summary>The detector's state at the start of block <see cref="Start"/>.</summary>
    public ClapDetectorState State { get; set; }

    /// <summary>2–7 kHz.</summary>
    public List<double> High { get; set; } = [];

    /// <summary>Below 2 kHz.</summary>
    public List<double> Low { get; set; } = [];

    /// <summary>500 Hz–1.2 kHz.</summary>
    public List<double> Body { get; set; } = [];

    /// <summary>The blocks (counted from <see cref="Start"/>) that were digital silence.</summary>
    public List<int> Silent { get; set; } = [];

    [JsonIgnore]
    public long End => Start + High.Count - 1;

    internal ClapSegment Copy() => new() { Start = Start, State = State, High = [.. High], Low = [.. Low], Body = [.. Body], Silent = [.. Silent] };
}

/// <summary>A sound the detector judged: when, its verdict and the figures it went by (dB).</summary>
public sealed class ClapSoundRecord
{
    /// <summary>The block it began at.</summary>
    public long Onset { get; set; }

    /// <summary>The block it was judged at.</summary>
    public long Block { get; set; }

    public ClapVerdict Verdict { get; set; }

    /// <summary>The high band's rise over its background.</summary>
    public double High { get; set; }

    /// <summary>The band below 2 kHz's rise over its background.</summary>
    public double Low { get; set; }

    /// <summary>The body band's rise over its background.</summary>
    public double Body { get; set; }

    public double BodyOverHigh { get; set; }

    public double HighOverLow { get; set; }

    /// <summary>How much it had dropped 40 ms after its peak (NaN if judged before then).</summary>
    public double Drop { get; set; }

    /// <summary>The high band's peak, re full scale.</summary>
    public double Peak { get; set; }

    /// <summary>How sharply the high band rose over what came just before.</summary>
    public double Rise { get; set; }

    /// <summary>How sharply the sharpest sound the speakers played that may have been reaching the microphone then rose; null: none.</summary>
    public double? SpeakerRise { get; set; }

    /// <summary>Its blocks are in <see cref="ClapRecording.Segments"/>.</summary>
    public bool Detail { get; set; }

    [JsonIgnore]
    public ClapJudgement Judgement => new(Verdict, High, Body, BodyOverHigh, HighOverLow, Drop, Peak, Rise);

    internal ClapSoundRecord Copy() => (ClapSoundRecord)MemberwiseClone();
}

/// <summary>A rise of the band below 2 kHz alone at a block, dB over the backgrounds.</summary>
public sealed record ClapNoiseRecord(long Block, double High, double Low);

/// <summary>Claps in a row that ended at a block; <paramref name="Reported"/> if that count was wanted.</summary>
public sealed record ClapPatternRecord(long Block, int Claps, bool Reported);

/// <summary>Half a second from <see cref="Start"/>: the room's background (dB re full scale) and the loudest moment (dB over it).</summary>
public sealed class ClapContextRecord
{
    public long Start { get; set; }

    public double FloorHigh { get; set; }

    public double FloorLow { get; set; }

    public double FloorBody { get; set; }

    /// <summary>How much the body band's background moves from block to block.</summary>
    public double Jitter { get; set; }

    /// <summary>The high band's highest rise over its background; null: nothing to measure (silence, or still learning).</summary>
    public double? MaxHigh { get; set; }

    public double? MaxLow { get; set; }

    public double? MaxBody { get; set; }

    /// <summary>The high band's sharpest rise over 20 ms.</summary>
    public double? MaxRise { get; set; }

    /// <summary>Blocks of digital silence.</summary>
    public int Silent { get; set; }

    /// <summary>The sharpest sound from the speakers that may have been reaching the microphone; null: none.</summary>
    public double? SpeakerRise { get; set; }

    internal ClapContextRecord Copy() => (ClapContextRecord)MemberwiseClone();
}
