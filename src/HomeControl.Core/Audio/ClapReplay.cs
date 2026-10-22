namespace HomeControl.Core.Audio;

/// <summary>What the detector, as it is now, makes of a saved recording.</summary>
/// <param name="Sounds">The sounds it judged, with their verdicts.</param>
/// <param name="Patterns">Claps in a row, as they ended.</param>
/// <param name="Unknown">
/// Sounds it could only take over as they were judged then, because their blocks weren't kept
/// (speech and music): a changed detector may judge those differently.
/// </param>
/// <param name="Unfinished">Sounds it was still judging when their kept blocks ran out (other thresholds take longer): no verdict.</param>
public sealed record ClapReplayResult(IReadOnlyList<ClapSoundRecord> Sounds, IReadOnlyList<ClapPatternRecord> Patterns, int Unknown, int Unfinished)
{
    /// <summary>The claps in a row it would report.</summary>
    public IEnumerable<ClapPatternRecord> Reported => Patterns.Where(p => p.Reported);
}

/// <summary>
/// Runs a saved <see cref="ClapRecording"/> through <see cref="ClapDetector"/> again, to see
/// what it makes of the same sounds with other settings or after a change to how it judges.
/// </summary>
/// <remarks>
/// The kept blocks go through the detector as they came from the microphone, starting from the
/// state it had then. Between them it only learns what was noted: sounds judged without their
/// blocks (speech, music) count with their verdict then, rises of the low band alone as other
/// sounds, and claps in a row still end on time. With the settings of the recording and an
/// unchanged detector, it comes to the same verdicts and patterns.
/// </remarks>
public static class ClapReplay
{
    /// <param name="speakerMargin">How much more sharply than the speakers' sound a sound may rise and still be theirs (see <see cref="SpeakerSounds.RiseMargin"/>).</param>
    public static ClapReplayResult Run(ClapRecording recording, ClapSensitivity sensitivity, IReadOnlyList<int> counts, double speakerMargin = 6)
    {
        var history = new ClapHistory(keepSeconds: double.PositiveInfinity);
        var speakerRises = recording.Sounds
            .Where(s => s.SpeakerRise is not null)
            .GroupBy(s => s.Onset)
            .ToDictionary(g => g.Key, g => g.Max(s => s.SpeakerRise!.Value));
        var detector = new ClapDetector(Math.Max(8000, recording.SampleRate), sensitivity)
        {
            Counts = counts,
            History = history,
            SpeakerCheck = (onset, rise) => speakerRises.TryGetValue(onset, out var speakers) && rise - speakerMargin <= speakers,
        };

        var segments = recording.Segments.OrderBy(s => s.Start).ToList();
        var silent = segments.Select(s => s.Silent.ToHashSet()).ToList();
        var interruptions = recording.Interruptions.ToLookup(b => b);
        var noises = recording.Noises.ToLookup(n => n.Block);
        var unkept = recording.Sounds.Where(s => !s.Detail).ToList();
        var onsets = unkept.ToLookup(s => s.Onset);
        var verdicts = unkept.ToLookup(s => s.Block);

        detector.StartAt(recording.FromBlock);
        var next = 0;
        var abandoned = 0;
        for (var block = recording.FromBlock; block <= recording.ToBlock; block++)
        {
            while (next < segments.Count && segments[next].End < block)
            {
                next++;
                if (detector.Tracking is { } onset && !unkept.Any(s => s.Onset <= block && s.Block >= block))
                {
                    // Still judging a sound when its kept blocks ran out (other thresholds take
                    // longer): what it would have made of it can't be known.
                    detector.ReplayAbandon();
                    abandoned++;
                }
            }

            var inSegment = next < segments.Count && segments[next].Start <= block;
            if (inSegment && segments[next].Start == block)
            {
                detector.Restore(segments[next].State);
            }

            foreach (var _ in interruptions[block])
            {
                detector.Interrupt();
            }

            if (inSegment)
            {
                var segment = segments[next];
                var i = (int)(block - segment.Start);
                detector.ProcessLevels(segment.High[i], segment.Low[i], segment.Body[i], silent[next].Contains(i));
                continue;
            }

            foreach (var sound in verdicts[block])
            {
                detector.ReplayVerdict(sound.Onset, sound.Judgement, sound.Low);
            }

            if (onsets[block].Any())
            {
                detector.ReplayOnset();
            }

            foreach (var noise in noises[block])
            {
                detector.ReplayNoise(noise.High, noise.Low);
            }

            detector.SkipBlock();
        }

        var (sounds, patterns) = history.Everything();
        return new ClapReplayResult(sounds, patterns, unkept.Count, abandoned);
    }

    /// <summary>
    /// Where a replay came to other verdicts or patterns than the recording, one line each (empty:
    /// the same). Only over the stretch the recording knows all about: not its first two seconds
    /// (unless it starts when listening did), when claps from before it could still count, nor
    /// its last block, which may have been taken while it was being judged.
    /// </summary>
    public static IReadOnlyList<string> Differences(ClapRecording recording, ClapReplayResult replay)
    {
        var from = recording.FromBlock == 0 ? 0 : recording.FromBlock + (long)Math.Round(2 / recording.BlockSeconds);
        var to = recording.ToBlock - 1;
        string Sound(ClapSoundRecord s) => $"sound {s.Onset}–{s.Block}: {ClapEvent.Name(s.Verdict)}";
        string Pattern(ClapPatternRecord p) => $"{p.Claps} claps in a row at {p.Block}{(p.Reported ? "" : " (not a count it listened for)")}";
        var then = recording.Sounds.Where(s => s.Onset >= from && s.Block <= to).Select(Sound)
            .Concat(recording.Patterns.Where(p => p.Block >= from && p.Block <= to).Select(Pattern)).ToHashSet();
        var now = replay.Sounds.Where(s => s.Onset >= from && s.Block <= to).Select(Sound)
            .Concat(replay.Patterns.Where(p => p.Block >= from && p.Block <= to).Select(Pattern)).ToHashSet();
        return [.. then.Except(now).Select(d => "then: " + d), .. now.Except(then).Select(d => "now: " + d)];
    }
}
