using System.Text.Json;
using HomeControl.Core.Settings;

namespace HomeControl.Core.Automations;

/// <summary>
/// Loads and saves automations.json. It is a separate file from settings.json, so a problem
/// with one never resets the other.
/// </summary>
public sealed class AutomationStore
{
    private readonly object _gate = new();

    public AutomationStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>Set when the file could not be read; it is kept as ".corrupt".</summary>
    public string? LoadError { get; private set; }

    public AutomationDocument Load()
    {
        lock (_gate)
        {
            LoadError = null;
            if (!File.Exists(FilePath))
            {
                return new AutomationDocument();
            }

            try
            {
                var document = JsonSerializer.Deserialize<AutomationDocument>(File.ReadAllText(FilePath), SettingsStore.JsonOptions)
                               ?? new AutomationDocument();
                Normalize(document);
                return document;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LoadError = ex.Message;
                try
                {
                    File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
                }
                catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
                {
                    // Best effort only.
                }

                return new AutomationDocument();
            }
        }
    }

    public void Save(AutomationDocument document)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(document, SettingsStore.JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    /// <summary>Repairs what a hand-edited or older file could contain.</summary>
    public static void Normalize(AutomationDocument document)
    {
        if (document.Location is { IsValid: false })
        {
            document.Location = null;
        }

        document.Automations ??= [];
        document.Automations.RemoveAll(a => a is null);
        var automationIds = new HashSet<string>();
        foreach (var automation in document.Automations)
        {
            if (string.IsNullOrWhiteSpace(automation.Id) || !automationIds.Add(automation.Id))
            {
                automation.Id = Guid.NewGuid().ToString("N");
                automationIds.Add(automation.Id);
            }

            automation.Name = string.IsNullOrWhiteSpace(automation.Name) ? "Automation" : automation.Name.Trim();
            automation.Nodes ??= [];
            automation.Links ??= [];
            automation.Nodes.RemoveAll(n => n is null);

            var nodeIds = new HashSet<string>();
            foreach (var node in automation.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id) || !nodeIds.Add(node.Id))
                {
                    node.Id = Guid.NewGuid().ToString("N");
                    nodeIds.Add(node.Id);
                }

                if (!double.IsFinite(node.X)) node.X = 0;
                if (!double.IsFinite(node.Y)) node.Y = 0;
                Clamp(node);
            }

            // Drop wires to missing nodes, to triggers, from unknown ports, duplicates and loops.
            var links = automation.Links.Where(l => l is not null).Distinct().ToList();
            automation.Links = [];
            foreach (var link in links)
            {
                AutomationGraph.Connect(automation, link.FromNode, link.FromPort, link.ToNode);
            }
        }
    }

    private static void Clamp(AutomationNode node)
    {
        switch (node)
        {
            case TimeTriggerNode t:
                t.At = Clamp(t.At);
                break;
            case TimeWindowConditionNode w:
                w.From = Clamp(w.From);
                w.To = Clamp(w.To);
                break;
            case PcEventTriggerNode p:
                p.IdleMinutes = Math.Clamp(p.IdleMinutes, 1, 24 * 60);
                break;
            case PcStateConditionNode p:
                p.IdleMinutes = Math.Clamp(p.IdleMinutes, 1, 24 * 60);
                break;
            case DelayActionNode d:
                d.Seconds = Math.Clamp(d.Seconds, 0, 24 * 60 * 60);
                break;
            case NotifyActionNode n:
                n.Message ??= string.Empty;
                break;
        }
    }

    private static TimePoint Clamp(TimePoint? point)
    {
        point ??= new TimePoint();
        point.OffsetMinutes = Math.Clamp(point.OffsetMinutes, -12 * 60, 12 * 60);
        return point;
    }
}
