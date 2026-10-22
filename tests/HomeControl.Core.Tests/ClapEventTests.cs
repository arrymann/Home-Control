using System.IO.Compression;
using System.Text.Json;
using HomeControl.Core.Audio;

namespace HomeControl.Core.Tests;

public class ClapEventTests
{
    private static readonly int[] Counts = [2, 3];

    /// <summary>
    /// Runs a signal through a detector with a history: the recordings taken a second after it
    /// reported claps (as the app saves them), every 7 s, and at the end.
    /// </summary>
    private static (List<ClapRecording> AtPatterns, ClapRecording AtEnd, List<ClapRecording> Others) Record(
        ClapSignals signal, ClapSensitivity sensitivity = ClapSensitivity.Medium, int[]? counts = null)
    {
        var history = new ClapHistory();
        var detector = new ClapDetector(signal.Rate, sensitivity) { Counts = counts ?? Counts, History = history };
        var atPatterns = new List<ClapRecording>();
        var others = new List<ClapRecording>();
        var due = new Queue<int>();
        var blocks = 0;
        detector.PatternDetected += _ => due.Enqueue(blocks + 100);
        var step = signal.Rate / 100;
        for (var start = 0; start < signal.Samples.Length; start += step)
        {
            detector.Process(signal.Samples.AsSpan(start, Math.Min(step, signal.Samples.Length - start)));
            blocks++;
            while (due.Count > 0 && due.Peek() <= blocks)
            {
                due.Dequeue();
                atPatterns.Add(history.Snapshot());
            }

            if (blocks % 700 == 0)
            {
                others.Add(history.Snapshot());
            }
        }

        return (atPatterns, history.Snapshot(), others);
    }

    private static ClapSignals Busy(int rate, ulong seed = 7) =>
        new ClapSignals(rate, 40, seed)
            .AddNoise(-62)
            .AddClaps(2.0, 0.3, 2)
            .AddSpeech(5.0, 9.0)
            .AddClaps(11.0, 0.35, 3, peakDb: -26)
            .AddTyping(14.0, 17.0, -30)
            .AddPrinterTick(18.0)
            .AddPrinterTick(18.4)
            .AddDeskClick(19.0)
            .AddKnock(20.0)
            .AddClaps(20.5, 0.3, 2)
            .AddMusic(23.0, 4.0, -30, kick: true)
            .AddClaps(28.0, 0.3, 2, peakDb: -40)
            .AddCough(31.0)
            .AddClaps(33.0, 0.25, 2)
            .AddPlosiveAndVowel(36.0)
            .AddClaps(38.0, 0.3, 2);

    private static void AssertSameJudgements(ClapRecording recording, ClapReplayResult replay)
    {
        Assert.Empty(ClapReplay.Differences(recording, replay));
        Assert.NotEmpty(recording.Sounds);
    }

    [Fact]
    public void A_recording_has_the_claps_it_heard()
    {
        var (atPatterns, _, _) = Record(new ClapSignals(48000, 6).AddNoise(-62).AddClaps(3.0, 0.3, 2));

        var recording = Assert.Single(atPatterns);
        var claps = recording.Sounds.Where(s => s.Verdict == ClapVerdict.Clap).ToList();
        Assert.Equal(2, claps.Count);
        Assert.All(claps, c => Assert.True(c.Detail));
        var pattern = Assert.Single(recording.Patterns);
        Assert.Equal((2, true), (pattern.Claps, pattern.Reported));
        Assert.InRange(recording.SecondsBefore(claps[0].Onset), -1.9, -1.3);

        // Each clap's blocks, from just before it rose until its verdict, and the half seconds around.
        Assert.All(claps, c => Assert.Contains(recording.Segments, s => s.Start <= c.Onset - 2 && s.End >= c.Block));
        Assert.InRange(recording.Context.Count, 9, 11); // a second after the pattern, 4.9 s in
        Assert.All(recording.Context, c => Assert.InRange(c.FloorHigh, -110, -40));
    }

    [Fact]
    public void Speech_and_music_keep_only_their_figures()
    {
        var signal = new ClapSignals(48000, 20).AddNoise(-62).AddSpeech(1.0, 9.0).AddMusic(10.0, 9.0, -25, kick: true);
        var (_, recording, _) = Record(signal, ClapSensitivity.High);

        // Most of what it judged was speech and music; very little of it is kept block by block.
        Assert.True(recording.Sounds.Count >= 10, $"{recording.Sounds.Count} sounds judged");
        var kept = recording.Segments.Sum(s => s.High.Count);
        Assert.True(kept <= 0.08 * 1900, $"{kept} blocks of 1900 kept");
        Assert.All(recording.Sounds.Where(s => s.Verdict is ClapVerdict.RoseAgain or ClapVerdict.Sustained), s => Assert.False(s.Detail));
        Assert.All(recording.Segments, s => Assert.True(s.High.Count <= 40, $"a run of {s.High.Count} blocks"));
    }

    [Fact]
    public void It_keeps_only_the_last_half_minute()
    {
        var (_, recording, _) = Record(new ClapSignals(16000, 50).AddNoise(-62).AddClaps(5.0, 0.3, 2).AddClaps(45.0, 0.3, 2));

        Assert.InRange(recording.SecondsBefore(recording.FromBlock), -30.5, -29.5);
        Assert.Equal(2, recording.Sounds.Count(s => s.Verdict == ClapVerdict.Clap));
        Assert.All(recording.Sounds, s => Assert.True(s.Block >= recording.FromBlock));
        Assert.Single(recording.Patterns);
    }

    [Theory]
    [InlineData(16000, ClapSensitivity.Low)]
    [InlineData(44100, ClapSensitivity.Medium)]
    [InlineData(48000, ClapSensitivity.High)]
    [InlineData(48000, ClapSensitivity.Medium)]
    public void A_replay_comes_to_the_same_verdicts(int rate, ClapSensitivity sensitivity)
    {
        var (atPatterns, atEnd, others) = Record(Busy(rate), sensitivity);

        Assert.NotEmpty(atPatterns);
        foreach (var recording in atPatterns.Append(atEnd).Concat(others))
        {
            AssertSameJudgements(recording, ClapReplay.Run(recording, sensitivity, Counts));
        }
    }

    [Fact]
    public void A_saved_event_replays_the_same_after_loading()
    {
        var (atPatterns, _, _) = Record(Busy(48000));
        var clapEvent = new ClapEvent { Id = "test", Time = DateTimeOffset.Now, Kind = ClapEventKind.Heard, Claps = 2, Recording = atPatterns[^1] };

        var loaded = JsonSerializer.Deserialize<ClapEvent>(JsonSerializer.Serialize(clapEvent, ClapEventStore.JsonOptions), ClapEventStore.JsonOptions)!;

        AssertSameJudgements(atPatterns[^1], ClapReplay.Run(loaded.Recording, ClapSensitivity.Medium, Counts));
    }

    [Fact]
    public void A_replay_at_another_sensitivity_judges_again()
    {
        // Quiet claps from across the room: heard at High, not at Low.
        var (atPatterns, _, _) = Record(new ClapSignals(48000, 4).AddClaps(1.0, 0.3, 2, -45), ClapSensitivity.High, counts: [2]);
        var recording = Assert.Single(atPatterns);

        Assert.Single(ClapReplay.Run(recording, ClapSensitivity.High, [2]).Reported);
        Assert.Empty(ClapReplay.Run(recording, ClapSensitivity.Low, [2]).Reported);
    }

    [Theory]
    [InlineData(ClapSensitivity.Medium, ClapSensitivity.Low)]
    [InlineData(ClapSensitivity.Medium, ClapSensitivity.High)]
    [InlineData(ClapSensitivity.High, ClapSensitivity.Low)]
    public void A_replay_at_another_sensitivity_hears_what_that_sensitivity_hears(ClapSensitivity recordedAt, ClapSensitivity replayedAt)
    {
        // Other thresholds judge a moment earlier or later: the blocks kept around each sound allow for it.
        var signal = Busy(48000);
        var (_, recording, _) = Record(signal, recordedAt);
        var (_, direct, _) = Record(signal, replayedAt);

        var replay = ClapReplay.Run(recording, replayedAt, Counts);

        string Patterns(IEnumerable<ClapPatternRecord> patterns) =>
            string.Join(", ", patterns.Where(p => p.Reported && p.Block >= recording.FromBlock).Select(p => $"{p.Claps}@{p.Block / 5}"));
        Assert.Equal(Patterns(direct.Patterns), Patterns(replay.Patterns));
        Assert.NotEmpty(direct.Patterns);
    }

    [Fact]
    public void A_replay_judges_what_the_speakers_played_with_the_margin_asked()
    {
        // Claps in a video, from digital silence, heard by the microphone 30 ms later.
        const double clock = 259_200.125;
        var played = new ClapSignals(48000, 6, seed: 11, room: false).AddClaps(3.0, 0.3, 2, -10);
        var mic = new ClapSignals(48000, 6, seed: 7).AddPlayed(played, 0.03);
        var history = new ClapHistory();
        var speakers = new SpeakerSounds();
        var detector = new ClapDetector(48000) { Counts = Counts, History = history, Speakers = speakers };
        var speakerStream = new SpeakerStream(speakers, 48000);
        var patterns = 0;
        detector.PatternDetected += _ => patterns++;
        for (var start = 0; start < mic.Samples.Length; start += 480)
        {
            speakerStream.Process(played.Samples.AsSpan(start, 480), clock + start / 48000.0);
            detector.Process(mic.Samples.AsSpan(start, 480), clock + start / 48000.0);
        }

        var recording = history.Snapshot();
        Assert.Equal(0, patterns);
        Assert.Contains(recording.Sounds, s => s.Verdict == ClapVerdict.FromSpeakers && s.SpeakerRise is not null);
        Assert.Empty(ClapReplay.Run(recording, ClapSensitivity.Medium, Counts).Reported);
        Assert.Single(ClapReplay.Run(recording, ClapSensitivity.Medium, Counts, speakerMargin: -100).Reported);
    }

    // ---------------------------------------------------------------- the store

    private static ClapEvent Event(DateTimeOffset time, ClapEventKind kind = ClapEventKind.Heard) =>
        new() { Time = time, Kind = kind, Claps = 2, Sensitivity = ClapSensitivity.Medium, Counts = [2], Recording = new ClapRecording { SampleRate = 48000 } };

    [Fact]
    public void The_store_saves_lists_and_flags_events()
    {
        using var folder = new TempFolder();
        var store = new ClapEventStore(folder.Path);
        var start = new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.FromHours(2));

        var first = store.Save(Event(start));
        var second = store.Save(Event(start.AddMinutes(1), ClapEventKind.Missed));
        store.SetFlag(first.Id, ClapEventFlag.NotClaps, "the 3D printer");

        // Read back by a new store (from the index), and again without the index (from the files).
        foreach (var reopened in new[] { new ClapEventStore(folder.Path), Reindexed(folder.Path) })
        {
            var list = reopened.List();
            Assert.Equal([second.Id, first.Id], list.Select(e => e.Id));
            Assert.Equal((ClapEventFlag.NotClaps, "the 3D printer"), (list[1].Flag, list[1].Note));
            Assert.Equal(ClapEventKind.Missed, list[0].Kind);
            Assert.Equal(ClapEventFlag.NotClaps, reopened.Get(first.Id)!.Flag);
        }

        Assert.Equal("20261022-210000-000", first.Id);
        Assert.StartsWith("Heard 2 claps", first.Description);
    }

    private static ClapEventStore Reindexed(string folder)
    {
        File.Delete(Path.Combine(folder, "index.json"));
        return new ClapEventStore(folder);
    }

    [Fact]
    public void The_store_keeps_flagged_events_longer()
    {
        using var folder = new TempFolder();
        var store = new ClapEventStore(folder.Path, keepUnflagged: 3, keepAll: 5);
        var start = DateTimeOffset.Now;
        var flagged = store.Save(Event(start));
        store.SetFlag(flagged.Id, ClapEventFlag.NotClaps);
        for (var i = 1; i <= 6; i++)
        {
            store.Save(Event(start.AddSeconds(i)));
        }

        var list = store.List();
        Assert.Equal(4, list.Count); // the 3 newest unflagged, and the flagged one
        Assert.Contains(list, e => e.Id == flagged.Id);
        Assert.Equal(4, Directory.GetFiles(folder.Path, "2*.json").Length);

        for (var i = 7; i <= 12; i++)
        {
            store.SetFlag(store.Save(Event(start.AddSeconds(i))).Id, ClapEventFlag.Right);
        }

        Assert.Equal(5, store.List().Count); // at most 5 in all: the oldest flagged one goes too
        Assert.DoesNotContain(store.List(), e => e.Id == flagged.Id);
    }

    [Fact]
    public void The_store_exports_flagged_events_to_a_zip_that_reads_back()
    {
        using var folder = new TempFolder();
        var store = new ClapEventStore(Path.Combine(folder.Path, "events"));
        var start = DateTimeOffset.Now;
        var wrong = store.Save(Event(start));
        store.Save(Event(start.AddSeconds(1)));
        store.SetFlag(wrong.Id, ClapEventFlag.NotClaps, "door");

        var zipPath = Path.Combine(folder.Path, "out", "claps.zip");
        Assert.Equal(1, store.Export(zipPath));

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.Equal([wrong.Id + ".json", "README.txt"], zip.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        }

        var read = Assert.Single(ClapEventStore.ReadAll(zipPath));
        Assert.Equal((wrong.Id, ClapEventFlag.NotClaps, "door"), (read.Id, read.Flag, read.Note));
        Assert.Equal(2, store.Export(zipPath, all: true));

        store.DeleteAll();
        Assert.Empty(store.List());
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Path, "events")));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hc-claps-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
