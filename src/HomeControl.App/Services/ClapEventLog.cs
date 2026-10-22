using System.Reflection;
using HomeControl.Core.Audio;
using HomeControl.Core.Settings;
using Microsoft.UI.Dispatching;

namespace HomeControl.Services;

/// <summary>
/// The clap event log for the app: saves clap events (off the UI thread), keeps the list for the
/// settings page and the tray menu, and flags, exports and deletes them. Each event holds the
/// detector's figures over the half minute before it, no sound. Use it on the UI thread.
/// </summary>
internal sealed class ClapEventLog
{
    /// <summary>How long after claps are heard their event is saved, so the sounds right after are in it too.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private static readonly string? Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);

    private readonly DispatcherQueue _dispatcher;
    private readonly ClapEventStore _store;
    private IReadOnlyList<ClapEventSummary> _events = [];
    private bool _loading;

    public ClapEventLog(DispatcherQueue dispatcher, string directory)
    {
        _dispatcher = dispatcher;
        _store = new ClapEventStore(directory);
    }

    /// <summary>Raised on the UI thread when <see cref="Events"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The saved events, newest first (empty until <see cref="Load"/> has read them).</summary>
    public IReadOnlyList<ClapEventSummary> Events => _events;

    public string Folder => _store.Directory;

    /// <summary>Reads the list in the background (once).</summary>
    public void Load()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        _ = RunAsync(() => _store.List(), "Reading the clap events");
    }

    /// <summary>Saves an event from what the detector worked out up to now (in the background).</summary>
    /// <param name="flag">What the user already said about it (claps it missed).</param>
    public void Save(ClapEventKind kind, int claps, ClapSource source, ClapSettings settings, IReadOnlyList<int> counts,
        DateTimeOffset time, ClapEventFlag flag = ClapEventFlag.None)
    {
        var clapEvent = new ClapEvent
        {
            Time = time,
            Kind = kind,
            Claps = claps,
            Flag = flag,
            Sensitivity = settings.Sensitivity,
            Counts = [.. counts],
            Microphone = source.Microphone,
            Raw = source.Raw,
            Speakers = source.Speakers,
            SpeakerMargin = source.SpeakerMargin,
            App = Version,
            Recording = source.History.Snapshot(),
        };
        Save(clapEvent);
    }

    /// <summary>Saves an event as it is (in the background).</summary>
    public void Save(ClapEvent clapEvent) =>
        _ = RunAsync(() =>
        {
            var summary = _store.Save(clapEvent);
            Log.Info($"Clap event saved: {summary.Description} ({summary.Id}).");
            return _store.List();
        }, "Saving a clap event");

    /// <summary>Sets what the user said about an event, and their note when given.</summary>
    public Task SetFlagAsync(string id, ClapEventFlag flag, string? note = null) =>
        RunAsync(() =>
        {
            _store.SetFlag(id, flag, note);
            return _store.List();
        }, "Marking a clap event");

    /// <summary>Sets the user's note on an event (empty: none).</summary>
    public Task SetNoteAsync(string id, string note) =>
        RunAsync(() =>
        {
            _store.SetNote(id, note);
            return _store.List();
        }, "Adding a note to a clap event");

    /// <summary>The newest event of claps heard, if it was saved in the last 15 minutes.</summary>
    public ClapEventSummary? LatestHeard() =>
        _events.FirstOrDefault(e => e.Kind == ClapEventKind.Heard) is { } latest && DateTimeOffset.Now - latest.Time < TimeSpan.FromMinutes(15)
            ? latest
            : null;

    /// <summary>Writes the events into a zip (all of them, with what the user said about each). Returns how many.</summary>
    public Task<int> ExportAsync(string zipPath) => Task.Run(() => _store.Export(zipPath, all: true));

    public Task DeleteAllAsync() =>
        RunAsync(() =>
        {
            _store.DeleteAll();
            Log.Info("Clap events deleted.");
            return _store.List();
        }, "Deleting the clap events");

    private async Task RunAsync(Func<IReadOnlyList<ClapEventSummary>> work, string what)
    {
        try
        {
            var events = await Task.Run(work);
            _dispatcher.TryEnqueue(() =>
            {
                _events = events;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            Log.Error(what, ex);
        }
    }
}
