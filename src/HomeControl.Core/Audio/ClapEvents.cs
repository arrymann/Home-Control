using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeControl.Core.Audio;

/// <summary>Why a clap event was saved.</summary>
public enum ClapEventKind
{
    /// <summary>Claps in a row were heard (and their automations ran).</summary>
    Heard,

    /// <summary>The user said it missed claps.</summary>
    Missed,
}

/// <summary>What the user said about a clap event.</summary>
public enum ClapEventFlag
{
    None,

    /// <summary>Those were the user's claps: it heard them right.</summary>
    Right,

    /// <summary>Those weren't claps (or not the user's): a false alarm.</summary>
    NotClaps,

    /// <summary>The user clapped and it didn't hear it.</summary>
    Missed,
}

/// <summary>
/// A saved moment of clap listening: when, what happened, what the user said about it, the
/// settings, and what the detector worked out over the half minute before (a
/// <see cref="ClapRecording"/>: figures, no sound).
/// </summary>
public sealed class ClapEvent
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    /// <summary>Also its file name, e.g. "20261022-210405-123".</summary>
    public string Id { get; set; } = "";

    public DateTimeOffset Time { get; set; }

    public ClapEventKind Kind { get; set; }

    /// <summary>Heard: how many claps in a row.</summary>
    public int Claps { get; set; }

    public ClapEventFlag Flag { get; set; }

    /// <summary>The user's note, e.g. "the 3D printer".</summary>
    public string? Note { get; set; }

    public ClapSensitivity Sensitivity { get; set; }

    /// <summary>The numbers of claps in a row it listened for.</summary>
    public List<int> Counts { get; set; } = [];

    public string? Microphone { get; set; }

    /// <summary>The microphone was used raw, without Windows' sound processing.</summary>
    public bool Raw { get; set; }

    /// <summary>The speakers whose sound it followed.</summary>
    public string? Speakers { get; set; }

    /// <summary>See <see cref="SpeakerSounds.RiseMargin"/>.</summary>
    public double SpeakerMargin { get; set; } = 6;

    /// <summary>The app's version.</summary>
    public string? App { get; set; }

    public ClapRecording Recording { get; set; } = new();

    /// <summary>One line about it, e.g. "Heard 2 claps · high +38/+35 dB, body −6/−8 dB below it".</summary>
    public string Describe()
    {
        var sounds = Recording.Sounds;
        if (Kind == ClapEventKind.Heard)
        {
            // The claps that made the pattern: the last ones counted before it was reported.
            var end = Recording.Patterns.LastOrDefault(p => p.Reported && p.Claps == Claps)?.Block ?? Recording.ToBlock + 1;
            var claps = sounds.Where(s => s.Verdict == ClapVerdict.Clap && s.Block <= end).TakeLast(Claps).ToList();
            var figures = claps.Count == 0
                ? ""
                : $" · high {string.Join("/", claps.Select(c => Signed(c.High)))} dB over the room, body {string.Join("/", claps.Select(c => Signed(c.BodyOverHigh)))} dB against it";
            return $"Heard {Claps} claps{figures}";
        }

        var judged = sounds.Where(s => s.Block >= Recording.ToBlock - (long)Math.Round(10 / Recording.BlockSeconds)).ToList();
        if (judged.Count == 0)
        {
            return "Missed claps · it judged no sound in the 10 seconds before";
        }

        var verdicts = judged.GroupBy(s => s.Verdict).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {Name(g.Key)}");
        return $"Missed claps · in the 10 seconds before it judged {judged.Count} sound{(judged.Count == 1 ? "" : "s")}: {string.Join(", ", verdicts)}";
    }

    /// <summary>A short name for a verdict, e.g. "no body".</summary>
    public static string Name(ClapVerdict verdict) => verdict switch
    {
        ClapVerdict.Clap => "clap",
        ClapVerdict.ClapNotCounted => "clap not counted",
        ClapVerdict.TooQuiet => "too quiet",
        ClapVerdict.RoseAgain => "rose again",
        ClapVerdict.Sustained => "lasted",
        ClapVerdict.NoRoomTail => "no room tail",
        ClapVerdict.DidntFade => "didn't fade",
        ClapVerdict.TooLowPitched => "too low-pitched",
        ClapVerdict.Thud => "thud",
        ClapVerdict.NoBody => "no body",
        ClapVerdict.FromSpeakers => "from the speakers",
        _ => verdict.ToString(),
    };

    private static string Signed(double value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);
}

/// <summary>A clap event in the list (without its recording).</summary>
public sealed record ClapEventSummary(string Id, DateTimeOffset Time, ClapEventKind Kind, int Claps, ClapEventFlag Flag, string? Note, string Description);

/// <summary>
/// The clap event log: one JSON file per event in a folder, and an index of them. Keeps the
/// newest events (the ones the user flagged longer), and exports them as a zip to send.
/// Thread-safe; every method does file work, so call it off the UI thread.
/// </summary>
public sealed class ClapEventStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // NaN: not measured
        Converters = { new JsonStringEnumConverter() },
    };

    private const string IndexFile = "index.json";

    private readonly object _gate = new();
    private readonly int _keepUnflagged;
    private readonly int _keepAll;
    private List<ClapEventSummary>? _index;

    /// <param name="keepUnflagged">Events the user didn't flag beyond this many go, oldest first.</param>
    /// <param name="keepAll">Beyond this many events in all, the oldest go, flagged or not.</param>
    public ClapEventStore(string directory, int keepUnflagged = 150, int keepAll = 400)
    {
        Directory = directory;
        _keepUnflagged = keepUnflagged;
        _keepAll = keepAll;
    }

    public string Directory { get; }

    /// <summary>A new id for an event at this time (unique in the folder).</summary>
    public string NewId(DateTimeOffset time)
    {
        lock (_gate)
        {
            var id = time.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var unique = id;
            for (var n = 2; Load().Any(e => e.Id == unique) || File.Exists(PathOf(unique)); n++)
            {
                unique = $"{id}-{n}";
            }

            return unique;
        }
    }

    /// <summary>The events, newest first.</summary>
    public IReadOnlyList<ClapEventSummary> List()
    {
        lock (_gate)
        {
            return [.. Load()];
        }
    }

    public ClapEventSummary Save(ClapEvent clapEvent)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(clapEvent.Id))
            {
                clapEvent.Id = NewId(clapEvent.Time);
            }

            Write(clapEvent);
            var summary = Summarize(clapEvent);
            var index = Load();
            index.RemoveAll(e => e.Id == clapEvent.Id);
            index.Insert(0, summary);
            index.Sort((a, b) => b.Time.CompareTo(a.Time));
            Prune(index);
            WriteIndex(index);
            return summary;
        }
    }

    public ClapEvent? Get(string id)
    {
        lock (_gate)
        {
            return Read(PathOf(id));
        }
    }

    /// <summary>Sets what the user said about an event (and their note, when given).</summary>
    public ClapEventSummary? SetFlag(string id, ClapEventFlag flag, string? note = null) => Change(id, e =>
    {
        e.Flag = flag;
        if (note is not null)
        {
            e.Note = note;
        }
    });

    /// <summary>Sets the user's note on an event (empty: none).</summary>
    public ClapEventSummary? SetNote(string id, string note) => Change(id, e => e.Note = note);

    private ClapEventSummary? Change(string id, Action<ClapEvent> change)
    {
        lock (_gate)
        {
            if (Read(PathOf(id)) is not { } clapEvent)
            {
                return null;
            }

            change(clapEvent);
            clapEvent.Note = string.IsNullOrWhiteSpace(clapEvent.Note) ? null : clapEvent.Note.Trim();
            return Save(clapEvent);
        }
    }

    /// <summary>Deletes every event (and the folder's other files).</summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
                {
                    File.Delete(file);
                }
            }

            _index = [];
        }
    }

    /// <summary>Writes events into a zip to send (flagged ones only, unless <paramref name="all"/>). Returns how many.</summary>
    public int Export(string zipPath, bool all = false)
    {
        lock (_gate)
        {
            var chosen = Load().Where(e => all || e.Flag != ClapEventFlag.None).ToList();
            var directory = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var temp = zipPath + ".tmp";
            using (var zip = new ZipArchive(File.Create(temp), ZipArchiveMode.Create))
            {
                using (var readme = new StreamWriter(zip.CreateEntry("README.txt").Open(), Encoding.UTF8))
                {
                    readme.Write(ReadMe);
                }

                foreach (var summary in chosen)
                {
                    var file = PathOf(summary.Id);
                    if (File.Exists(file))
                    {
                        zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                    }
                }
            }

            File.Move(temp, zipPath, overwrite: true);
            return chosen.Count;
        }
    }

    /// <summary>Reads events from a folder of event files or a zip of them (replays, analysis).</summary>
    public static IEnumerable<ClapEvent> ReadAll(string path)
    {
        if (System.IO.Directory.Exists(path))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(path, "*.json").Where(f => Path.GetFileName(f) != IndexFile).Order())
            {
                if (Read(file) is { } clapEvent)
                {
                    yield return clapEvent;
                }
            }

            yield break;
        }

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name))
            {
                using var stream = entry.Open();
                if (Parse(stream) is { } clapEvent)
                {
                    yield return clapEvent;
                }
            }

            yield break;
        }

        if (Read(path) is { } single)
        {
            yield return single;
        }
    }

    public static ClapEventSummary Summarize(ClapEvent e) => new(e.Id, e.Time, e.Kind, e.Claps, e.Flag, e.Note, e.Describe());

    private const string ReadMe =
        """
        Home Control clap events

        One JSON file per event: claps it heard (kind "heard") or claps you said it missed
        ("missed"), what you said about it ("flag": right, notClaps, missed) and your note,
        the settings, and what the clap detector worked out over the half minute before.

        There is no sound in here. "recording" holds the detector's own figures: every sound it
        judged, with its verdict and loudness figures in dB; for most of them the loudness of
        three pitch bands (2-7 kHz, 500 Hz-1.2 kHz, below 2 kHz) every 10 ms while they were
        judged ("segments"); and every half second the room's background level and the loudest
        moment ("context"). Blocks are 10 ms, counted from when the microphone opened.
        """;

    private string PathOf(string id) => Path.Combine(Directory, id + ".json");

    private List<ClapEventSummary> Load()
    {
        if (_index is not null)
        {
            return _index;
        }

        var indexPath = Path.Combine(Directory, IndexFile);
        try
        {
            if (File.Exists(indexPath))
            {
                _index = JsonSerializer.Deserialize<List<ClapEventSummary>>(File.ReadAllText(indexPath), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _index = null; // rebuilt from the files below
        }

        if (_index is null || _index.Any(e => !File.Exists(PathOf(e.Id))))
        {
            _index = System.IO.Directory.Exists(Directory)
                ? ReadAll(Directory).Select(Summarize).OrderByDescending(e => e.Time).ToList()
                : [];
        }

        return _index;
    }

    private void Prune(List<ClapEventSummary> index)
    {
        var unflagged = index.Where(e => e.Flag == ClapEventFlag.None).Skip(_keepUnflagged);
        var tooMany = index.Skip(_keepAll);
        foreach (var gone in unflagged.Concat(tooMany).Distinct().ToList())
        {
            index.Remove(gone);
            try
            {
                File.Delete(PathOf(gone.Id));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next prune.
            }
        }
    }

    private void Write(ClapEvent clapEvent)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(clapEvent.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(clapEvent, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private void WriteIndex(List<ClapEventSummary> index)
    {
        _index = index;
        var path = Path.Combine(Directory, IndexFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static ClapEvent? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ClapEvent? Parse(Stream stream)
    {
        try
        {
            return JsonSerializer.Deserialize<ClapEvent>(stream, JsonOptions) is { Id.Length: > 0 } clapEvent ? clapEvent : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
