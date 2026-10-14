using System.Collections;
using System.Reflection;
using HomeControl.Core.Audio;

namespace HomeControl.Core.Tests;

public class ClapDetectorTests
{
    private static readonly int[] Rates = [16000, 44100, 48000];
    private static readonly ClapSensitivity[] AllSensitivities = [ClapSensitivity.Low, ClapSensitivity.Medium, ClapSensitivity.High];

    /// <summary>What the detector reported, and when (seconds of sound fed in, to the next 10 ms).</summary>
    private sealed record Heard(List<(int Count, double Time)> Patterns, int Claps);

    private static Heard Listen(ClapSignals signal, int[] counts, ClapSensitivity sensitivity = ClapSensitivity.Medium, int chunk = 0)
    {
        var detector = new ClapDetector(signal.Rate, sensitivity) { Counts = counts };
        var patterns = new List<(int, double)>();
        var claps = 0;
        var fed = 0;
        detector.PatternDetected += n => patterns.Add((n, (double)fed / signal.Rate));
        detector.ClapHeard += () => claps++;

        var step = chunk > 0 ? chunk : signal.Rate / 100;
        for (var start = 0; start < signal.Samples.Length; start += step)
        {
            var length = Math.Min(step, signal.Samples.Length - start);
            fed = start + length;
            detector.Process(signal.Samples.AsSpan(start, length));
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

    // ---------------------------------------------------------------- claps it must hear

    [Theory]
    [InlineData(new[] { 2 }, 0.40, 0.55)]     // the largest count: a short closing window
    [InlineData(new[] { 2, 3 }, 0.55, 0.65)]  // a third clap could still come
    public void Hears_two_claps_once_they_are_over(int[] counts, double soonest, double latest)
    {
        ForEachRate(rate =>
        {
            var heard = Listen(new ClapSignals(rate, 3).AddClaps(1.0, 0.3, 2), counts);

            var (count, time) = Assert.Single(heard.Patterns);
            Assert.Equal(2, count);
            Assert.InRange(time - 1.3, soonest, latest);
            Assert.Equal(2, heard.Claps);
        });
    }

    [Theory]
    [InlineData(3, 0.25)]
    [InlineData(4, 0.22)]
    public void Hears_three_and_four_claps_without_also_reporting_fewer(int count, double gap)
    {
        ForEachRate(rate =>
        {
            var heard = Listen(new ClapSignals(rate, 3).AddClaps(1.0, gap, count), [2, 3, 4]);

            Assert.Equal([count], heard.Patterns.Select(p => p.Count));
        });
    }

    [Theory]
    [InlineData(0.15, -20)] // quick
    [InlineData(0.58, -20)] // slow
    [InlineData(0.30, -35)] // quiet
    [InlineData(0.30, -3)]  // very loud
    public void Hears_claps_at_any_reasonable_pace_and_level(double gap, double level)
    {
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                var heard = Listen(new ClapSignals(rate, 3).AddClaps(1.0, gap, 2, level), [2], sensitivity);

                Assert.Equal([2], heard.Patterns.Select(p => p.Count));
            }
        });
    }

    [Theory]
    [InlineData(0.072)] // RT60 0.5 s
    [InlineData(0.116)] // RT60 0.8 s
    public void Hears_claps_in_reverberant_rooms(double reverbDecay)
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3)
                .AddClap(1.0, reverb: 0.5, reverbDecay: reverbDecay)
                .AddClap(1.35, reverb: 0.5, reverbDecay: reverbDecay);

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void Hears_claps_over_a_fan_over_music_and_after_talking()
    {
        ForEachRate(rate =>
        {
            var fan = new ClapSignals(rate, 3).AddNoise(-40, lowPassHz: 150).AddClaps(1.0, 0.3, 2);
            var music = new ClapSignals(rate, 4).AddMusic(0, 4, -36, kick: false).AddClaps(1.6, 0.3, 2, -18); // louder music hides the claps' body
            var talk = new ClapSignals(rate, 4).AddSpeech(0.8, 1.6).AddClaps(2.25, 0.3, 2);
            var narrow = new ClapSignals(rate, 3).AddClap(1.0, lowHz: 400, highHz: 3000).AddClap(1.3, lowHz: 400, highHz: 3000); // a voice-band microphone

            Assert.Equal([2], Listen(fan, [2]).Patterns.Select(p => p.Count));
            Assert.Equal([2], Listen(music, [2]).Patterns.Select(p => p.Count));
            Assert.Equal([2], Listen(talk, [2]).Patterns.Select(p => p.Count));
            Assert.Equal([2], Listen(narrow, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void A_fumbled_try_doesnt_block_the_next_one()
    {
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                // Two claps too close together, then a proper pair a second later.
                var signal = new ClapSignals(rate, 5).AddClap(1.0).AddClap(1.08).AddClaps(2.0, 0.3, 2);

                Assert.Equal([2], Listen(signal, [2], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Fact]
    public void A_stray_clap_a_while_before_doesnt_matter()
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddClap(0.8).AddClaps(1.75, 0.3, 2);

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Theory]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 2, 3 })]
    [InlineData(new[] { 2, 3, 4 })]
    public void A_pattern_can_be_repeated_straight_away(int[] counts)
    {
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                // Double claps again and again (the light didn't react, or another automation),
                // 0.65, 0.75 and 0.85 s from one pair to the next.
                var signal = new ClapSignals(rate, 5.5)
                    .AddClaps(1.0, 0.27, 2).AddClaps(1.92, 0.27, 2).AddClaps(2.94, 0.27, 2).AddClaps(4.06, 0.27, 2);

                Assert.Equal([2, 2, 2, 2], Listen(signal, counts, sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Fact]
    public void Two_pairs_close_together_are_two_pairs()
    {
        ForEachRate(rate =>
        {
            // The pause between the pairs is twice the gap within them: not a steady rhythm of four.
            var signal = new ClapSignals(rate, 3.5).AddClaps(1.0, 0.25, 2).AddClaps(1.75, 0.25, 2);

            Assert.Equal([2, 2], Listen(signal, [2, 3, 4]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void Faint_high_ticks_dont_spoil_claps()
    {
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                // A 3D printer: a motor hum, and faint ticks just before the claps and while the
                // sequence is still open.
                var signal = new ClapSignals(rate, 3).AddNoise(-50, lowPassHz: 150).AddClaps(1.0, 0.3, 2)
                    .AddClick(0.85, -30).AddClick(1.45, -30).AddClick(1.6, -30);

                Assert.Equal([2], Listen(signal, [2, 3], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Fact]
    public void Sensitivity_decides_how_quiet_a_clap_may_be()
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddClaps(1.0, 0.3, 2, -45);

            Assert.Equal([2], Listen(signal, [2], ClapSensitivity.High).Patterns.Select(p => p.Count));
            Assert.Empty(Listen(signal, [2], ClapSensitivity.Low).Patterns);
        });
    }

    // ---------------------------------------------------------------- what isn't a pattern

    [Theory]
    [InlineData(new[] { 0.0, 0.2, 0.4, 0.6, 0.8 }, new[] { 2, 3, 4 })] // five claps: too many
    [InlineData(new[] { 0.0, 0.3, 0.6 }, new[] { 2 })]                  // three when only two are used
    [InlineData(new[] { 0.0, 0.1 }, new[] { 2 })]                       // too close together
    [InlineData(new[] { 0.0, 0.8 }, new[] { 2 })]                       // too far apart
    [InlineData(new[] { 0.0, 0.15, 0.65 }, new[] { 3 })]                // no steady rhythm
    [InlineData(new[] { 0.0, 0.5, 0.7 }, new[] { 3 })]                  // slowing down then rushing
    [InlineData(new[] { 0.0, 0.7, 1.0 }, new[] { 2 })]                  // a stray clap just before
    public void Wrong_patterns_are_ignored(double[] times, int[] counts)
    {
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                var signal = new ClapSignals(rate, 3);
                foreach (var time in times)
                {
                    signal.AddClap(1.0 + time);
                }

                Assert.Empty(Listen(signal, counts, sensitivity).Patterns);
            }
        });
    }

    public static TheoryData<string> Sounds => ["knocks", "speech", "high speech", "plosives", "quiet typing", "loud typing", "music", "music with drums", "door slam", "coughs"];

    private static ClapSignals Sound(string name, int rate) => name switch
    {
        "knocks" => new ClapSignals(rate, 3).AddKnock(1.0).AddKnock(1.2).AddKnock(1.4),
        "speech" => new ClapSignals(rate, 5).AddSpeech(1.0, 4.5),
        "high speech" => new ClapSignals(rate, 5).AddSpeech(1.0, 4.5, -15, 220),
        "plosives" => Enumerable.Range(0, 6).Aggregate(new ClapSignals(rate, 4), (s, i) => s.AddPlosiveAndVowel(1.0 + 0.4 * i)),
        "quiet typing" => new ClapSignals(rate, 5).AddTyping(1.0, 4.0, -30),
        "loud typing" => new ClapSignals(rate, 5).AddTyping(1.0, 4.0, -15),
        "music" => new ClapSignals(rate, 5).AddMusic(0.6, 4, -20, kick: false),
        "music with drums" => new ClapSignals(rate, 5).AddMusic(0.6, 4, -20, kick: true),
        "door slam" => new ClapSignals(rate, 3).AddSlam(1.0),
        "coughs" => new ClapSignals(rate, 3).AddCough(1.0).AddCough(1.45),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Sounds))]
    public void Other_sounds_are_not_claps(string sound)
    {
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                var heard = Listen(Sound(sound, rate), [2, 3, 4], sensitivity);

                Assert.Empty(heard.Patterns);
                Assert.Equal(0, heard.Claps);
            }
        });
    }

    [Fact]
    public void Claps_mixed_with_other_sounds_are_ignored()
    {
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                // Right after talking, cut short by a knock, under continuous talking, and in applause.
                var afterTalk = new ClapSignals(rate, 4).AddSpeech(0.8, 1.6).AddClaps(1.8, 0.3, 2);
                var knocked = new ClapSignals(rate, 3).AddClaps(1.0, 0.3, 2).AddKnock(1.5);
                var underTalk = new ClapSignals(rate, 4).AddSpeech(0.8, 3.5, -25).AddClaps(1.9, 0.3, 2, -15);
                var applause = new ClapSignals(rate, 5).AddApplause(1.0, 4.0);

                Assert.Empty(Listen(afterTalk, [2], sensitivity).Patterns);
                Assert.Empty(Listen(knocked, [2], sensitivity).Patterns);
                Assert.Empty(Listen(underTalk, [2], sensitivity).Patterns);
                Assert.Empty(Listen(applause, [2, 3], sensitivity).Patterns);
            }
        });
    }

    [Fact]
    public void Talking_still_counts_next_to_a_3d_printer()
    {
        // The printer's hum hides voices in the high band; at a normal level they still count
        // as other sounds, right before the claps and all through them.
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                var afterTalk = new ClapSignals(rate, 4).AddPrinterRoom().AddSpeech(0.8, 1.6, -20).AddClaps(1.8, 0.3, 2);
                var underTalk = new ClapSignals(rate, 4).AddPrinterRoom().AddSpeech(0.8, 3.5, -25).AddClaps(1.9, 0.3, 2, -15);

                Assert.Empty(Listen(afterTalk, [2], sensitivity).Patterns);
                Assert.Empty(Listen(underTalk, [2], sensitivity).Patterns);
            }
        });
    }

    [Fact]
    public void Trying_again_after_a_spoiled_pair_works()
    {
        // The second clap of the first try was too soft; the user tries again at once, and again.
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                var signal = new ClapSignals(rate, 5).AddClap(1.0).AddClap(1.27, -75)
                    .AddClaps(1.62, 0.27, 2).AddClaps(2.45, 0.27, 2).AddClaps(3.3, 0.27, 2);

                Assert.Equal([2, 2], Listen(signal, [2], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Theory]
    [InlineData(0.45)]
    [InlineData(0.5)]
    public void A_pair_a_little_after_a_pair_is_another_pair(double pause)
    {
        // Just too late to keep the rhythm of the first pair: that pair is over, and this one counts too.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 4).AddClaps(1.0, 0.25, 2).AddClaps(1.25 + pause, 0.25, 2);

            Assert.Equal([2, 2], Listen(signal, [2, 3, 4]).Patterns.Select(p => p.Count));
        });
    }

    /// <summary>A room with a steady machine hum (pure tones): its low band barely moves, so a high tick is just that.</summary>
    private static ClapSignals HummingRoom(int rate, double seconds) =>
        new ClapSignals(rate, seconds).AddTone(100, -30).AddTone(200, -36);

    [Theory]
    [InlineData(-12, true)] // a tick that stands out from the room, but 15 dB below the claps, doesn't matter
    [InlineData(-4, false)] // one about as loud as the claps does
    public void Ticks_count_only_when_about_as_loud_as_the_claps(double tickDb, bool heard)
    {
        // Just before the claps, and while the sequence is still open.
        ForEachRate(rate =>
        {
            var before = HummingRoom(rate, 3).AddHighTick(0.8, tickDb).AddClaps(1.0, 0.3, 2, -10);
            var during = HummingRoom(rate, 3).AddClaps(1.0, 0.3, 2, -10).AddHighTick(1.55, tickDb);
            var expected = heard ? new[] { 2 } : [];

            Assert.Equal(expected, Listen(before, [2, 3]).Patterns.Select(p => p.Count));
            Assert.Equal(expected, Listen(during, [2, 3]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void A_tick_is_measured_against_the_quietest_clap()
    {
        // The second clap is 10 dB quieter: a tick 15 dB below the first clap but near the second counts.
        ForEachRate(rate =>
        {
            var signal = HummingRoom(rate, 3).AddClap(1.0, -10).AddClap(1.3, -20).AddHighTick(1.55, -12);

            Assert.Empty(Listen(signal, [2, 3]).Patterns);
        });
    }

    // ---------------------------------------------------------------- next to a 3D printer

    [Fact]
    public void Hears_dull_cupped_claps_over_a_3d_printer()
    {
        // Cupped hands ring at a few hundred hertz: as loud below 2 kHz as above, or louder. The
        // second pair is clapped close enough to clip.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 5).AddPrinterRoom()
                .AddDullClap(1.0, -16).AddDullClap(1.27, -12)
                .AddDullClap(3.0, -12, clipAt: 0.5).AddDullClap(3.25, -10, clipAt: 0.5);

            foreach (var sensitivity in AllSensitivities)
            {
                var heard = Listen(signal, [2], sensitivity);

                Assert.Equal([2, 2], heard.Patterns.Select(p => p.Count));
                Assert.Equal(4, heard.Claps);
            }
        });
    }

    [Fact]
    public void Hears_soft_claps_over_a_3d_printer()
    {
        // Soft enough that the printer's hum is about as loud as the claps' high band.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddPrinterRoom()
                .AddClap(1.0, -23, reverb: 0.35, reverbDecay: 0.08)
                .AddClap(1.27, -22, reverb: 0.35, reverbDecay: 0.08);

            foreach (var sensitivity in AllSensitivities)
            {
                Assert.Equal([2], Listen(signal, [2], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Fact]
    public void A_3d_printer_is_not_clapping()
    {
        // Its ticks and whirs, and clicks on the desk next to it, at random for 11 seconds.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 12).AddPrinterRoom();
            for (var t = 0.8; t < 11.5; t += 0.1 + 0.5 * signal.Random())
            {
                var kind = signal.Random();
                if (kind < 0.5)
                {
                    signal.AddPrinterTick(t, -52 + 10 * signal.Random(), 0.002 + 0.006 * signal.Random(), 0.02 + 0.06 * signal.Random());
                }
                else if (kind < 0.8)
                {
                    signal.AddPrinterTick(t, -50 + 8 * signal.Random(), 0.02 + 0.03 * signal.Random(), 0.1 + 0.15 * signal.Random());
                }
                else
                {
                    signal.AddDeskClick(t, -30 + 10 * signal.Random());
                }
            }

            foreach (var sensitivity in AllSensitivities)
            {
                var heard = Listen(signal, [2, 3, 4], sensitivity);

                Assert.Empty(heard.Patterns);
                Assert.Equal(0, heard.Claps);
            }
        });
    }

    [Fact]
    public void A_loud_click_next_to_the_microphone_is_not_a_clap()
    {
        // As loud as a soft clap in the high band, but with no room tail, and faint next to the
        // printer's hum.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddPrinterRoom().AddDeskClick(1.0, -20).AddDeskClick(1.3, -19);

            foreach (var sensitivity in AllSensitivities)
            {
                var heard = Listen(signal, [2], sensitivity);

                Assert.Empty(heard.Patterns);
                Assert.Equal(0, heard.Claps);
            }
        });
    }

    [Fact]
    public void Clicks_next_to_the_microphone_are_not_claps_in_a_quiet_room_either()
    {
        // With no hum to stand out from, only the missing room tail gives them away. (At High
        // sensitivity the loudest of them can still pass for a soft clap.)
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 5);
            for (var i = 0; i < 6; i++)
            {
                signal.AddDeskClick(1.0 + 0.5 * i, -34 + i);
            }

            Assert.Equal(0, Listen(signal, [2], ClapSensitivity.Low).Claps);
            Assert.Equal(0, Listen(signal, [2], ClapSensitivity.Medium).Claps);
        });
    }

    [Fact]
    public void A_printers_faint_ticks_dont_interrupt_claps()
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddPrinterRoom().AddClaps(1.0, 0.3, 2)
                .AddPrinterTick(1.45, -46).AddPrinterTick(1.62, -44, attack: 0.01, decay: 0.15);

            foreach (var sensitivity in AllSensitivities)
            {
                Assert.Equal([2], Listen(signal, [2], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Fact]
    public void A_clap_that_starts_softly_is_still_one_clap()
    {
        // The hands don't always meet at once: a softer smack 30 ms before the clap is part of it,
        // not another sound that interrupts the pair.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddClap(1.0).AddClap(1.27, -32).AddClap(1.30, -18);

            foreach (var sensitivity in AllSensitivities)
            {
                Assert.Equal([2], Listen(signal, [2], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Fact]
    public void Hears_claps_close_to_the_microphone()
    {
        // Close up, the direct sound is far louder than the room's echo: it drops by about 20 dB
        // in 40 ms, yet the echo is still loud.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddClap(1.0, -12, reverb: 0.1).AddClap(1.3, -12, reverb: 0.1);

            foreach (var sensitivity in AllSensitivities)
            {
                Assert.Equal([2], Listen(signal, [2], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Theory]
    [InlineData(1.0, 1.05)] // a dropout
    [InlineData(1.0, 4.0)]  // muted for a while
    public void Clicks_after_digital_silence_are_not_claps(double from, double to)
    {
        // Silence measures far below any room; the background must be learned again afterwards,
        // or dry clicks would pass the "room tail" test against it.
        ForEachRate(rate =>
        {
            foreach (var sensitivity in AllSensitivities)
            {
                var signal = new ClapSignals(rate, to + 2).AddClick(to + 0.4, -30).AddClick(to + 0.7, -30);
                signal.Samples.AsSpan((int)(from * rate), (int)((to - from) * rate)).Clear();

                var heard = Listen(signal, [2, 3], sensitivity);

                Assert.Empty(heard.Patterns);
                Assert.Equal(0, heard.Claps);
            }
        });
    }

    [Fact]
    public void Claps_are_heard_again_after_a_mute()
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 4).AddClaps(2.6, 0.3, 2);
            signal.Samples.AsSpan(rate, rate).Clear();

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void Hears_claps_through_a_16_bit_microphone_in_a_very_quiet_room()
    {
        // Many 10 ms blocks are exactly zero after rounding to 16 bits: that isn't a mute.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3);
            Array.Clear(signal.Samples);
            signal.AddNoise(-106).AddClaps(1.0, 0.3, 2);
            for (var i = 0; i < signal.Samples.Length; i++)
            {
                signal.Samples[i] = MathF.Round(signal.Samples[i] * 32768) / 32768;
            }

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void Hears_claps_through_a_noise_gate()
    {
        // The gate passes only loud sounds: digital silence right before and between the claps.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddClaps(1.0, 0.3, 2);
            for (var i = 0; i < signal.Samples.Length; i++)
            {
                var t = (double)i / rate;
                var open = t < 0.8 || (t >= 0.998 && t < 1.15) || (t >= 1.298 && t < 1.45);
                if (!open)
                {
                    signal.Samples[i] = 0;
                }
            }

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void Hears_claps_when_the_sound_starts_silent()
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 4).AddClaps(2.5, 0.3, 2);
            signal.Samples.AsSpan(0, rate).Clear();

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void A_broken_sample_doesnt_stop_it_hearing()
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 4).AddClaps(2.5, 0.3, 2);
            signal.Samples[rate] = float.NaN;
            signal.Samples[rate * 3 / 2] = float.PositiveInfinity;
            signal.Samples[rate * 3 / 2 + 1] = float.NegativeInfinity;

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void Only_the_counts_asked_for_are_reported()
    {
        var signal = new ClapSignals(48000, 3).AddClaps(1.0, 0.3, 2);

        Assert.Empty(Listen(signal, []).Patterns);
        Assert.Empty(Listen(signal, [3, 4]).Patterns);

        var detector = new ClapDetector(48000) { Counts = [4, 1, 2, 9, 2] };
        Assert.Equal([2, 4], detector.Counts);
    }

    [Fact]
    public void Needs_a_usable_sample_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClapDetector(4000));
    }

    // ---------------------------------------------------------------- whatever the room sounds like

    [Theory]
    [InlineData(-35)]
    [InlineData(-20)]
    [InlineData(-3)]
    public void Hears_bright_claps_with_little_body(double level)
    {
        // Flat, bright claps: almost nothing below 800 Hz. Their body is faint but their own.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddClap(1.0, level, lowHz: 800).AddClap(1.3, level, lowHz: 800);
            foreach (var sensitivity in AllSensitivities)
            {
                Assert.Equal([2], Listen(signal, [2], sensitivity).Patterns.Select(p => p.Count));
            }
        });
    }

    [Fact]
    public void A_loud_hum_doesnt_make_claps_too_low_pitched()
    {
        // The room's hum is far louder than the claps below 2 kHz: the claps' own sound is what counts.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddTone(100, -18).AddTone(200, -24).AddClaps(1.0, 0.3, 2, -32);
            var judged = new List<ClapJudgement>();
            var detector = new ClapDetector(rate, ClapSensitivity.High) { Counts = [2] };
            detector.SoundJudged += judged.Add;
            var patterns = new List<int>();
            detector.PatternDetected += patterns.Add;
            detector.Process(signal.Samples);

            Assert.DoesNotContain(judged, j => j.Verdict == ClapVerdict.TooLowPitched);
            Assert.Equal([2], patterns);
        });
    }

    [Fact]
    public void Hears_claps_over_a_machines_whine()
    {
        // A stepper motor whining steadily at 700 and 940 Hz, right in the claps' body.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 3).AddTone(700, -40).AddTone(940, -40).AddClaps(1.0, 0.3, 2);

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void A_printers_ticks_near_the_claps_dont_spoil_them()
    {
        // Ticks with no body, well below the claps, just before, between and after them (once
        // the clap's tail has faded: a tick right on it is measured with the tail).
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 4).AddPrinterRoom()
                .AddPrinterTick(1.6, -33).AddClaps(2.0, 0.3, 2, -20).AddPrinterTick(2.2, -33).AddPrinterTick(2.9, -33);

            Assert.Equal([2], Listen(signal, [2]).Patterns.Select(p => p.Count));
        });
    }

    // ---------------------------------------------------------------- what it tells the user

    [Fact]
    public void Says_what_it_made_of_each_sound()
    {
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 7).AddClaps(1.0, 0.3, 2).AddPrinterTick(3.0, -30).AddClap(4.5).AddClap(4.6);
            var detector = new ClapDetector(rate) { Counts = [2] };
            var judged = new List<ClapJudgement>();
            detector.SoundJudged += judged.Add;
            detector.Process(signal.Samples);

            Assert.Equal(
                [ClapVerdict.Clap, ClapVerdict.Clap, ClapVerdict.NoBody, ClapVerdict.Clap, ClapVerdict.ClapNotCounted],
                judged.Select(j => j.Verdict));

            // The figures it went by: the claps rose far over the room, in the high band and the
            // body; the tick's body, if it rose at all, is far below its high band.
            Assert.All(judged.Where(j => j.Verdict == ClapVerdict.Clap), j => Assert.True(j.High > 30 && j.Body > 15, j.ToString()));
            Assert.True(judged[2].High > 30, judged[2].ToString());
        });
    }

    // ---------------------------------------------------------------- it keeps no sound

    [Fact]
    public void Has_nowhere_to_keep_sound()
    {
        // Only single numbers: no arrays, lists, buffers, memory or streams that could hold samples.
        // The same goes for what follows the speakers (their loopback stream is sound too).
        Type[] listening = [typeof(ClapDetector), typeof(SpeakerSounds), typeof(SpeakerStream)];
        var types = listening.SelectMany(t => t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Append(t)).ToList();
        foreach (var type in types)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var fieldType = field.FieldType;
                var holdsSound = fieldType.IsArray || fieldType.IsPointer ||
                                 typeof(IEnumerable).IsAssignableFrom(fieldType) ||
                                 typeof(Stream).IsAssignableFrom(fieldType) ||
                                 (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() is var g &&
                                  (g == typeof(Memory<>) || g == typeof(ReadOnlyMemory<>) || g == typeof(ArraySegment<>)));
                Assert.False(holdsSound, $"{type.Name}.{field.Name} ({fieldType.Name}) could hold samples");
                Assert.True(
                    fieldType.IsPrimitive || fieldType.IsEnum || typeof(Delegate).IsAssignableFrom(fieldType) || types.Contains(fieldType),
                    $"{type.Name}.{field.Name} has an unexpected type {fieldType.Name}");
            }
        }
    }

    [Fact]
    public void Allocates_nothing_while_listening()
    {
        var signal = new ClapSignals(48000, 5).AddClaps(2.0, 0.3, 2).AddSpeech(3.0, 4.5);
        var detector = new ClapDetector(48000) { Counts = [2] };
        var patterns = 0;
        detector.PatternDetected += _ => patterns++;
        detector.Process(signal.Samples.AsSpan(0, 480)); // first call: JIT and such

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var start = 480; start < signal.Samples.Length; start += 480)
        {
            detector.Process(signal.Samples.AsSpan(start, 480));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1, patterns);
    }

    [Fact]
    public void Gives_the_same_result_however_the_sound_is_cut_up()
    {
        // No hidden buffering: each sample is used as it arrives.
        ForEachRate(rate =>
        {
            var signal = new ClapSignals(rate, 4).AddClaps(1.0, 0.3, 2).AddSpeech(2.0, 3.5);
            var whole = Listen(signal, [2], chunk: signal.Samples.Length);

            foreach (var chunk in new[] { 1, 7, 160, 441, 480, 1024, 4800 })
            {
                var cut = Listen(signal, [2], chunk: chunk);
                Assert.Equal(whole.Claps, cut.Claps);
                Assert.Equal(whole.Patterns.Select(p => p.Count), cut.Patterns.Select(p => p.Count));
            }

            Assert.Equal([2], whole.Patterns.Select(p => p.Count));
        });
    }

    [Fact]
    public void Reset_leaves_nothing_of_the_sound()
    {
        // After Reset every field is as in a new detector: nothing derived from the sound remains.
        var signal = new ClapSignals(48000, 3).AddClaps(1.0, 0.3, 2).AddSpeech(1.8, 2.8);
        var used = new ClapDetector(48000, ClapSensitivity.High) { Counts = [2] };
        used.Process(signal.Samples.AsSpan(0, signal.Samples.Length - 1234));
        used.Reset();
        var fresh = new ClapDetector(48000, ClapSensitivity.High) { Counts = [2] };

        foreach (var field in typeof(ClapDetector).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (!typeof(Delegate).IsAssignableFrom(field.FieldType))
            {
                Assert.Equal(field.GetValue(fresh), field.GetValue(used));
            }
        }
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var signal = new ClapSignals(48000, 3).AddClaps(1.0, 0.3, 2);
        var detector = new ClapDetector(48000) { Counts = [2] };
        var patterns = new List<int>();
        detector.PatternDetected += patterns.Add;

        // Reset between the two claps: the first one is forgotten.
        detector.Process(signal.Samples.AsSpan(0, (int)(1.2 * 48000)));
        detector.Reset();
        detector.Process(signal.Samples.AsSpan((int)(1.2 * 48000)));
        Assert.Empty(patterns);

        // Afterwards it hears exactly what a new detector hears.
        detector.Reset();
        detector.Process(signal.Samples);
        Assert.Equal([2], patterns);
    }

    [Fact]
    public void An_interruption_cancels_a_sequence()
    {
        var signal = new ClapSignals(48000, 3).AddClaps(1.0, 0.3, 2);
        var detector = new ClapDetector(48000) { Counts = [2] };
        var patterns = 0;
        detector.PatternDetected += _ => patterns++;

        detector.Process(signal.Samples.AsSpan(0, (int)(1.5 * 48000)));
        detector.Interrupt();
        detector.Process(signal.Samples.AsSpan((int)(1.5 * 48000)));

        Assert.Equal(0, patterns);
    }
}
