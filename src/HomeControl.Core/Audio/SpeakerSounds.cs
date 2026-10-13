namespace HomeControl.Core.Audio;

/// <summary>
/// When sounds from the PC's own speakers may be reaching the microphone, worked out by one or
/// more <see cref="SpeakerStream"/>s from what Windows sends to the speakers (the loopback
/// stream). Given to <see cref="ClapDetector.Speakers"/>, it makes the detector ignore claps
/// that start at such a time: a clap in a video, a game or a notification sound isn't the user's.
/// </summary>
/// <remarks>
/// <para>
/// It keeps no sound, only up to four time windows (seconds on the clock the streams and the
/// detector share): each is when a burst of sharp sounds from the speakers may arrive at the
/// microphone. A newer window replaces the one that ended first; the detector asks within a
/// fraction of a second, long before four newer bursts can have pushed out the one it needs.
/// </para>
/// <para>
/// Use it, its streams and the detector from one thread (the microphone's capture thread);
/// only <see cref="PlayingUnseen"/> may be set from any thread.
/// </para>
/// </remarks>
public sealed class SpeakerSounds
{
    private const double JoinSeconds = 0.03; // windows this close become one

    private Window _window0 = Window.None;
    private Window _window1 = Window.None;
    private Window _window2 = Window.None;
    private Window _window3 = Window.None;
    private volatile bool _playingUnseen;

    /// <summary>
    /// The speakers may be playing something no <see cref="SpeakerStream"/> can follow (another
    /// app has them in exclusive mode, or their loopback stream failed): then every sound may
    /// be theirs, and no clap counts.
    /// </summary>
    public bool PlayingUnseen
    {
        get => _playingUnseen;
        set => _playingUnseen = value;
    }

    /// <summary>Whether a sound that started at the microphone at <paramref name="time"/> may be the speakers'.</summary>
    public bool MayExplain(double time) =>
        _playingUnseen || _window0.Contains(time) || _window1.Contains(time) || _window2.Contains(time) || _window3.Contains(time);

    /// <summary>Forgets the windows (not <see cref="PlayingUnseen"/>, which describes the device, not the sound).</summary>
    public void Reset()
    {
        _window0 = _window1 = _window2 = _window3 = Window.None;
    }

    /// <summary>A sharp sound from the speakers may arrive at the microphone between these times.</summary>
    internal void Add(double from, double to)
    {
        // Join the window it overlaps (sounds come in order, so usually the latest one)...
        if (_window0.Joins(from, to))
        {
            _window0 = _window0.With(from, to);
        }
        else if (_window1.Joins(from, to))
        {
            _window1 = _window1.With(from, to);
        }
        else if (_window2.Joins(from, to))
        {
            _window2 = _window2.With(from, to);
        }
        else if (_window3.Joins(from, to))
        {
            _window3 = _window3.With(from, to);
        }
        else
        {
            // ...or replace the one that ended first.
            var window = new Window(from, to);
            var earliest = Math.Min(Math.Min(_window0.To, _window1.To), Math.Min(_window2.To, _window3.To));
            if (_window0.To == earliest)
            {
                _window0 = window;
            }
            else if (_window1.To == earliest)
            {
                _window1 = window;
            }
            else if (_window2.To == earliest)
            {
                _window2 = window;
            }
            else
            {
                _window3 = window;
            }
        }
    }

    /// <summary>From when to when (seconds); empty when <see cref="From"/> is after <see cref="To"/>.</summary>
    private readonly struct Window(double from, double to)
    {
        public static readonly Window None = new(double.PositiveInfinity, double.NegativeInfinity);

        public readonly double From = from;
        public readonly double To = to;

        public bool Contains(double time) => time >= From && time <= To;

        public bool Joins(double from, double to) => from <= To + JoinSeconds && to >= From - JoinSeconds;

        public Window With(double from, double to) => new(Math.Min(From, from), Math.Max(To, to));
    }
}
