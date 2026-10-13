namespace HomeControl.Core.Tests;

/// <summary>
/// Synthetic sounds for the clap detector tests, made in memory (no recordings are used or kept).
/// Levels are dB re full scale; times are seconds from the start of the buffer.
/// </summary>
internal sealed class ClapSignals
{
    private readonly ClapSignalRandom _random;

    /// <param name="room">Start from a quiet room's noise; false: from digital silence (what an app plays).</param>
    public ClapSignals(int rate, double seconds, ulong seed = 42, bool room = true)
    {
        Rate = rate;
        Samples = new float[(int)(rate * seconds)];
        _random = new ClapSignalRandom(seed);

        if (room)
        {
            // A quiet room: hiss plus a low hum like a fan.
            AddNoise(-70);
            AddNoise(-60, lowPassHz: 150);
        }
    }

    public int Rate { get; }

    public float[] Samples { get; }

    public double Random() => _random.Uniform();

    private static double Amplitude(double db) => Math.Pow(10, db / 20);

    public ClapSignals AddNoise(double rmsDb, double lowPassHz = 0)
    {
        var a = lowPassHz > 0 ? Math.Exp(-2 * Math.PI * lowPassHz / Rate) : 0;
        var gain = lowPassHz > 0 ? Math.Sqrt((1 + a) / (1 - a)) : 1;
        var amplitude = Amplitude(rmsDb);
        double state = 0;
        for (var i = 0; i < Samples.Length; i++)
        {
            var white = _random.Normal();
            state = lowPassHz > 0 ? a * state + (1 - a) * white : white;
            Samples[i] += (float)(amplitude * state * gain);
        }

        return this;
    }

    /// <summary>
    /// A hand clap: a broadband burst (0.3 ms attack, <paramref name="direct"/> decay) plus the
    /// room's reverberation (relative level <paramref name="reverb"/>, decay <paramref name="reverbDecay"/>).
    /// </summary>
    public ClapSignals AddClap(double time, double peakDb = -20, double direct = 0.004, double reverb = 0.25,
        double reverbDecay = 0.07, double lowHz = 800, double highHz = 6000)
    {
        Burst(time, 8 * Math.Max(direct, reverbDecay),
            t => Math.Min(1, t / 0.0003) * (Math.Exp(-t / direct) + reverb * Math.Exp(-t / reverbDecay)),
            lowHz, highHz, Amplitude(peakDb));
        return this;
    }

    /// <summary>
    /// What the PC played (<paramref name="played"/>, at the same rate), heard through its speakers
    /// <paramref name="delay"/> seconds later and <paramref name="gainDb"/> louder or quieter, a
    /// little duller (the room absorbs the highest sounds first).
    /// </summary>
    public ClapSignals AddPlayed(ClapSignals played, double delay, double gainDb = -6)
    {
        var shift = (int)Math.Round(delay * Rate);
        var gain = Amplitude(gainDb);
        var a = Math.Exp(-2 * Math.PI * 9000 / Rate);
        double state = 0;
        for (var i = Math.Max(0, shift); i < Samples.Length && i - shift < played.Samples.Length; i++)
        {
            state = a * state + (1 - a) * played.Samples[i - shift];
            Samples[i] += (float)(gain * state);
        }

        return this;
    }

    public ClapSignals AddClaps(double start, double gap, int count, double peakDb = -20)
    {
        for (var i = 0; i < count; i++)
        {
            AddClap(start + i * gap, peakDb);
        }

        return this;
    }

    /// <summary>A knock on a door: decaying low tones and a faint click.</summary>
    public ClapSignals AddKnock(double time, double peakDb = -10)
    {
        var amplitude = Amplitude(peakDb);
        var start = (int)(time * Rate);
        for (var k = 0; k < (int)(0.15 * Rate) && start + k < Samples.Length; k++)
        {
            var t = (double)k / Rate;
            var v = 0.7 * Math.Exp(-t / 0.020) * Math.Sin(2 * Math.PI * 180 * t) + 0.3 * Math.Exp(-t / 0.010) * Math.Sin(2 * Math.PI * 420 * t);
            Samples[start + k] += (float)(amplitude * v);
        }

        Burst(time, 0.004, t => Math.Exp(-t / 0.0007), 1000, 6000, amplitude * Amplitude(-20));
        return this;
    }

    /// <summary>A spoken syllable: a gliding harmonic tone up to 4 kHz with a smooth envelope.</summary>
    public ClapSignals AddSyllable(double time, double rmsDb = -20, double pitchHz = 140,
        double attack = 0.025, double sustain = 0.12, double release = 0.04)
    {
        var harmonics = (int)(4000 / pitchHz);
        double norm = 0;
        for (var k = 1; k <= harmonics; k++)
        {
            norm += 0.5 / (k * k);
        }

        var amplitude = Amplitude(rmsDb) / Math.Sqrt(norm);
        var start = (int)(time * Rate);
        var length = (int)((attack + sustain + release) * Rate);
        for (var i = 0; i < length && start + i < Samples.Length; i++)
        {
            var t = (double)i / Rate;
            var envelope = t < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / attack)
                : t < attack + sustain ? 1
                : 0.5 + 0.5 * Math.Cos(Math.PI * (t - attack - sustain) / release);
            var phase = 2 * Math.PI * pitchHz * (1 + 0.08 * Math.Sin(2 * Math.PI * 3 * t)) * t;
            double v = 0;
            for (var k = 1; k <= harmonics; k++)
            {
                v += Math.Sin(k * phase) / k;
            }

            Samples[start + i] += (float)(amplitude * envelope * v);
        }

        return this;
    }

    public ClapSignals AddSpeech(double from, double to, double rmsDb = -20, double pitchHz = 140)
    {
        for (var t = from; t < to; t += 0.25)
        {
            AddSyllable(t, rmsDb, pitchHz);
        }

        return this;
    }

    /// <summary>A plosive ("p", "t") followed by a louder vowel.</summary>
    public ClapSignals AddPlosiveAndVowel(double time)
    {
        Burst(time, 0.01, t => Math.Exp(-t / 0.002), 1000, 7000, Amplitude(-15));
        return AddSyllable(time + 0.025, -18, 140, 0.015, 0.15, 0.04);
    }

    /// <summary>A key click close to the microphone (no room tail).</summary>
    public ClapSignals AddClick(double time, double peakDb)
    {
        Burst(time, 0.004, t => Math.Exp(-t / 0.0005), 2000, 7000, Amplitude(peakDb));
        return this;
    }

    public ClapSignals AddTyping(double from, double to, double peakDb)
    {
        for (var t = from; t < to; t += 0.09 + 0.16 * _random.Uniform())
        {
            AddClick(t, peakDb);
        }

        return this;
    }

    /// <summary>Sustained chords that change every 0.5 s, optionally with a kick drum on each change.</summary>
    public ClapSignals AddMusic(double from, double seconds, double rmsDb, bool kick)
    {
        double[][] chords = [[220.0, 277.18, 329.63], [196.0, 246.94, 293.66], [174.61, 220.0, 261.63], [196.0, 246.94, 293.66]];
        var amplitude = Amplitude(rmsDb) / Math.Sqrt(3 * 0.5 * 1.49);
        var start = (int)(from * Rate);
        for (var i = 0; i < (int)(seconds * Rate) && start + i < Samples.Length; i++)
        {
            var t = (double)i / Rate;
            var chord = chords[(int)(t / 0.5) % chords.Length];
            var sinceChange = t % 0.5;
            double v = 0;
            foreach (var f in chord)
            {
                for (var k = 1; k <= 6; k++)
                {
                    v += Math.Sin(2 * Math.PI * f * k * t) / k;
                }
            }

            var drum = kick ? 2.0 * Math.Exp(-sinceChange / 0.05) * Math.Sin(2 * Math.PI * 60 * sinceChange) : 0;
            Samples[start + i] += (float)(amplitude * (Math.Min(1, sinceChange / 0.010) * v + drum));
        }

        return this;
    }

    /// <summary>A door slamming: a big low thump with some broadband noise.</summary>
    public ClapSignals AddSlam(double time, double peakDb = -6)
    {
        var amplitude = Amplitude(peakDb);
        var start = (int)(time * Rate);
        for (var k = 0; k < (int)(0.6 * Rate) && start + k < Samples.Length; k++)
        {
            var t = (double)k / Rate;
            Samples[start + k] += (float)(amplitude * Math.Exp(-t / 0.12) * Math.Sin(2 * Math.PI * 70 * t));
        }

        Burst(time, 0.1, t => Math.Exp(-t / 0.01), 300, 6000, amplitude * Amplitude(-15));
        return this;
    }

    public ClapSignals AddCough(double time, double peakDb = -15)
    {
        Burst(time, 0.3, t => Math.Min(1, t / 0.008) * (t < 0.06 ? 1 : Math.Exp(-(t - 0.06) / 0.08)), 300, 3500, Amplitude(peakDb));
        return AddSyllable(time + 0.04, peakDb - 8, 250, 0.02, 0.15, 0.05);
    }

    /// <summary>Applause: claps at random, about 12 a second.</summary>
    public ClapSignals AddApplause(double from, double to)
    {
        for (var t = from; t < to; t += -Math.Log(1 - _random.Uniform()) / 12)
        {
            AddClap(t, -30 + 15 * _random.Uniform());
        }

        return this;
    }

    /// <summary>A steady tone, such as a fan's or a motor's hum.</summary>
    public ClapSignals AddTone(double frequency, double rmsDb)
    {
        var amplitude = Amplitude(rmsDb) * Math.Sqrt(2);
        var phase = 2 * Math.PI * _random.Uniform();
        for (var i = 0; i < Samples.Length; i++)
        {
            Samples[i] += (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate + phase));
        }

        return this;
    }

    /// <summary>
    /// A room with a 3D printer running: its fans and motors hum loudly below 500 Hz (the low band
    /// sits about 35 dB above the high band's background), with a little hiss above.
    /// </summary>
    public ClapSignals AddPrinterRoom()
    {
        AddNoise(-62);                                        // hiss: the high band's background
        AddTone(48, -34).AddTone(96, -40).AddTone(192, -33);  // fans
        Burst(0, (double)Samples.Length / Rate, _ => 1, 60, 300, Amplitude(-26)); // motor rumble
        return this;
    }

    /// <summary>
    /// A 3D printer's tick or whir: a soft hiss above 2 kHz (<paramref name="peakDb"/> re full
    /// scale) that rises in a few milliseconds (<paramref name="attack"/>) and fades slowly.
    /// </summary>
    public ClapSignals AddPrinterTick(double time, double peakDb = -50, double attack = 0.003, double decay = 0.05)
    {
        Burst(time, Math.Max(0.3, 6 * decay), t => Math.Min(1, t / attack) * Math.Exp(-t / decay), 2000, 7000, Amplitude(peakDb));
        return this;
    }

    /// <summary>
    /// A click on the desk near the microphone (a mouse, a pen): a bright ring of a few
    /// milliseconds (<paramref name="peakDb"/> re full scale) and only a faint, short tail,
    /// nothing like a clap's room tail. The ring is the same at any sample rate.
    /// </summary>
    public ClapSignals AddDeskClick(double time, double peakDb = -24)
    {
        var amplitude = Amplitude(peakDb);
        var start = (int)(time * Rate);
        double[] modes = [2100, 3300, 4600, 5900];
        for (var k = 0; k < (int)(0.02 * Rate) && start + k < Samples.Length; k++)
        {
            var t = (double)k / Rate;
            double v = 0;
            for (var m = 0; m < modes.Length; m++)
            {
                v += Math.Sin(2 * Math.PI * modes[m] * t + m);
            }

            Samples[start + k] += (float)(amplitude * 0.5 * Math.Min(1, t / 0.0002) * Math.Exp(-t / 0.0012) * v);
        }

        Burst(time, 0.2, t => Math.Exp(-t / 0.03), 1500, 7000, amplitude * 0.07);
        return this;
    }

    /// <summary>
    /// A clap with cupped hands: besides the broadband burst, the hollow of the hands rings at
    /// a few hundred hertz, so the low band is as loud as the high band or louder. Clapped close
    /// to the microphone it may clip (<paramref name="clipAt"/>, the converter's full scale).
    /// </summary>
    public ClapSignals AddDullClap(double time, double peakDb = -14, double body = 6, double clipAt = 0)
    {
        AddClap(time, peakDb, reverb: 0.35, reverbDecay: 0.08, lowHz: 600);
        Burst(time, 0.5, t => Math.Min(1, t / 0.0005) * (Math.Exp(-t / 0.008) + 0.35 * Math.Exp(-t / 0.08)), 250, 900, Amplitude(peakDb) * body);
        if (clipAt > 0)
        {
            var from = (int)(time * Rate);
            for (var i = from; i < Math.Min(Samples.Length, from + Rate / 2); i++)
            {
                Samples[i] = Math.Clamp(Samples[i], (float)-clipAt, (float)clipAt);
            }
        }

        return this;
    }

    /// <summary>Band-limited noise (two high-pass and two low-pass stages) shaped by an envelope.</summary>
    private void Burst(double time, double seconds, Func<double, double> envelope, double lowHz, double highHz, double amplitude)
    {
        var top = Math.Min(highHz, 0.45 * Rate);
        var hp1 = new Filter(Rate, lowHz, highPass: true);
        var hp2 = new Filter(Rate, lowHz, highPass: true);
        var lp1 = new Filter(Rate, top, highPass: false);
        var lp2 = new Filter(Rate, top, highPass: false);
        var norm = Math.Sqrt(Rate / 2.0 / Math.Max(top - lowHz, 1));
        var start = (int)(time * Rate);
        for (var k = 0; k < (int)(seconds * Rate) && start + k < Samples.Length; k++)
        {
            var v = lp2.Step(lp1.Step(hp2.Step(hp1.Step(_random.Normal()))));
            Samples[start + k] += (float)(amplitude * norm * envelope((double)k / Rate) * v);
        }
    }

    private sealed class Filter
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public Filter(double rate, double frequency, bool highPass)
        {
            var w = 2 * Math.PI * frequency / rate;
            var cos = Math.Cos(w);
            var alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
            var a0 = 1 + alpha;
            _b0 = (highPass ? (1 + cos) / 2 : (1 - cos) / 2) / a0;
            _b1 = (highPass ? -(1 + cos) : 1 - cos) / a0;
            _b2 = _b0;
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }

        public double Step(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}

/// <summary>A small deterministic generator (xorshift64), so the signals are the same on every run.</summary>
internal sealed class ClapSignalRandom(ulong seed)
{
    private ulong _state = seed * 0x9E3779B97F4A7C15UL + 1;

    public double Uniform()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (_state >> 11) * (1.0 / (1UL << 53));
    }

    public double Normal()
    {
        var u1 = Math.Max(Uniform(), 1e-300);
        var u2 = Uniform();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
