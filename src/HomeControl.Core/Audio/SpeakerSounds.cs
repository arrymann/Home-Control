namespace HomeControl.Core.Audio;

/// <summary>
/// When sounds from the PC's own speakers may be reaching the microphone, worked out by one or
/// more <see cref="SpeakerStream"/>s from what Windows sends to the speakers (the loopback
/// stream). Given to <see cref="ClapDetector.Speakers"/>, it makes the detector ignore claps
/// the speakers can explain: a clap in a video, a game or a notification sound isn't the user's.
/// </summary>
/// <remarks>
/// <para>
/// It keeps no sound, only up to four time windows (seconds on the clock the streams and the
/// detector share): each is when a burst of sharp sounds from the speakers may arrive at the
/// microphone, and how sharply they rose (dB over what played just before). A sound at the
/// microphone is the speakers' only if it starts in such a window and rises no more sharply
/// than they did: the way through the room can soften a rise but not sharpen it. So a drum
/// beat in music explains the beat the microphone hears, but not the user's much sharper clap
/// over it, while a clap in a video, rising from a quiet moment, explains a clap at the
/// microphone too. A newer window replaces the one that ended first; the detector asks within
/// a fraction of a second, long before four newer bursts can have pushed out the one it needs.
/// </para>
/// <para>
/// Use it, its streams and the detector from one thread (the microphone's capture thread);
/// only <see cref="Unfollowed"/> may be set from any thread.
/// </para>
/// </remarks>
public sealed class SpeakerSounds
{
    private const double JoinSeconds = 0.03; // windows this close (of one sound, or of sounds about as sharp) become one
    private const double JoinRise = 6;       // dB: "about as sharp"
    private const double RiseMargin = 6;     // dB: the two streams cut their 10 ms blocks at different moments

    private Window _window0 = Window.None;
    private Window _window1 = Window.None;
    private Window _window2 = Window.None;
    private Window _window3 = Window.None;
    private volatile bool _unfollowed;

    /// <summary>
    /// A speaker device can't be followed right now (another app has it in exclusive mode, or
    /// its loopback stream failed), so what it plays can't be told from the user's claps. Only
    /// for telling the user: claps still count.
    /// </summary>
    public bool Unfollowed
    {
        get => _unfollowed;
        set => _unfollowed = value;
    }

    /// <summary>
    /// Whether a sound that started at the microphone at <paramref name="time"/>, rising
    /// <paramref name="rise"/> dB over what it heard just before, may be the speakers'.
    /// </summary>
    public bool MayExplain(double time, double rise = 0) =>
        _window0.Explains(time, rise) || _window1.Explains(time, rise) || _window2.Explains(time, rise) || _window3.Explains(time, rise);

    /// <summary>Forgets the windows (not <see cref="PlayingUnseen"/>, which describes the device, not the sound).</summary>
    public void Reset()
    {
        _window0 = _window1 = _window2 = _window3 = Window.None;
    }

    /// <summary>
    /// A sound from the speakers that rose <paramref name="rise"/> dB may arrive at the
    /// microphone between these times.
    /// </summary>
    internal void Add(double from, double to, double rise)
    {
        // Join the window it overlaps, of sounds about as sharp (sounds come in order, so usually the latest one)...
        if (_window0.Joins(from, to, rise))
        {
            _window0 = _window0.With(from, to, rise);
        }
        else if (_window1.Joins(from, to, rise))
        {
            _window1 = _window1.With(from, to, rise);
        }
        else if (_window2.Joins(from, to, rise))
        {
            _window2 = _window2.With(from, to, rise);
        }
        else if (_window3.Joins(from, to, rise))
        {
            _window3 = _window3.With(from, to, rise);
        }
        else
        {
            // ...or replace the one that ended first.
            var window = new Window(from, to, rise);
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

    /// <summary>
    /// From when to when (seconds; empty when <see cref="From"/> is after <see cref="To"/>), and
    /// the sharpest rise of the sounds in it (dB).
    /// </summary>
    private readonly struct Window(double from, double to, double rise)
    {
        public static readonly Window None = new(double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

        public readonly double From = from;
        public readonly double To = to;
        public readonly double Rise = rise;

        public bool Explains(double time, double rise) => time >= From && time <= To && rise <= Rise + RiseMargin;

        // The same sound seen in the next block too, or another about as sharp nearby (a beat).
        public bool Joins(double from, double to, double rise) =>
            from <= To + JoinSeconds && to >= From - JoinSeconds && (Math.Abs(from - From) <= JoinSeconds || Math.Abs(rise - Rise) <= JoinRise);

        public Window With(double from, double to, double rise) => new(Math.Min(From, from), Math.Max(To, to), Math.Max(Rise, rise));
    }
}
