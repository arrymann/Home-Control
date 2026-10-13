using System.Reflection;
using HomeControl.Core.Audio;

namespace HomeControl.Core.Tests;

/// <summary>Claps that come out of the PC's own speakers (videos, games, notifications) aren't the user's.</summary>
public class SpeakerSoundsTests
{
    private static readonly int[] Rates = [16000, 44100, 48000];
    private const double Clock = 259_200.125; // the shared clock (QPC seconds) after three days up

    private sealed record Heard(List<int> Patterns, int Claps);

    /// <summary>
    /// Plays <paramref name="played"/> through the speakers' loopback stream and <paramref name="mic"/>
    /// through the detector, packet by packet as the app does: before each microphone packet,
    /// everything the speakers have played up to its end (less <paramref name="lag"/>) is analysed.
    /// The speakers deliver packets only between <paramref name="playFrom"/> and <paramref name="playTo"/>
    /// (nothing plays otherwise). The microphone's sample 0 is at <paramref name="micStart"/> on the
    /// speakers' timeline.
    /// </summary>
    private static Heard Listen(ClapSignals mic, ClapSignals? played, double maxDelay = SpeakerStream.WiredMaxDelay,
        double playFrom = 0, double playTo = double.MaxValue, int[]? counts = null, int micChunk = 0, int playedChunk = 0,
        double lag = 0, double micStart = 0, SpeakerSounds? sounds = null, bool useSpeakers = true)
    {
        var rate = mic.Rate;
        sounds ??= new SpeakerSounds();
        var stream = new SpeakerStream(sounds, rate, maxDelay);
        var detector = new ClapDetector(rate) { Counts = counts ?? [2, 3, 4], Speakers = useSpeakers ? sounds : null };
        var patterns = new List<int>();
        var claps = 0;
        detector.PatternDetected += patterns.Add;
        detector.ClapHeard += () => claps++;

        var micStep = micChunk > 0 ? micChunk : rate / 100;
        var playedStep = playedChunk > 0 ? playedChunk : rate / 100;
        var next = 0;
        for (var start = 0; start < mic.Samples.Length; start += micStep)
        {
            var length = Math.Min(micStep, mic.Samples.Length - start);
            var until = (int)Math.Round((micStart + (double)(start + length) / rate - lag) * rate);
            while (played is not null && next < Math.Min(until, played.Samples.Length))
            {
                var n = Math.Min(playedStep, played.Samples.Length - next);
                var time = (double)next / rate;
                if (time >= playFrom && time < playTo)
                {
                    stream.Process(played.Samples.AsSpan(next, n), Clock + time);
                }

                next += n;
            }

            detector.Process(mic.Samples.AsSpan(start, length), Clock + micStart + (double)start / rate);
        }

        return new Heard(patterns, claps);
    }

    private static void ForEachRate(Action<int> test)
    {
        foreach (var rate in Rates)
        {
            test(rate);
        }
    }

    /// <summary>Two claps in a video: loud, from digital silence.</summary>
    private static ClapSignals VideoClaps(int rate, double seconds = 4, double at = 1.0) =>
        new ClapSignals(rate, seconds, seed: 11, room: false).AddClaps(at, 0.3, 2, -10);

    // ---------------------------------------------------------------- the speakers' claps

    [Fact]
    public void Claps_from_the_speakers_are_not_the_users()
    {
        ForEachRate(rate =>
        {
            var played = VideoClaps(rate);
            var mic = new ClapSignals(rate, 4, seed: 7).AddPlayed(played, 0.03);

            // Without the speakers' stream they are a double clap...
            Assert.Equal([2], Listen(mic, played, useSpeakers: false).Patterns);

            // ...with it, not even a clap.
            var heard = Listen(mic, played);
            Assert.Empty(heard.Patterns);
            Assert.Equal(0, heard.Claps);
        });
    }

    [Theory]
    [InlineData(0.000, SpeakerStream.WiredMaxDelay)]
    [InlineData(0.150, SpeakerStream.WiredMaxDelay)]
    [InlineData(0.250, SpeakerStream.DisplayMaxDelay)]
    [InlineData(0.450, SpeakerStream.BluetoothMaxDelay)]
    public void Allows_for_the_speakers_latency(double delay, double maxDelay)
    {
        var played = VideoClaps(48000);
        var mic = new ClapSignals(48000, 4, seed: 7).AddPlayed(played, delay);

        Assert.Equal(0, Listen(mic, played, maxDelay).Claps);
    }

    [Fact]
    public void Slower_speakers_than_allowed_for_get_through()
    {
        // Why the app picks the delay for the device: Bluetooth speakers are late for a wired window.
        var played = new ClapSignals(48000, 4, seed: 11, room: false).AddClaps(1.0, 0.5, 2, -10);
        var mic = new ClapSignals(48000, 4, seed: 7).AddPlayed(played, 0.3);

        Assert.Equal([2], Listen(mic, played, SpeakerStream.WiredMaxDelay).Patterns);
        Assert.Empty(Listen(mic, played, SpeakerStream.BluetoothMaxDelay).Patterns);
    }

    [Theory]
    [InlineData(2.95)] // the stream wakes a moment before the sound
    [InlineData(3.00)] // the first packet starts with it
    public void A_sound_that_starts_from_nothing_playing_is_caught(double wakeUp)
    {
        // Nothing plays (no packets) until a notification with two sharp clicks.
        ForEachRate(rate =>
        {
            var played = VideoClaps(rate, seconds: 5, at: 3.0);
            var mic = new ClapSignals(rate, 5, seed: 7).AddPlayed(played, 0.02);

            Assert.Equal(0, Listen(mic, played, playFrom: wakeUp, playTo: 3.8).Claps);
        });
    }

    [Fact]
    public void A_clap_over_music_from_the_speakers_is_caught()
    {
        // A video: music with a beat, and two claps standing out of it.
        ForEachRate(rate =>
        {
            var played = new ClapSignals(rate, 4, seed: 11, room: false).AddMusic(0.2, 3.6, -26, kick: true).AddClaps(1.6, 0.3, 2, -12);
            var mic = new ClapSignals(rate, 4, seed: 7).AddPlayed(played, 0.03);

            Assert.True(Listen(mic, played, useSpeakers: false).Claps > 0);
            Assert.Equal(0, Listen(mic, played).Claps);
        });
    }

    [Fact]
    public void Tolerates_the_speakers_stream_arriving_a_little_late()
    {
        // The app reads the speakers' stream when a microphone packet comes; even if that were
        // 50 ms behind, the verdict on a clap (80 ms or more after it starts) waits long enough.
        var played = VideoClaps(48000);
        var mic = new ClapSignals(48000, 4, seed: 7).AddPlayed(played, 0.02);

        Assert.Equal(0, Listen(mic, played, lag: 0.05).Claps);
    }

    [Fact]
    public void A_clap_from_the_speakers_among_the_users_claps_spoils_them()
    {
        // The user claps twice; a video claps in between: never three claps, nor a clean pair.
        var played = new ClapSignals(48000, 4, seed: 11, room: false).AddClap(1.15, -10);
        var mic = new ClapSignals(48000, 4, seed: 7).AddClaps(1.0, 0.3, 2).AddPlayed(played, 0.02);

        Assert.Equal([3], Listen(mic, played, counts: [2, 3], useSpeakers: false).Patterns);
        Assert.Empty(Listen(mic, played, counts: [2, 3]).Patterns);
    }

    [Fact]
    public void A_speaker_that_cant_be_followed_doesnt_stop_claps()
    {
        // Another app has a speaker to itself: its sounds can't be told apart, but the user's claps still count.
        var mic = new ClapSignals(48000, 4).AddClaps(1.0, 0.3, 2);

        Assert.Equal([2], Listen(mic, played: null, sounds: new SpeakerSounds { Unfollowed = true }).Patterns);
    }

    // ---------------------------------------------------------------- the user's claps

    [Fact]
    public void The_users_claps_count_when_nothing_plays()
    {
        ForEachRate(rate =>
        {
            var mic = new ClapSignals(rate, 4).AddClaps(1.0, 0.3, 2);
            Assert.Equal([2], Listen(mic, played: null).Patterns);

            // Digital silence counts as nothing too.
            Assert.Equal([2], Listen(mic, new ClapSignals(rate, 4, room: false)).Patterns);
        });
    }

    [Fact]
    public void The_users_claps_count_while_the_speakers_play_steady_sound()
    {
        ForEachRate(rate =>
        {
            var played = new ClapSignals(rate, 4, seed: 11, room: false).AddNoise(-30).AddTone(220, -20);
            var mic = new ClapSignals(rate, 4, seed: 7).AddPlayed(played, 0.02, -30).AddClaps(1.5, 0.3, 2);

            Assert.Equal([2], Listen(mic, played).Patterns);
        });
    }

    [Fact]
    public void The_users_claps_over_the_speakers_beat_count()
    {
        // A beat plays all along and the room hears it: a wash of cymbals with a snare every
        // half second. The user claps right on two snares, much more sharply than any of them.
        // (Before, every snare hid claps for a fifth of a second, so these never counted.)
        ForEachRate(rate =>
        {
            var played = new ClapSignals(rate, 5, seed: 11, room: false).AddNoise(-40).AddClaps(0.5, 0.5, 9, -30);
            var mic = new ClapSignals(rate, 5, seed: 7).AddPlayed(played, 0.02, -12).AddClaps(1.55, 0.5, 2, -14);

            Assert.Equal([2], Listen(mic, played).Patterns);
        });
    }

    [Fact]
    public void The_speakers_beat_alone_is_not_clapping()
    {
        ForEachRate(rate =>
        {
            var played = new ClapSignals(rate, 5, seed: 11, room: false).AddNoise(-40).AddClaps(0.5, 0.5, 9, -18);
            var mic = new ClapSignals(rate, 5, seed: 7).AddPlayed(played, 0.02, -6);

            Assert.Equal(0, Listen(mic, played).Claps);
        });
    }

    [Fact]
    public void A_clap_from_the_speakers_after_a_quiet_moment_explains_one_at_the_microphone()
    {
        // The speakers' sound rose from nothing: it can explain a sound as sharp as any clap.
        var sounds = new SpeakerSounds();
        var stream = new SpeakerStream(sounds, 48000);
        stream.Process(VideoClaps(48000).Samples, 20.0);

        Assert.True(sounds.MayExplain(21.05, rise: 60));
    }

    [Fact]
    public void A_beat_explains_only_sounds_about_as_sharp_as_itself()
    {
        var sounds = new SpeakerSounds();
        var stream = new SpeakerStream(sounds, 48000);
        var played = new ClapSignals(48000, 5, seed: 11, room: false).AddNoise(-40).AddClaps(0.5, 0.5, 9, -30);
        stream.Process(played.Samples, 30.0);

        // The snares rose a little over the cymbals; a sound that rose 40 dB at the microphone isn't one of them.
        var explainedSoft = false;
        for (var t = 31.0; t < 34.5; t += 0.01)
        {
            explainedSoft |= sounds.MayExplain(t, rise: 6);
            Assert.False(sounds.MayExplain(t, rise: 40));
        }

        Assert.True(explainedSoft);
    }

    [Fact]
    public void The_users_claps_between_the_speakers_sharp_sounds_count()
    {
        // A video claps now and then; the user claps in between.
        var played = new ClapSignals(48000, 6, seed: 11, room: false).AddClap(0.6, -10).AddClap(3.0, -10).AddClap(5.0, -10);
        var mic = new ClapSignals(48000, 6, seed: 7).AddPlayed(played, 0.02).AddClaps(1.6, 0.3, 2);

        var heard = Listen(mic, played);
        Assert.Equal([2], heard.Patterns);
        Assert.Equal(2, heard.Claps);
    }

    [Fact]
    public void Follows_the_shared_clock_not_the_sample_counts()
    {
        // The microphone started 0.7 s after the speakers' stream: their claps line up by time only.
        var played = VideoClaps(48000, seconds: 5, at: 1.7);
        var mic = new ClapSignals(48000, 4, seed: 7).AddClaps(1.0 + 0.02, 0.3, 2, -16);

        Assert.Equal(0, Listen(mic, played, micStart: 0.7).Claps);
        Assert.Equal([2], Listen(mic, played, micStart: 0.0).Patterns); // the same sounds, a second apart
    }

    // ---------------------------------------------------------------- how it works

    [Fact]
    public void Gives_the_same_result_however_the_sound_is_cut_up()
    {
        foreach (var rate in Rates)
        {
            var played = VideoClaps(rate, seconds: 6, at: 3.5);
            var mic = new ClapSignals(rate, 6, seed: 7).AddClaps(1.0, 0.3, 2).AddPlayed(played, 0.03);
            foreach (var micChunk in new[] { 1, 7, 441, 1024, 4800 })
            {
                foreach (var playedChunk in new[] { 1, 13, 480, 4410 })
                {
                    var heard = Listen(mic, played, micChunk: micChunk, playedChunk: playedChunk);
                    Assert.Equal([2], heard.Patterns);
                    Assert.Equal(2, heard.Claps);
                }
            }
        }
    }

    [Fact]
    public void A_gap_in_the_speakers_stream_starts_it_afresh()
    {
        var sounds = new SpeakerSounds();
        var stream = new SpeakerStream(sounds, 48000);
        var played = VideoClaps(48000);

        stream.Process(played.Samples.AsSpan(0, 48000), 10.0);
        stream.Gap(); // a silent or lost packet
        stream.Process(played.Samples.AsSpan(48000, 4800), 11.0);

        Assert.True(sounds.MayExplain(11.0 + 0.05));
        Assert.False(sounds.MayExplain(11.0 + SpeakerStream.WiredMaxDelay + 0.05));
        Assert.False(sounds.MayExplain(10.5));
    }

    [Fact]
    public void Keeps_only_the_latest_windows()
    {
        // Sharp sounds every 0.5 s for 5 s: the latest are remembered, the oldest forgotten.
        var sounds = new SpeakerSounds();
        var stream = new SpeakerStream(sounds, 48000);
        var played = new ClapSignals(48000, 5, room: false);
        for (var t = 0.5; t < 5; t += 0.5)
        {
            played.AddClap(t, -10);
        }

        stream.Process(played.Samples, 0);

        Assert.True(sounds.MayExplain(4.55));
        Assert.True(sounds.MayExplain(3.05));
        Assert.False(sounds.MayExplain(0.55));
        Assert.False(sounds.MayExplain(4.75)); // between the sounds' windows
    }

    [Fact]
    public void Allocates_nothing_while_listening()
    {
        var played = VideoClaps(48000, seconds: 5, at: 3.0);
        var mic = new ClapSignals(48000, 5).AddClaps(1.0, 0.3, 2).AddPlayed(played, 0.02);
        var sounds = new SpeakerSounds();
        var stream = new SpeakerStream(sounds, 48000);
        var detector = new ClapDetector(48000) { Counts = [2], Speakers = sounds };
        var patterns = 0;
        detector.PatternDetected += _ => patterns++;
        stream.Process(played.Samples.AsSpan(0, 480), 0);
        detector.Process(mic.Samples.AsSpan(0, 480), 0); // first calls: JIT and such

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var start = 480; start < mic.Samples.Length; start += 480)
        {
            stream.Process(played.Samples.AsSpan(start, 480), start / 48000.0);
            detector.Process(mic.Samples.AsSpan(start, 480), start / 48000.0);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1, patterns);
    }

    [Fact]
    public void Reset_leaves_nothing_of_the_sound()
    {
        var played = VideoClaps(48000);
        var usedSounds = new SpeakerSounds();
        var used = new SpeakerStream(usedSounds, 48000, SpeakerStream.BluetoothMaxDelay);
        used.Process(played.Samples.AsSpan(0, played.Samples.Length - 1234), 5.0);
        Assert.True(usedSounds.MayExplain(6.1));
        used.Reset();
        usedSounds.Reset();

        var freshSounds = new SpeakerSounds();
        var fresh = new SpeakerStream(freshSounds, 48000, SpeakerStream.BluetoothMaxDelay);
        AssertSameFields(fresh, used, nameof(SpeakerStream));
        AssertSameFields(freshSounds, usedSounds, nameof(SpeakerSounds));
    }

    [Fact]
    public void Needs_a_usable_sample_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeakerStream(new SpeakerSounds(), 4000));
    }

    private static void AssertSameFields(object expected, object actual, string name)
    {
        foreach (var field in expected.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(SpeakerSounds))
            {
                continue; // where it marks, not what it heard
            }

            Assert.True(Equals(field.GetValue(expected), field.GetValue(actual)), $"{name}.{field.Name}");
        }
    }
}
