using System.Globalization;
using HomeControl.Core.Automations;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HomeControl.Controls;

/// <summary>What node cards need to know about the rest of the app.</summary>
public interface INodeEditorContext
{
    /// <summary>Devices to pick from (id, label).</summary>
    IReadOnlyList<(string Id, string Name)> Devices { get; }

    /// <summary>"Next: today 21:14", or a hint when it can't be worked out.</summary>
    string NextRunText(TimeTriggerNode trigger);

    /// <summary>Whether claps are being listened for, e.g. "Listening with Microphone Array".</summary>
    string ClapStatusText();
}

/// <summary>
/// One node of the automation editor: a card with a coloured header, inline editors for the
/// node's settings, an input port on the left and output ports on the right. Built in code
/// because the body differs for every node type.
/// </summary>
public sealed class NodeView : UserControl
{
    public const double NodeWidth = 264;
    private const double PortSize = 24;

    private readonly INodeEditorContext _context;
    private readonly Border _card;
    private readonly Border _header;
    private readonly FontIcon _icon;
    private readonly FontIcon _issueIcon;
    private readonly Dictionary<string, FrameworkElement> _outputs = [];
    private readonly List<Action> _refreshers = [];
    private bool _selected;
    private bool _loading;

    public NodeView(AutomationNode node, INodeEditorContext context)
    {
        Node = node;
        _context = context;
        Width = NodeWidth;
        IsTabStop = false;

        var root = new Grid();

        _card = new Border { Style = StyleOf("NodeCardStyle") };
        root.Children.Add(_card);

        var layout = new StackPanel();
        root.Children.Add(layout);

        // Header: icon, title, issue marker, delete button.
        _icon = new FontIcon { Glyph = Glyph(node), Style = StyleOf("NodeBadgeIconStyle") };
        var iconBadge = new Border
        {
            Style = StyleOf(node.Category switch
            {
                NodeCategory.Trigger => "NodeTriggerBadgeStyle",
                NodeCategory.Condition => "NodeConditionBadgeStyle",
                _ => "NodeActionBadgeStyle",
            }),
            Child = _icon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = NodeText.Title(node),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var kind = new TextBlock
        {
            Text = node.Category switch { NodeCategory.Trigger => "When", NodeCategory.Condition => "If", _ => "Then" },
            Style = StyleOf("NodeCaptionStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _issueIcon = new FontIcon
        {
            Style = StyleOf("NodeIssueIconStyle"),
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var delete = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Style = StyleOf("NodeDeleteButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(delete, "Delete node");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, "Delete node");
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

        var headerGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 8, 6, 8) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(kind);
        titles.Children.Add(title);
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(_issueIcon, 2);
        Grid.SetColumn(delete, 3);
        headerGrid.Children.Add(iconBadge);
        headerGrid.Children.Add(titles);
        headerGrid.Children.Add(_issueIcon);
        headerGrid.Children.Add(delete);

        _header = new Border { Child = headerGrid, Style = StyleOf("NodeHeaderStyle") };
        layout.Children.Add(_header);

        var body = new StackPanel { Spacing = 8, Padding = new Thickness(12, 10, 12, 12) };
        _loading = true;
        BuildBody(body);
        _loading = false;
        layout.Children.Add(body);

        // Ports: the input sits on the header's left edge, outputs on the right.
        if (node.HasInput)
        {
            var input = CreatePort(node, null);
            input.HorizontalAlignment = HorizontalAlignment.Left;
            input.VerticalAlignment = VerticalAlignment.Top;
            input.Margin = new Thickness(-PortSize / 2, 18, 0, 0);
            root.Children.Add(input);
            InputPort = input;
        }

        if (node.Category == NodeCategory.Condition)
        {
            var outputs = new StackPanel { Spacing = 4, Padding = new Thickness(0, 0, 0, 10) };
            foreach (var (port, label) in new[] { (Ports.Yes, "Yes"), (Ports.No, "No") })
            {
                var row = new Grid { Height = PortSize };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    Style = StyleOf("NodeCaptionStyle"),
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 18, 0),
                });
                var element = CreatePort(node, port);
                element.HorizontalAlignment = HorizontalAlignment.Right;
                element.Margin = new Thickness(0, 0, -PortSize / 2, 0);
                row.Children.Add(element);
                outputs.Children.Add(row);
                _outputs[port] = element;
            }

            layout.Children.Add(outputs);
        }
        else
        {
            var output = CreatePort(node, Ports.Then);
            output.HorizontalAlignment = HorizontalAlignment.Right;
            output.VerticalAlignment = VerticalAlignment.Top;
            output.Margin = new Thickness(0, 18, -PortSize / 2, 0);
            root.Children.Add(output);
            _outputs[Ports.Then] = output;
        }

        Content = root;
    }

    public AutomationNode Node { get; }

    public FrameworkElement? InputPort { get; }

    public IReadOnlyDictionary<string, FrameworkElement> OutputPorts => _outputs;

    /// <summary>A setting of the node changed.</summary>
    public event EventHandler? Edited;

    public event EventHandler? DeleteRequested;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            _card.Style = StyleOf(value ? "NodeCardSelectedStyle" : "NodeCardStyle");
        }
    }

    /// <summary>Shows a warning marker in the header (null hides it).</summary>
    public void SetIssue(string? message)
    {
        _issueIcon.Visibility = Shown(message is not null);
        ToolTipService.SetToolTip(_issueIcon, message);
    }

    /// <summary>Updates texts that depend on the clock or the location (e.g. the next run).</summary>
    public void RefreshInfo()
    {
        foreach (var refresh in _refreshers)
        {
            refresh();
        }
    }

    /// <summary>Port element owner and name: (node, null) is the input.</summary>
    public static (AutomationNode Node, string? Port)? PortInfo(object element) =>
        (element as FrameworkElement)?.Tag is ValueTuple<AutomationNode, string> tag ? (tag.Item1, tag.Item2) : null;

    /// <summary>The style of a node's ports (their colour shows the node's category).</summary>
    public static string PortStyleKey(NodeCategory category) => category switch
    {
        NodeCategory.Trigger => "NodeTriggerPortStyle",
        NodeCategory.Condition => "NodeConditionPortStyle",
        _ => "NodeActionPortStyle",
    };

    public static string Glyph(AutomationNode node) => node switch
    {
        TimeTriggerNode { At.IsSolar: true } => "",
        TimeTriggerNode => "",
        PcEventTriggerNode => "",
        ShutdownTriggerNode => "",
        ClapTriggerNode => "\uE720",
        TimeWindowConditionNode => "",
        DaysConditionNode => "",
        DeviceStateConditionNode => "",
        PcStateConditionNode => "",
        DeviceActionNode => "",
        AllOffActionNode => "",
        DelayActionNode => "",
        NotifyActionNode => "",
        PcPowerActionNode => "",
        _ => "",
    };

    private static FrameworkElement CreatePort(AutomationNode node, string? port)
    {
        var dot = new Ellipse
        {
            Style = StyleOf(PortStyleKey(node.Category)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hit = new Grid
        {
            Width = PortSize,
            Height = PortSize,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = (node, port),
            ManipulationMode = Microsoft.UI.Xaml.Input.ManipulationModes.All, // keep touch drags away from the scroll viewer
        };
        hit.Children.Add(dot);
        ToolTipService.SetToolTip(hit, port switch
        {
            null => "Input: drag a wire here",
            Ports.Yes => "Yes: drag to what happens when this is true",
            Ports.No => "No: drag to what happens when this is false",
            _ => "Drag to what happens next",
        });
        return hit;
    }

    // ------------------------------------------------------------------ editors

    private void BuildBody(StackPanel body)
    {
        switch (Node)
        {
            case TimeTriggerNode t:
                body.Children.Add(TimePointEditor(t.At, "When", () => _icon.Glyph = Glyph(Node)));
                body.Children.Add(DaysEditor(() => t.Days, days => t.Days = days));
                body.Children.Add(InfoText(() => _context.NextRunText(t)));
                break;

            case PcEventTriggerNode p:
            {
                var minutes = MinutesBox("Minutes without input", p.IdleMinutes, v => p.IdleMinutes = v);
                body.Children.Add(Choice("Event", p.Event, PcEventName, v =>
                {
                    p.Event = v;
                    minutes.Visibility = Shown(v is PcEvent.Idle or PcEvent.Active);
                }));
                minutes.Visibility = Shown(p.Event is PcEvent.Idle or PcEvent.Active);
                body.Children.Add(minutes);
                break;
            }

            case ShutdownTriggerNode s:
                body.Children.Add(Choice("When Windows", s.Kind, v => v switch
                {
                    ShutdownKind.SignOut => "signs out",
                    ShutdownKind.Any => "shuts down, restarts or signs out",
                    _ => "shuts down or restarts",
                }, v => s.Kind = v));
                body.Children.Add(Caption("Windows waits a few seconds for these actions; waits are skipped."));
                break;

            case ClapTriggerNode c:
                body.Children.Add(Choice("Claps in a row", c.Count, v => $"{v} claps", v => c.Count = v,
                    Enumerable.Range(ClapTriggerNode.MinCount, ClapTriggerNode.MaxCount - ClapTriggerNode.MinCount + 1).ToArray()));
                body.Children.Add(InfoText(_context.ClapStatusText));
                body.Children.Add(Caption("Uses the microphone while clap listening is on (Settings › General). Sound is analysed as it arrives and never kept."));
                break;

            case TimeWindowConditionNode w:
                body.Children.Add(TimePointEditor(w.From, "From", null));
                body.Children.Add(TimePointEditor(w.To, "To", null));
                body.Children.Add(Caption("Can run past midnight, e.g. sunset to sunrise."));
                break;

            case DaysConditionNode d:
                body.Children.Add(DaysEditor(() => d.Days, days => d.Days = days));
                break;

            case DeviceStateConditionNode d:
                body.Children.Add(DevicePicker(() => d.DeviceId, id => d.DeviceId = id));
                body.Children.Add(Choice("Is", d.IsOn, v => v ? "on" : "off", v => d.IsOn = v, [true, false]));
                break;

            case PcStateConditionNode p:
            {
                var minutes = MinutesBox("Minutes", p.IdleMinutes, v => p.IdleMinutes = v);
                body.Children.Add(Choice("The PC", p.State, PcStateName, v =>
                {
                    p.State = v;
                    minutes.Visibility = Shown(v is PcState.Idle or PcState.InUse);
                }));
                minutes.Visibility = Shown(p.State is PcState.Idle or PcState.InUse);
                body.Children.Add(minutes);
                break;
            }

            case DeviceActionNode a:
                body.Children.Add(Choice("Do", a.Command, v => v switch
                {
                    DeviceCommand.TurnOff => "Turn off",
                    DeviceCommand.Toggle => "Toggle",
                    _ => "Turn on",
                }, v => a.Command = v));
                body.Children.Add(DevicePicker(() => a.DeviceId, id => a.DeviceId = id));
                break;

            case AllOffActionNode:
                body.Children.Add(Caption("Turns off every device shown in the tray."));
                break;

            case DelayActionNode d:
                body.Children.Add(DelayEditor(d));
                break;

            case NotifyActionNode n:
            {
                var box = new TextBox { Header = "Message", Text = n.Message, PlaceholderText = "e.g. Lights are on", MaxLength = 200 };
                box.TextChanged += (_, _) =>
                {
                    n.Message = box.Text;
                    OnEdited();
                };
                body.Children.Add(box);
                break;
            }

            case PcPowerActionNode p:
            {
                var note = Caption($"Windows shows a warning and waits {PcPowerWarning}; cancel it from the tray menu.");
                body.Children.Add(Choice("Do", p.Command, v => v switch
                {
                    PcPowerCommand.Sleep => "Sleep",
                    PcPowerCommand.ShutDown => "Shut down",
                    PcPowerCommand.Restart => "Restart",
                    PcPowerCommand.DisplayOff => "Turn display off",
                    _ => "Lock",
                }, v =>
                {
                    p.Command = v;
                    note.Visibility = Shown(v is PcPowerCommand.ShutDown or PcPowerCommand.Restart);
                }));
                note.Visibility = Shown(p.Command is PcPowerCommand.ShutDown or PcPowerCommand.Restart);
                body.Children.Add(note);
                break;
            }
        }
    }

    private const string PcPowerWarning = "one minute";

    private static string PcEventName(PcEvent value) => value switch
    {
        PcEvent.Started => "Home Control starts",
        PcEvent.Locked => "PC is locked",
        PcEvent.Unlocked => "PC is unlocked",
        PcEvent.Sleeping => "PC goes to sleep",
        PcEvent.Resumed => "PC wakes up",
        PcEvent.Idle => "PC is idle",
        PcEvent.Active => "You're back (after idle)",
        PcEvent.DisplayOff => "Display turns off",
        PcEvent.DisplayOn => "Display turns on",
        PcEvent.OnBattery => "PC switches to battery",
        PcEvent.PluggedIn => "PC is plugged in",
        _ => value.ToString(),
    };

    private static string PcStateName(PcState value) => value switch
    {
        PcState.Locked => "is locked",
        PcState.Unlocked => "is unlocked",
        PcState.Idle => "is idle (no input)",
        PcState.InUse => "is in use",
        PcState.OnBattery => "is on battery",
        PcState.PluggedIn => "is plugged in",
        PcState.DisplayOn => "has the display on",
        PcState.DisplayOff => "has the display off",
        _ => value.ToString(),
    };

    private static string ReferenceLabel(TimeReference reference) => reference switch
    {
        TimeReference.Clock => "A set time",
        TimeReference.Dawn => "Dawn (twilight begins)",
        TimeReference.Dusk => "Dusk (twilight ends)",
        TimeReference.NauticalDawn => "Nautical dawn",
        TimeReference.NauticalDusk => "Nautical dusk",
        TimeReference.AstronomicalDawn => "Astronomical dawn",
        TimeReference.AstronomicalDusk => "Astronomical dusk",
        TimeReference.SolarNoon => "Solar noon",
        TimeReference.Sunrise => "Sunrise",
        TimeReference.Sunset => "Sunset",
        _ => reference.ToString(),
    };

    /// <summary>"A set time" with a time picker, or a sun event with "N min before/after".</summary>
    private FrameworkElement TimePointEditor(TimePoint point, string header, Action? referenceChanged)
    {
        var panel = new StackPanel { Spacing = 6 };

        var picker = new TimePicker
        {
            Time = point.Time.ToTimeSpan(),
            MinuteIncrement = 1,
            ClockIdentifier = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H') ? "24HourClock" : "12HourClock",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
        };
        picker.SelectedTimeChanged += (_, e) =>
        {
            if (e.NewTime is { } time)
            {
                point.Time = TimeOnly.FromTimeSpan(time);
                OnEdited();
            }
        };

        var amount = new NumberBox
        {
            Minimum = 0,
            Maximum = 720,
            Value = Math.Abs(point.OffsetMinutes),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 5,
            LargeChange = 30,
            Width = 96,
        };
        var direction = new ComboBox { ItemsSource = new[] { "min before", "min after" }, SelectedIndex = point.OffsetMinutes < 0 ? 0 : 1, MinWidth = 0 };
        void UpdateOffset()
        {
            if (_loading || double.IsNaN(amount.Value))
            {
                return;
            }

            var minutes = (int)Math.Round(Math.Clamp(amount.Value, 0, 720));
            point.OffsetMinutes = direction.SelectedIndex == 0 ? -minutes : minutes;
            OnEdited();
        }

        amount.ValueChanged += (_, _) => UpdateOffset();
        direction.SelectionChanged += (_, _) => UpdateOffset();
        var offsetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        offsetRow.Children.Add(amount);
        offsetRow.Children.Add(direction);

        void ShowFor(TimeReference reference)
        {
            picker.Visibility = Shown(reference == TimeReference.Clock);
            offsetRow.Visibility = Shown(reference != TimeReference.Clock);
        }

        panel.Children.Add(Choice(header, point.Reference, ReferenceLabel, reference =>
        {
            point.Reference = reference;
            ShowFor(reference);
            referenceChanged?.Invoke();
        }));
        panel.Children.Add(picker);
        panel.Children.Add(offsetRow);
        ShowFor(point.Reference);
        return panel;
    }

    private FrameworkElement DaysEditor(Func<Weekdays> get, Action<Weekdays> set)
    {
        var row = new Grid { ColumnSpacing = 3 };
        var days = Enum.GetValues<DayOfWeek>().OrderBy(d => ((int)d + 6) % 7).ToList(); // Monday first
        for (var i = 0; i < days.Count; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var flag = TimeSchedule.ToWeekdays(days[i]);
            var name = CultureInfo.CurrentCulture.DateTimeFormat.GetShortestDayName(days[i]);
            var toggle = new ToggleButton
            {
                Content = name,
                IsChecked = (get() & flag) != 0,
                Padding = new Thickness(0, 4, 0, 4),
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTipService.SetToolTip(toggle, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(days[i]));
            toggle.Click += (_, _) =>
            {
                set(toggle.IsChecked == true ? get() | flag : get() & ~flag);
                OnEdited();
            };
            Grid.SetColumn(toggle, i);
            row.Children.Add(toggle);
        }

        return row;
    }

    private FrameworkElement DevicePicker(Func<string?> get, Action<string?> set)
    {
        var devices = _context.Devices.ToList();
        var current = get();
        if (current is not null && devices.All(d => d.Id != current))
        {
            devices.Insert(0, (current, "(removed device)"));
        }

        var box = new ComboBox
        {
            Header = "Device",
            ItemsSource = devices.Select(d => d.Name).ToList(),
            SelectedIndex = devices.FindIndex(d => d.Id == current),
            PlaceholderText = devices.Count == 0 ? "No devices yet" : "Pick a device",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.SelectionChanged += (_, _) =>
        {
            if (!_loading && box.SelectedIndex >= 0)
            {
                set(devices[box.SelectedIndex].Id);
                OnEdited();
            }
        };
        return box;
    }

    private FrameworkElement DelayEditor(DelayActionNode node)
    {
        var minutes = new NumberBox { Header = "Minutes", Minimum = 0, Maximum = 1440, Value = node.Seconds / 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var seconds = new NumberBox { Header = "Seconds", Minimum = 0, Maximum = 59, Value = node.Seconds % 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        void Update()
        {
            if (_loading)
            {
                return;
            }

            var m = double.IsNaN(minutes.Value) ? 0 : (int)Math.Clamp(minutes.Value, 0, 1440);
            var s = double.IsNaN(seconds.Value) ? 0 : (int)Math.Clamp(seconds.Value, 0, 59);
            node.Seconds = m * 60 + s;
            OnEdited();
        }

        minutes.ValueChanged += (_, _) => Update();
        seconds.ValueChanged += (_, _) => Update();
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(seconds, 1);
        row.Children.Add(minutes);
        row.Children.Add(seconds);
        return row;
    }

    private NumberBox MinutesBox(string header, int value, Action<int> set)
    {
        var box = new NumberBox
        {
            Header = header,
            Minimum = 1,
            Maximum = 1440,
            Value = value,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 1,
            LargeChange = 10,
        };
        box.ValueChanged += (_, _) =>
        {
            if (!_loading && !double.IsNaN(box.Value))
            {
                set((int)Math.Clamp(box.Value, 1, 1440));
                OnEdited();
            }
        };
        return box;
    }

    /// <summary>A labelled ComboBox over an enum (or the given values).</summary>
    private ComboBox Choice<T>(string header, T value, Func<T, string> label, Action<T> set, T[]? values = null) where T : notnull
    {
        var items = values ?? (typeof(T).IsEnum ? Enum.GetValues(typeof(T)).Cast<T>().ToArray() : [value]);
        var box = new ComboBox
        {
            Header = header,
            ItemsSource = items.Select(label).ToList(),
            SelectedIndex = Array.IndexOf(items, value),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.SelectionChanged += (_, _) =>
        {
            if (!_loading && box.SelectedIndex >= 0)
            {
                set(items[box.SelectedIndex]);
                OnEdited();
            }
        };
        return box;
    }

    private TextBlock InfoText(Func<string> text)
    {
        var block = Caption(text());
        _refreshers.Add(() => block.Text = text());
        return block;
    }

    private static TextBlock Caption(string text) => new() { Text = text, Style = StyleOf("NodeCaptionStyle") };

    private void OnEdited()
    {
        if (_loading)
        {
            return;
        }

        RefreshInfo();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private static Visibility Shown(bool visible) => visible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];
}
