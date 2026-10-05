using System.Globalization;
using HomeControl.Controls;
using HomeControl.Core.Automations;
using HomeControl.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Devices.Geolocation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace HomeControl.Views;

/// <summary>A row of the automation list.</summary>
public sealed class AutomationListItem
{
    internal AutomationListItem(Automation automation, string description, string status)
    {
        Id = automation.Id;
        Name = automation.Name;
        Enabled = automation.Enabled;
        Description = description;
        Status = status;
        Glyph = automation.Nodes.FirstOrDefault(n => n.Category == NodeCategory.Trigger) is { } trigger ? NodeView.Glyph(trigger) : "";
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public string Status { get; }

    public string Glyph { get; }

    public bool Enabled { get; }
}

/// <summary>Settings › Automations: the location, the list of automations and the node editor.</summary>
public sealed partial class AutomationsPage : Page, INodeEditorContext
{
    private const int MaxUndo = 100;

    private static string? _pendingEdit;
    private static AutomationsPage? _current;

    private readonly Stack<(List<AutomationNode> Nodes, List<AutomationLink> Links)> _undo = new();
    private readonly DispatcherQueueTimer _infoTimer;
    private Automation? _editing;
    private List<GeoPlace> _places = [];
    private string? _placesQuery;
    private CancellationTokenSource? _searchCts;
    private bool _loading;

    public AutomationsPage()
    {
        InitializeComponent();
        NewButton.Flyout = CreateTemplateMenu();
        AddNodeButton.Flyout = NodeCatalog.CreateMenu(entry => GraphCanvas.AddNode(entry.Create()), grouped: true);
        GraphCanvas.Changed += OnCanvasChanged;
        GraphCanvas.Message += (_, message) => ShowRunBar(InfoBarSeverity.Informational, null, message);

        var undo = new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
        undo.Invoked += (_, args) =>
        {
            if (_editing is not null)
            {
                args.Handled = true;
                Undo();
            }
        };
        KeyboardAccelerators.Add(undo);

        // "Next: today 21:14" texts on time triggers.
        _infoTimer = DispatcherQueue.CreateTimer();
        _infoTimer.Interval = TimeSpan.FromSeconds(30);
        _infoTimer.Tick += (_, _) => GraphCanvas.RefreshInfo();
    }

    private static AutomationService Service => App.Host.Automations;

    // ------------------------------------------------------------------ INodeEditorContext

    IReadOnlyList<(string Id, string Name)> INodeEditorContext.Devices =>
        App.Host.Settings.Devices.Where(d => !d.Missing).Select(d => (d.Id, d.Label.Trim())).ToList();

    string INodeEditorContext.NextRunText(TimeTriggerNode trigger)
    {
        if (trigger.At.IsSolar && Service.Document.Location is null)
        {
            return "Set your location (Automations page) for sun times.";
        }

        if (trigger.Days == Weekdays.None)
        {
            return "Pick at least one day.";
        }

        return Service.Engine.NextOccurrence(trigger) is { } next ? $"Next: {Friendly(next)}" : "Doesn't happen in the coming year here.";
    }

    /// <summary>Opens the editor for an automation once the page is shown (used by the smoke test).</summary>
    internal static void Edit(string automationId)
    {
        if (_current is { } page)
        {
            page.OpenEditor(automationId);
        }
        else
        {
            _pendingEdit = automationId;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _current = this;
        Service.Changed += OnServiceChanged;
        App.Host.SettingsApplied += OnServiceChanged;
        LoadLocation();
        RefreshList();
        if (_pendingEdit is { } id)
        {
            _pendingEdit = null;
            OpenEditor(id);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_current == this)
        {
            _current = null;
        }

        Service.Changed -= OnServiceChanged;
        App.Host.SettingsApplied -= OnServiceChanged;
        _infoTimer.Stop();
        _searchCts?.Cancel();
        Service.SaveNow();
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (_editing is null)
        {
            DispatcherQueue.TryEnqueue(RefreshList);
        }
    }

    // ------------------------------------------------------------------ list

    private void RefreshList()
    {
        var deviceIds = App.Host.Settings.Devices.Where(d => !d.Missing).Select(d => d.Id).ToHashSet();
        var hasLocation = Service.Document.Location is not null;
        var items = Service.Document.Automations.Select(a =>
        {
            var issues = AutomationGraph.Validate(a, deviceIds, hasLocation);
            var status = issues.Any(i => i.Severity == IssueSeverity.Error)
                ? "Needs attention: " + issues.First(i => i.Severity == IssueSeverity.Error).Message
                : a.LastRun is { } lastRun
                    ? $"Ran {Friendly(lastRun)}: {a.LastResult}"
                    : "Hasn't run yet";
            return new AutomationListItem(a, AutomationGraph.Summarize(a, DeviceName), status);
        }).ToList();

        AutomationList.ItemsSource = items;
        ListHeader.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private MenuFlyout CreateTemplateMenu()
    {
        var menu = new MenuFlyout();
        foreach (var template in Enum.GetValues<AutomationTemplate>())
        {
            var item = new MenuFlyoutItem { Text = AutomationTemplates.Title(template) };
            item.Click += (_, _) =>
            {
                var automation = AutomationTemplates.Create(template, App.Host.Settings.Devices);
                Service.Document.Automations.Add(automation);
                Service.Save();
                OpenEditor(automation.Id);
            };
            menu.Items.Add(item);
            if (template == AutomationTemplate.Blank)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }
        }

        return menu;
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AutomationListItem item)
        {
            OpenEditor(item.Id);
        }
    }

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not AutomationListItem item ||
            Find(item.Id) is not { } automation || automation.Enabled == toggle.IsOn)
        {
            return; // initial binding, or nothing changed
        }

        automation.Enabled = toggle.IsOn;
        if (!automation.Enabled)
        {
            Service.Engine.Stop(automation.Id);
        }

        Service.Save();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AutomationListItem item || Find(item.Id) is not { } automation)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Delete “{automation.Name}”?",
            Content = "This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            Service.Engine.Stop(automation.Id);
            Service.Document.Automations.Remove(automation);
            Service.Save();
            RefreshList();
        }
    }

    // ------------------------------------------------------------------ editor

    internal void OpenEditor(string automationId)
    {
        if (Find(automationId) is not { } automation)
        {
            return;
        }

        _editing = automation;
        _loading = true;
        NameBox.Text = automation.Name;
        EnabledSwitch.IsOn = automation.Enabled;
        _loading = false;

        _undo.Clear();
        _undo.Push(Snapshot(automation));
        UndoButton.IsEnabled = false;
        RunBar.IsOpen = false;

        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        GraphCanvas.Load(automation, this);
        Validate();
        _infoTimer.Start();
    }

    private void CloseEditor()
    {
        _infoTimer.Stop();
        _editing = null;
        Service.SaveNow();
        EditorPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
        RefreshList();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => CloseEditor();

    private void OnCanvasChanged(object? sender, EventArgs e)
    {
        if (_editing is null)
        {
            return;
        }

        _undo.Push(Snapshot(_editing));
        if (_undo.Count > MaxUndo)
        {
            // Keep the newest entries.
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var entry in keep)
            {
                _undo.Push(entry);
            }
        }

        UndoButton.IsEnabled = true;
        Service.Save();
        Validate();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_editing is null || _undo.Count < 2)
        {
            return;
        }

        _undo.Pop(); // the current state
        var (nodes, links) = _undo.Peek();
        _editing.Nodes = nodes.Select(n => n.Clone()).ToList();
        _editing.Links = links.ToList();
        GraphCanvas.Load(_editing, this, fit: false);
        UndoButton.IsEnabled = _undo.Count > 1;
        Service.Save();
        Validate();
    }

    private static (List<AutomationNode>, List<AutomationLink>) Snapshot(Automation automation) =>
        (automation.Nodes.Select(n => n.Clone()).ToList(), automation.Links.ToList());

    private void Validate()
    {
        if (_editing is null)
        {
            return;
        }

        var deviceIds = App.Host.Settings.Devices.Where(d => !d.Missing).Select(d => d.Id).ToHashSet();
        var issues = AutomationGraph.Validate(_editing, deviceIds, Service.Document.Location is not null);
        GraphCanvas.SetIssues(issues);

        if (issues.Count == 0)
        {
            IssuesBar.IsOpen = false;
            return;
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        IssuesBar.Severity = errors > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        IssuesBar.Title = errors > 0 ? "Not ready yet" : "Check this";
        var messages = issues.Select(i => i.Message).Distinct().ToList();
        IssuesBar.Message = string.Join("  ", messages.Take(3)) + (messages.Count > 3 ? $"  (+{messages.Count - 3} more)" : string.Empty);
        IssuesBar.IsOpen = true;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _editing is null || string.IsNullOrWhiteSpace(NameBox.Text))
        {
            return;
        }

        _editing.Name = NameBox.Text.Trim();
        Service.Save();
    }

    private void OnEditorEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _editing is null)
        {
            return;
        }

        _editing.Enabled = EnabledSwitch.IsOn;
        if (!_editing.Enabled)
        {
            Service.Engine.Stop(_editing.Id);
        }

        Service.Save();
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
        {
            return;
        }

        var triggers = _editing.Nodes.Where(n => n.Category == NodeCategory.Trigger).OrderBy(n => n.Y).ToList();
        if (triggers.Count == 0)
        {
            ShowRunBar(InfoBarSeverity.Warning, "Nothing to run", "Add a trigger first.");
            return;
        }

        if (GraphCanvas.SelectedNode is { Category: NodeCategory.Trigger } selected)
        {
            _ = RunAsync(selected);
        }
        else if (triggers.Count == 1)
        {
            _ = RunAsync(triggers[0]);
        }
        else
        {
            // Several triggers: ask which one to start from.
            var menu = new MenuFlyout();
            foreach (var trigger in triggers)
            {
                var item = new MenuFlyoutItem { Text = NodeText.Describe(trigger, DeviceName), Icon = new FontIcon { Glyph = NodeView.Glyph(trigger) } };
                item.Click += (_, _) => _ = RunAsync(trigger);
                menu.Items.Add(item);
            }

            menu.ShowAt(RunButton);
        }
    }

    private async Task RunAsync(AutomationNode trigger)
    {
        if (_editing is not { } automation)
        {
            return;
        }

        RunButton.IsEnabled = false;
        ShowRunBar(InfoBarSeverity.Informational, "Running…", NodeText.Describe(trigger, DeviceName));
        try
        {
            var result = await Service.RunNowAsync(automation, trigger);
            var steps = result.Steps.Select(s => (s.Success ? "✓ " : "✗ ") + s.Text).ToList();
            ShowRunBar(
                result.Failed ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                result.Failed ? "Ran, with problems" : "Ran",
                steps.Count == 0 ? "Nothing is connected to this trigger." : string.Join("   ", steps));
        }
        catch (Exception ex)
        {
            Log.Error("Running an automation from the editor", ex);
            ShowRunBar(InfoBarSeverity.Error, "Couldn't run it", ex.Message);
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void ShowRunBar(InfoBarSeverity severity, string? title, string message)
    {
        RunBar.Severity = severity;
        RunBar.Title = title ?? string.Empty;
        RunBar.Message = message;
        RunBar.IsOpen = true;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => GraphCanvas.ZoomBy(1.25);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => GraphCanvas.ZoomBy(0.8);

    private void OnFitClick(object sender, RoutedEventArgs e) => GraphCanvas.ZoomToFit();

    // ------------------------------------------------------------------ location

    private void LoadLocation()
    {
        var location = Service.Document.Location;
        _loading = true;
        LatitudeBox.Value = location?.Latitude ?? double.NaN;
        LongitudeBox.Value = location?.Longitude ?? double.NaN;
        _loading = false;

        if (location is null)
        {
            LocationExpander.Description = "Not set. Needed for sunrise, sunset and twilight.";
            return;
        }

        var parts = new List<string>();
        if (Service.TodaysSun() is { } sun)
        {
            void Add(string name, DateTimeOffset? time)
            {
                if (time is { } value)
                {
                    parts.Add($"{name} {value.ToString("t", CultureInfo.CurrentCulture)}");
                }
            }

            Add("dawn", sun.Dawn);
            Add("sunrise", sun.Sunrise);
            Add("sunset", sun.Sunset);
            Add("dusk", sun.Dusk);
        }

        LocationExpander.Description = parts.Count == 0
            ? $"{location}. No sunrise or sunset there today."
            : $"{location}. Today: {string.Join(" · ", parts)}";
    }

    private void SetLocation(GeoLocation location)
    {
        Service.Document.Location = location;
        Service.Save();
        LocationMessage.IsOpen = false;
        LoadLocation();
        GraphCanvas.RefreshInfo();
        Validate();
    }

    private async void OnUseMyLocationClick(object sender, RoutedEventArgs e)
    {
        UseMyLocationButton.IsEnabled = false;
        LocationProgress.IsActive = true;
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                ShowLocationMessage("Windows didn't share this PC's location. Turn on Location (and “Let desktop apps access your location”) in Windows Settings › Privacy & security › Location, or find your town below.");
                return;
            }

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20));
            var point = position.Coordinate.Point.Position;
            SetLocation(new GeoLocation(Math.Round(point.Latitude, 4), Math.Round(point.Longitude, 4), "This PC's location"));
        }
        catch (Exception ex)
        {
            Log.Error("Reading the PC's location", ex);
            ShowLocationMessage("This PC's location isn't available right now. Find your town below instead.");
        }
        finally
        {
            UseMyLocationButton.IsEnabled = true;
            LocationProgress.IsActive = false;
        }
    }

    private async void OnPlaceTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        // The old results belong to the old text: don't let Enter pick one of them.
        _places = [];
        _placesQuery = null;
        sender.ItemsSource = null;
        try
        {
            await Task.Delay(350, cts.Token); // wait until typing pauses
            var query = sender.Text.Trim();
            var places = await Service.Geocoding.SearchAsync(query, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _places = places.ToList();
            _placesQuery = query;
            sender.ItemsSource = _places.Count == 0 && sender.Text.Trim().Length >= 2
                ? new List<string> { "No places found" }
                : _places.Select(p => p.DisplayName).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            Log.Error("Searching for a place", ex);
            ShowLocationMessage("Couldn't search for places (no connection?). You can enter coordinates instead.");
        }
    }

    private void OnPlaceChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        // Moving through the list with the arrow keys: show the place; Enter or a click submits it.
        if (args.SelectedItem is string name && _places.Any(p => p.DisplayName == name))
        {
            sender.Text = name;
        }
    }

    private void OnPlaceSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var place = args.ChosenSuggestion is string name
            ? _places.FirstOrDefault(p => p.DisplayName == name)
            : _places.Count > 0 && _placesQuery == sender.Text.Trim() ? _places[0] : null;
        if (place is not null)
        {
            sender.Text = place.DisplayName;
            SetLocation(place.ToLocation());
        }
    }

    private void OnSaveCoordinatesClick(object sender, RoutedEventArgs e)
    {
        var location = new GeoLocation(LatitudeBox.Value, LongitudeBox.Value);
        if (double.IsNaN(location.Latitude) || double.IsNaN(location.Longitude) || !location.IsValid)
        {
            ShowLocationMessage("Enter a latitude between -90 and 90 and a longitude between -180 and 180.");
            return;
        }

        SetLocation(location);
    }

    private void ShowLocationMessage(string message)
    {
        LocationMessage.Message = message;
        LocationMessage.IsOpen = true;
    }

    // ------------------------------------------------------------------ helpers

    private static Automation? Find(string id) => Service.Document.Automations.FirstOrDefault(a => a.Id == id);

    private static string? DeviceName(string? id) =>
        id is null ? null : App.Host.Settings.Devices.FirstOrDefault(d => d.Id == id && !d.Missing)?.Label.Trim();

    /// <summary>"today 21:14", "tomorrow 07:02", "yesterday 18:00", "Mon 07:00", "12 Oct 07:00".</summary>
    private static string Friendly(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var days = (local.Date - DateTime.Today).Days;
        var clock = local.ToString("t", CultureInfo.CurrentCulture);
        return days switch
        {
            0 => $"today {clock}",
            1 => $"tomorrow {clock}",
            -1 => $"yesterday {clock}",
            > 1 and < 7 => $"{local.ToString("ddd", CultureInfo.CurrentCulture)} {clock}",
            _ => $"{local.ToString("d MMM", CultureInfo.CurrentCulture)} {clock}",
        };
    }
}
