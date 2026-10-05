namespace HomeControl.Core.Automations;

public enum IssueSeverity
{
    /// <summary>The automation can't do anything useful until this is fixed.</summary>
    Error,

    /// <summary>Probably a mistake, but the rest still works.</summary>
    Warning,
}

/// <summary>Something wrong with an automation (or one of its nodes, when <see cref="NodeId"/> is set).</summary>
public sealed record AutomationIssue(IssueSeverity Severity, string Message, string? NodeId = null);

/// <summary>Editing rules and checks for the node graph.</summary>
public static class AutomationGraph
{
    /// <summary>Can a wire go from <paramref name="fromPort"/> of one node to the input of another?</summary>
    public static bool CanConnect(Automation automation, string fromNode, string fromPort, string toNode, out string? reason)
    {
        var from = automation.FindNode(fromNode);
        var to = automation.FindNode(toNode);
        reason = (from, to) switch
        {
            (null, _) or (_, null) => "That node no longer exists.",
            _ when fromNode == toNode => "A node can't connect to itself.",
            _ when !to!.HasInput => "Triggers start an automation; nothing can lead into them.",
            _ when !from!.Outputs.Contains(fromPort) => "That node has no such output.",
            _ when automation.Links.Contains(new AutomationLink(fromNode, fromPort, toNode)) => "These nodes are already connected.",
            _ when Reaches(automation, toNode, fromNode) => "That would make a loop.",
            _ => null,
        };

        return reason is null;
    }

    /// <summary>Adds a wire if <see cref="CanConnect"/> allows it.</summary>
    public static bool Connect(Automation automation, string fromNode, string fromPort, string toNode)
    {
        if (!CanConnect(automation, fromNode, fromPort, toNode, out _))
        {
            return false;
        }

        automation.Links.Add(new AutomationLink(fromNode, fromPort, toNode));
        return true;
    }

    /// <summary>Removes a node and every wire to or from it.</summary>
    public static void RemoveNode(Automation automation, string nodeId)
    {
        automation.Nodes.RemoveAll(n => n.Id == nodeId);
        automation.Links.RemoveAll(l => l.FromNode == nodeId || l.ToNode == nodeId);
    }

    /// <summary>The nodes a port leads to, top to bottom (the order a run reports them in).</summary>
    public static IReadOnlyList<AutomationNode> Targets(Automation automation, string nodeId, string port) =>
        automation.Links
            .Where(l => l.FromNode == nodeId && l.FromPort == port)
            .Select(l => automation.FindNode(l.ToNode))
            .OfType<AutomationNode>()
            .OrderBy(n => n.Y)
            .ThenBy(n => n.X)
            .ToList();

    /// <summary>Nodes reachable from any trigger.</summary>
    public static HashSet<string> Reachable(Automation automation)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>(automation.Nodes.Where(n => n.Category == NodeCategory.Trigger).Select(n => n.Id));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id))
            {
                continue;
            }

            foreach (var link in automation.Links.Where(l => l.FromNode == id))
            {
                queue.Enqueue(link.ToNode);
            }
        }

        return seen;
    }

    /// <summary>Problems worth showing in the editor and the automation list.</summary>
    public static IReadOnlyList<AutomationIssue> Validate(Automation automation, ISet<string> deviceIds, bool hasLocation)
    {
        var issues = new List<AutomationIssue>();
        var triggers = automation.Nodes.Where(n => n.Category == NodeCategory.Trigger).ToList();
        if (triggers.Count == 0)
        {
            issues.Add(new(IssueSeverity.Error, "Add a trigger: it decides when the automation runs."));
        }

        var reachable = Reachable(automation);
        if (triggers.Count > 0 && !automation.Nodes.Any(n => n.Category == NodeCategory.Action && reachable.Contains(n.Id)))
        {
            issues.Add(new(IssueSeverity.Error, "Connect an action to a trigger: nothing happens yet."));
        }

        foreach (var node in automation.Nodes)
        {
            if (node.Category != NodeCategory.Trigger && !reachable.Contains(node.Id))
            {
                issues.Add(new(IssueSeverity.Warning, $"“{NodeText.Title(node)}” isn't connected to a trigger, so it never runs.", node.Id));
            }

            var deviceId = node switch
            {
                DeviceActionNode a => a.DeviceId,
                DeviceStateConditionNode c => c.DeviceId,
                _ => "-",
            };
            if (deviceId is null)
            {
                issues.Add(new(IssueSeverity.Error, $"Pick a device for “{NodeText.Title(node)}”.", node.Id));
            }
            else if (deviceId != "-" && !deviceIds.Contains(deviceId))
            {
                issues.Add(new(IssueSeverity.Error, $"The device of “{NodeText.Title(node)}” was removed. Pick another one.", node.Id));
            }

            if (!hasLocation && UsesSun(node))
            {
                issues.Add(new(IssueSeverity.Error, "Set your location (above the automation list) for sunrise, sunset and twilight.", node.Id));
            }

            if (node is DaysConditionNode { Days: Weekdays.None } or TimeTriggerNode { Days: Weekdays.None })
            {
                issues.Add(new(IssueSeverity.Warning, $"“{NodeText.Title(node)}” has no days selected.", node.Id));
            }
        }

        if (triggers.Any(t => t is ShutdownTriggerNode))
        {
            var afterShutdown = Downstream(automation, triggers.Where(t => t is ShutdownTriggerNode).Select(t => t.Id));
            if (automation.Nodes.Any(n => afterShutdown.Contains(n.Id) && n is DelayActionNode))
            {
                issues.Add(new(IssueSeverity.Warning, "Waits are skipped when the PC shuts down: Windows only gives a few seconds."));
            }

            if (automation.Nodes.Any(n => afterShutdown.Contains(n.Id) && n is PcPowerActionNode))
            {
                issues.Add(new(IssueSeverity.Warning, "PC power actions are ignored when the PC is already shutting down."));
            }
        }

        return issues;
    }

    /// <summary>"When sunset, PC shuts down → Turn on Backlight, Turn every device off".</summary>
    public static string Summarize(Automation automation, Func<string?, string?> deviceName)
    {
        var triggers = automation.Nodes.Where(n => n.Category == NodeCategory.Trigger).OrderBy(n => n.Y).ToList();
        if (triggers.Count == 0)
        {
            return "No trigger yet";
        }

        var reachable = Reachable(automation);
        var actions = automation.Nodes
            .Where(n => n.Category == NodeCategory.Action && reachable.Contains(n.Id))
            .OrderBy(n => n.X)
            .ThenBy(n => n.Y)
            .Select(n => NodeText.Describe(n, deviceName))
            .Distinct()
            .ToList();
        var conditions = automation.Nodes.Any(n => n.Category == NodeCategory.Condition && reachable.Contains(n.Id));

        var when = string.Join("; ", triggers.Select(t => NodeText.Describe(t, deviceName)));
        var then = actions.Count == 0 ? "nothing yet" : string.Join(", ", actions.Take(3)) + (actions.Count > 3 ? $" (+{actions.Count - 3})" : string.Empty);
        return $"{when} → {(conditions ? "if… " : string.Empty)}{then}";
    }

    public static bool UsesSun(AutomationNode node) => node switch
    {
        TimeTriggerNode t => t.At.IsSolar,
        TimeWindowConditionNode w => w.From.IsSolar || w.To.IsSolar,
        _ => false,
    };

    private static HashSet<string> Downstream(Automation automation, IEnumerable<string> starts)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>(starts);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var link in automation.Links.Where(l => l.FromNode == id))
            {
                if (seen.Add(link.ToNode))
                {
                    queue.Enqueue(link.ToNode);
                }
            }
        }

        return seen;
    }

    /// <summary>True when <paramref name="target"/> can be reached by following wires from <paramref name="start"/>.</summary>
    private static bool Reaches(Automation automation, string start, string target) =>
        start == target || Downstream(automation, [start]).Contains(target);
}
