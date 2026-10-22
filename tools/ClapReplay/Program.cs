using System.Globalization;
using HomeControl.Core.Audio;

// Runs saved clap events (Settings › General › Clap events › Export…) through the clap detector
// as it is now, to see whether a change fixes what was marked wrong without losing what was right.
//
//   dotnet run --project tools/ClapReplay -- <events.zip | folder | event.json> [--sensitivity Low|Medium|High] [--sounds]
//
// --sensitivity replays every event at that sensitivity instead of its own; --sounds lists each
// sound judged in the last few seconds before each event, then and now, with its figures.
// Only the sounds the detector judged when the event was saved have their blocks kept, so a
// replay that is more sensitive than the event's own setting can't judge sounds it didn't track.

if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine("Usage: ClapReplay <events.zip | folder | event.json> [--sensitivity Low|Medium|High] [--sounds]");
    return 1;
}

ClapSensitivity? sensitivity = null;
var sounds = false;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--sounds")
    {
        sounds = true;
    }
    else if (args[i] == "--sensitivity" && i + 1 < args.Length && Enum.TryParse<ClapSensitivity>(args[++i], ignoreCase: true, out var value))
    {
        sensitivity = value;
    }
    else
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        return 1;
    }
}

var events = ClapEventStore.ReadAll(args[0]).OrderBy(e => e.Time).ToList();
if (events.Count == 0)
{
    Console.Error.WriteLine($"No clap events in {args[0]}.");
    return 1;
}

var tally = new Dictionary<string, int>();
void Count(string what) => tally[what] = tally.GetValueOrDefault(what) + 1;
string Figure(double value) => double.IsNaN(value) ? "–" : value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

foreach (var clapEvent in events)
{
    var recording = clapEvent.Recording;
    var asked = sensitivity ?? clapEvent.Sensitivity;
    var counts = clapEvent.Counts.Count > 0 ? clapEvent.Counts : [2, 3, 4];
    var replay = ClapReplay.Run(recording, asked, counts, clapEvent.SpeakerMargin);
    var then = recording.Patterns.Where(p => p.Reported).ToList();
    var now = replay.Reported.Where(p => p.Block >= recording.FromBlock && p.Block <= recording.ToBlock).ToList();

    // The claps the event is about: the pattern it saved (Heard), or any in the time before (Missed).
    var own = clapEvent.Kind == ClapEventKind.Heard ? then.LastOrDefault(p => p.Claps == clapEvent.Claps) : null;
    var heardNow = own is null ? now.Count > 0 : now.Any(p => p.Claps == own.Claps && Math.Abs(p.Block - own.Block) <= 50);
    var outcome = (clapEvent.Flag, clapEvent.Kind) switch
    {
        (ClapEventFlag.NotClaps, _) => heardNow ? "still heard: still wrong" : "no longer heard: fixed",
        (ClapEventFlag.Right, _) => heardNow ? "still heard: still right" : "NO LONGER HEARD: lost",
        (ClapEventFlag.Missed, _) or (_, ClapEventKind.Missed) => heardNow ? "heard now: fixed" : "still missed",
        _ => heardNow ? "heard (not marked)" : "no longer heard (not marked)",
    };
    Count($"{clapEvent.Flag}: {outcome}");

    var differences = sensitivity is null || sensitivity == clapEvent.Sensitivity ? ClapReplay.Differences(recording, replay) : [];
    Console.WriteLine($"{clapEvent.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {clapEvent.Describe()}");
    Console.WriteLine($"    marked {clapEvent.Flag}{(clapEvent.Note is { } note ? $" (“{note}”)" : "")}, {clapEvent.Sensitivity} on {clapEvent.Microphone ?? "?"}{(clapEvent.Raw ? " (raw)" : "")}"
                      + $" → at {asked}: {outcome}; patterns then [{string.Join(", ", then.Select(p => $"{p.Claps}@{recording.SecondsBefore(p.Block):0.0}s"))}]"
                      + $", now [{string.Join(", ", now.Select(p => $"{p.Claps}@{recording.SecondsBefore(p.Block):0.0}s"))}]"
                      + (replay.Unknown > 0 ? $"; {replay.Unknown} sounds kept as judged then (speech, music)" : "")
                      + (replay.Unfinished > 0 ? $"; {replay.Unfinished} sounds without a verdict (their blocks ran out)" : "")
                      + (differences.Count > 0 ? $"; {differences.Count} differences from then" : ""));

    if (sounds)
    {
        var replayed = replay.Sounds.ToLookup(s => s.Onset);
        foreach (var sound in recording.Sounds.Where(s => recording.SecondsBefore(s.Onset) >= -6))
        {
            var verdictNow = replayed[sound.Onset].FirstOrDefault()?.Verdict;
            Console.WriteLine($"      {recording.SecondsBefore(sound.Onset),6:0.00}s  {ClapEvent.Name(sound.Verdict),-17} now {(verdictNow is { } v ? ClapEvent.Name(v) : "–"),-17}"
                              + $" high {Figure(sound.High)} low {Figure(sound.Low)} body {Figure(sound.Body)} body−high {Figure(sound.BodyOverHigh)}"
                              + $" high−low {Figure(sound.HighOverLow)} drop {Figure(sound.Drop)} rise {Figure(sound.Rise)} peak {Figure(sound.Peak)}"
                              + (sound.SpeakerRise is { } speakers ? $" speakers {Figure(speakers)}" : "")
                              + (sound.Detail ? "" : " (no blocks)"));
        }
    }
}

Console.WriteLine();
Console.WriteLine($"{events.Count} events{(sensitivity is { } s ? $" at {s}" : "")}:");
foreach (var (what, count) in tally.OrderBy(t => t.Key))
{
    Console.WriteLine($"  {count,4}  {what}");
}

return 0;
