using HomeControl.Core.Automations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace HomeControl.Controls;

/// <summary>The kinds of nodes the editor offers, with their menu texts.</summary>
internal static class NodeCatalog
{
    public sealed record Entry(NodeCategory Category, string Title, string Description, Func<AutomationNode> Create);

    public static readonly Entry[] All =
    [
        new(NodeCategory.Trigger, "Time of day", "A set time, sunrise, sunset or twilight", () => new TimeTriggerNode()),
        new(NodeCategory.Trigger, "PC event", "Locked, unlocked, sleep, wake, idle, display…", () => new PcEventTriggerNode()),
        new(NodeCategory.Trigger, "PC shutdown", "Windows shuts down, restarts or signs out", () => new ShutdownTriggerNode()),
        new(NodeCategory.Condition, "Time window", "Between two times, e.g. sunset and sunrise", () => new TimeWindowConditionNode()),
        new(NodeCategory.Condition, "Day of the week", "Only on some days", () => new DaysConditionNode()),
        new(NodeCategory.Condition, "Device state", "Whether a device is on or off", () => new DeviceStateConditionNode()),
        new(NodeCategory.Condition, "PC state", "Locked, idle, on battery…", () => new PcStateConditionNode()),
        new(NodeCategory.Action, "Device", "Turn a device on or off, or toggle it", () => new DeviceActionNode()),
        new(NodeCategory.Action, "Everything off", "Turn off every device in the tray", () => new AllOffActionNode()),
        new(NodeCategory.Action, "Wait", "Wait before the next step", () => new DelayActionNode()),
        new(NodeCategory.Action, "Notification", "Show a Windows notification", () => new NotifyActionNode()),
        new(NodeCategory.Action, "PC power", "Lock, sleep, display off, shut down", () => new PcPowerActionNode()),
    ];

    /// <summary>A menu of node kinds: grouped in submenus, or flat (conditions and actions only) for wiring.</summary>
    public static MenuFlyout CreateMenu(Action<Entry> pick, bool grouped)
    {
        var menu = new MenuFlyout();
        if (grouped)
        {
            foreach (var group in All.GroupBy(e => e.Category))
            {
                var sub = new MenuFlyoutSubItem
                {
                    Text = group.Key switch { NodeCategory.Trigger => "Trigger (when)", NodeCategory.Condition => "Condition (if)", _ => "Action (then)" },
                };
                foreach (var entry in group)
                {
                    sub.Items.Add(Item(entry, pick));
                }

                menu.Items.Add(sub);
            }
        }
        else
        {
            foreach (var entry in All.Where(e => e.Category != NodeCategory.Trigger))
            {
                if (entry.Category == NodeCategory.Action && menu.Items.Count > 0 && menu.Items[^1] is not MenuFlyoutSeparator &&
                    All.First(e => e.Category == NodeCategory.Action) == entry)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                }

                menu.Items.Add(Item(entry, pick));
            }
        }

        return menu;
    }

    private static MenuFlyoutItem Item(Entry entry, Action<Entry> pick)
    {
        var item = new MenuFlyoutItem { Text = entry.Title, Icon = new FontIcon { Glyph = NodeView.Glyph(entry.Create()) } };
        ToolTipService.SetToolTip(item, entry.Description);
        item.Click += (_, _) => pick(entry);
        return item;
    }
}

/// <summary>
/// The automation graph editor: a zoomable, pannable surface with node cards and bezier wires.
/// Drag a card to move it, drag from an output port to an input port to connect, drop a wire
/// on empty space to add a connected node, click a wire to select it and press Delete (or
/// right-click it) to remove it. Drag the background to pan; Ctrl+wheel zooms.
/// </summary>
public sealed class NodeCanvas : UserControl
{
    private const double GridSize = 24;
    private const double SnapSize = 8;
    private const double MinSurfaceWidth = 3000;
    private const double MinSurfaceHeight = 2000;
    private const double PortSnapDistance = 28;

    private readonly ScrollViewer _scroll;
    private readonly Grid _surface;
    private readonly Path _gridLines;
    private readonly Canvas _wireLayer = new();
    private readonly Canvas _nodeLayer = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Dictionary<string, NodeView> _views = [];
    private readonly Dictionary<AutomationLink, (Path Hit, Path Line)> _wires = [];

    private Automation? _automation;
    private INodeEditorContext? _context;
    private NodeView? _selectedNode;
    private AutomationLink? _selectedLink;
    private bool _fitPending;
    private bool _redrawPending;

    // Gestures
    private NodeView? _dragNode;
    private Point _dragStart;
    private Point _dragOrigin;
    private bool _dragMoved;
    private (AutomationNode Node, string Port)? _wireFrom;
    private Path? _preview;
    private bool _panning;
    private Point _panStart;
    private Point _panOrigin;

    public NodeCanvas()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _gridLines = new Path { Style = StyleOf("NodeGridLineStyle"), IsHitTestVisible = false };
        _surface = new Grid
        {
            Width = MinSurfaceWidth,
            Height = MinSurfaceHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _surface.Children.Add(_gridLines);
        _surface.Children.Add(_wireLayer);
        _surface.Children.Add(_nodeLayer);
        _surface.Children.Add(_overlay);
        _surface.PointerPressed += OnSurfacePressed;
        _surface.PointerMoved += OnSurfaceMoved;
        _surface.PointerReleased += OnSurfaceReleased;
        _surface.PointerCaptureLost += (_, _) => _panning = false;
        _surface.RightTapped += OnSurfaceRightTapped;

        _scroll = new ScrollViewer
        {
            Content = _surface,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.3f,
            MaxZoomFactor = 2f,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsTabStop = false,
        };
        _scroll.SizeChanged += (_, _) =>
        {
            if (_fitPending && _scroll.ActualWidth > 0)
            {
                _fitPending = false;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ZoomToFit);
            }
        };
        Content = _scroll;

        KeyDown += OnKeyDown;
        DrawGrid();
    }

    /// <summary>The graph changed (moved, connected, removed or edited). Save and re-validate.</summary>
    public event EventHandler? Changed;

    public event EventHandler? SelectionChanged;

    /// <summary>Something to tell the user, e.g. why two nodes can't be connected.</summary>
    public event EventHandler<string>? Message;

    public AutomationNode? SelectedNode => _selectedNode?.Node;

    public double Zoom => _scroll.ZoomFactor;

    /// <summary>Shows an automation (replacing the current one), zoomed to fit unless <paramref name="fit"/> is false.</summary>
    public void Load(Automation automation, INodeEditorContext context, bool fit = true)
    {
        _automation = automation;
        _context = context;
        _selectedNode = null;
        _selectedLink = null;
        _nodeLayer.Children.Clear();
        _wireLayer.Children.Clear();
        _views.Clear();
        _wires.Clear();

        foreach (var node in automation.Nodes)
        {
            AddView(node);
        }

        UpdateSurfaceSize();
        ScheduleRedraw();
        if (!fit)
        {
            return;
        }

        _fitPending = true;
        if (_scroll.ActualWidth > 0)
        {
            _fitPending = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ZoomToFit);
        }
    }

    /// <summary>Adds a node at a point of the surface (default: the middle of what's visible).</summary>
    public void AddNode(AutomationNode node, Point? at = null)
    {
        if (_automation is null)
        {
            return;
        }

        var position = at ?? new Point(
            (_scroll.HorizontalOffset + _scroll.ViewportWidth / 2) / _scroll.ZoomFactor - NodeView.NodeWidth / 2,
            (_scroll.VerticalOffset + _scroll.ViewportHeight / 2) / _scroll.ZoomFactor - 80);
        var x = Snap(Math.Max(0, position.X));
        var y = Snap(Math.Max(0, position.Y));
        while (_automation.Nodes.Any(n => Math.Abs(n.X - x) < 1 && Math.Abs(n.Y - y) < 1))
        {
            x += GridSize;
            y += GridSize;
        }

        node.X = x;
        node.Y = y;
        PrepareNew(node);
        _automation.Nodes.Add(node);
        var view = AddView(node);
        UpdateSurfaceSize();
        Select(view);
        ScheduleRedraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks nodes with problems (the first issue of each node is its tooltip).</summary>
    public void SetIssues(IReadOnlyList<AutomationIssue> issues)
    {
        foreach (var (id, view) in _views)
        {
            view.SetIssue(issues.FirstOrDefault(i => i.NodeId == id)?.Message);
        }
    }

    /// <summary>Refreshes texts that depend on the clock or location (e.g. a trigger's next run).</summary>
    public void RefreshInfo()
    {
        foreach (var view in _views.Values)
        {
            view.RefreshInfo();
        }
    }

    public void ZoomBy(double factor)
    {
        var zoom = (float)Math.Clamp(_scroll.ZoomFactor * factor, _scroll.MinZoomFactor, _scroll.MaxZoomFactor);

        // Keep the middle of the view in place.
        var centerX = (_scroll.HorizontalOffset + _scroll.ViewportWidth / 2) / _scroll.ZoomFactor;
        var centerY = (_scroll.VerticalOffset + _scroll.ViewportHeight / 2) / _scroll.ZoomFactor;
        _scroll.ChangeView(centerX * zoom - _scroll.ViewportWidth / 2, centerY * zoom - _scroll.ViewportHeight / 2, zoom);
    }

    /// <summary>Zooms and scrolls so every node is visible (at most 100%).</summary>
    public void ZoomToFit()
    {
        if (_views.Count == 0 || _scroll.ViewportWidth <= 0)
        {
            _scroll.ChangeView(0, 0, 1f, true);
            return;
        }

        var left = _views.Values.Min(v => v.Node.X);
        var top = _views.Values.Min(v => v.Node.Y);
        var right = _views.Values.Max(v => v.Node.X + NodeView.NodeWidth);
        var bottom = _views.Values.Max(v => v.Node.Y + Math.Max(v.ActualHeight, 120));
        const double margin = 40;
        var zoom = (float)Math.Clamp(
            Math.Min(_scroll.ViewportWidth / (right - left + 2 * margin), _scroll.ViewportHeight / (bottom - top + 2 * margin)),
            _scroll.MinZoomFactor,
            1.0);
        _scroll.ChangeView(Math.Max(0, (left - margin) * zoom), Math.Max(0, (top - margin) * zoom), zoom, true);
    }

    // ------------------------------------------------------------------ nodes

    private NodeView AddView(AutomationNode node)
    {
        var view = new NodeView(node, _context!);
        Canvas.SetLeft(view, node.X);
        Canvas.SetTop(view, node.Y);
        view.ManipulationMode = ManipulationModes.All; // touch drags move the node, not the scroll viewer
        view.PointerPressed += OnNodePressed;
        view.PointerMoved += OnNodeMoved;
        view.PointerReleased += OnNodeReleased;
        view.PointerCaptureLost += (_, _) => EndNodeDrag();
        view.SizeChanged += (_, _) => ScheduleRedraw();
        view.Edited += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        view.DeleteRequested += (_, _) => DeleteNode(view);

        foreach (var port in view.OutputPorts.Values.Append(view.InputPort).OfType<FrameworkElement>())
        {
            port.PointerPressed += OnPortPressed;
            port.PointerMoved += OnPortMoved;
            port.PointerReleased += OnPortReleased;
            port.PointerCaptureLost += (_, _) => CancelWire();
        }

        _nodeLayer.Children.Add(view);
        _views[node.Id] = view;
        return view;
    }

    private void PrepareNew(AutomationNode node)
    {
        // Devices: start with the first one, so a new node is useful right away.
        var first = _context?.Devices.FirstOrDefault().Id;
        switch (node)
        {
            case DeviceActionNode { DeviceId: null } action:
                action.DeviceId = first;
                break;
            case DeviceStateConditionNode { DeviceId: null } condition:
                condition.DeviceId = first;
                break;
        }
    }

    private void DeleteNode(NodeView view)
    {
        if (_automation is null)
        {
            return;
        }

        AutomationGraph.RemoveNode(_automation, view.Node.Id);
        _nodeLayer.Children.Remove(view);
        _views.Remove(view.Node.Id);
        if (_selectedNode == view)
        {
            Select(null);
        }

        RedrawWires();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Select(NodeView? view)
    {
        if (_selectedNode is not null)
        {
            _selectedNode.IsSelected = false;
        }

        _selectedNode = view;
        if (view is not null)
        {
            view.IsSelected = true;
            SelectLink(null);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNodePressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not NodeView view || !e.GetCurrentPoint(_nodeLayer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Select(view);
        Focus(FocusState.Programmatic);
        _dragNode = view;
        _dragStart = e.GetCurrentPoint(_nodeLayer).Position;
        _dragOrigin = new Point(view.Node.X, view.Node.Y);
        _dragMoved = false;
        view.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnNodeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragNode is null || sender != _dragNode)
        {
            return;
        }

        var position = e.GetCurrentPoint(_nodeLayer).Position;
        var x = Math.Max(0, _dragOrigin.X + position.X - _dragStart.X);
        var y = Math.Max(0, _dragOrigin.Y + position.Y - _dragStart.Y);
        if (!_dragMoved && Math.Abs(x - _dragOrigin.X) + Math.Abs(y - _dragOrigin.Y) < 3)
        {
            return;
        }

        _dragMoved = true;
        MoveTo(_dragNode, x, y);
        e.Handled = true;
    }

    private void OnNodeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragNode is null || sender != _dragNode)
        {
            return;
        }

        var view = _dragNode;
        view.ReleasePointerCapture(e.Pointer);
        EndNodeDrag();
        e.Handled = true;
    }

    private void EndNodeDrag()
    {
        if (_dragNode is not { } view)
        {
            return;
        }

        _dragNode = null;
        if (_dragMoved)
        {
            MoveTo(view, Snap(view.Node.X), Snap(view.Node.Y));
            UpdateSurfaceSize();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void MoveTo(NodeView view, double x, double y)
    {
        view.Node.X = x;
        view.Node.Y = y;
        Canvas.SetLeft(view, x);
        Canvas.SetTop(view, y);
        RedrawWires();
    }

    // ------------------------------------------------------------------ wires

    private void OnPortPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_automation is null || NodeView.PortInfo(sender) is not { } info || !e.GetCurrentPoint(_nodeLayer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        Focus(FocusState.Programmatic);
        if (info.Port is { } port)
        {
            _wireFrom = (info.Node, port);
        }
        else
        {
            // Grabbing an input picks up the last wire into it, to move or drop it.
            var link = _automation.Links.LastOrDefault(l => l.ToNode == info.Node.Id);
            if (link is null || _automation.FindNode(link.FromNode) is not { } source)
            {
                return;
            }

            _automation.Links.Remove(link);
            RedrawWires();
            Changed?.Invoke(this, EventArgs.Empty);
            _wireFrom = (source, link.FromPort);
        }

        _preview = new Path { Style = StyleOf("WirePreviewStyle"), IsHitTestVisible = false };
        _overlay.Children.Add(_preview);
        UpdatePreview(e.GetCurrentPoint(_nodeLayer).Position);
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void OnPortMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_wireFrom is null)
        {
            return;
        }

        UpdatePreview(e.GetCurrentPoint(_nodeLayer).Position);
        e.Handled = true;
    }

    private void OnPortReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_wireFrom is not { } from || _automation is null)
        {
            return;
        }

        e.Handled = true;
        var position = e.GetCurrentPoint(_nodeLayer).Position;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        CancelWire();

        if (FindInput(position) is { } target)
        {
            if (AutomationGraph.CanConnect(_automation, from.Node.Id, from.Port, target.Node.Id, out var reason))
            {
                _automation.Links.Add(new AutomationLink(from.Node.Id, from.Port, target.Node.Id));
                RedrawWires();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else if (reason is not null)
            {
                Message?.Invoke(this, reason);
            }

            return;
        }

        // Dropped on empty space: offer to add a node there, already connected.
        var menu = NodeCatalog.CreateMenu(entry =>
        {
            var node = entry.Create();
            AddNode(node, new Point(position.X + 16, position.Y - 30));
            if (AutomationGraph.Connect(_automation, from.Node.Id, from.Port, node.Id))
            {
                RedrawWires();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }, grouped: false);
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = position });
    }

    private void CancelWire()
    {
        _wireFrom = null;
        if (_preview is not null)
        {
            _overlay.Children.Remove(_preview);
            _preview = null;
        }
    }

    private void UpdatePreview(Point pointer)
    {
        if (_wireFrom is not { } from || _preview is null || !_views.TryGetValue(from.Node.Id, out var view) ||
            !view.OutputPorts.TryGetValue(from.Port, out var port))
        {
            return;
        }

        // Snap to an input that would accept the wire.
        var end = pointer;
        if (FindInput(pointer) is { } target && _automation is not null &&
            AutomationGraph.CanConnect(_automation, from.Node.Id, from.Port, target.Node.Id, out _))
        {
            end = PortCenter(target, target.InputPort!);
        }

        _preview.Data = Bezier(PortCenter(view, port), end);
    }

    /// <summary>The node whose input port is nearest to a point (within reach), if any.</summary>
    private NodeView? FindInput(Point point)
    {
        var reach = PortSnapDistance / Math.Max(0.5, _scroll.ZoomFactor);
        return _views.Values
            .Where(v => v.InputPort is not null)
            .Select(v => (View: v, Distance: Distance(PortCenter(v, v.InputPort!), point)))
            .Where(c => c.Distance <= reach)
            .OrderBy(c => c.Distance)
            .Select(c => c.View)
            .FirstOrDefault();
    }

    private void ScheduleRedraw()
    {
        if (_redrawPending)
        {
            return;
        }

        _redrawPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _redrawPending = false;
            RedrawWires();
        });
    }

    /// <summary>Creates, updates and removes wire paths to match the links.</summary>
    private void RedrawWires()
    {
        if (_automation is null)
        {
            return;
        }

        var links = _automation.Links.ToHashSet();
        foreach (var gone in _wires.Keys.Where(l => !links.Contains(l)).ToList())
        {
            _wireLayer.Children.Remove(_wires[gone].Hit);
            _wireLayer.Children.Remove(_wires[gone].Line);
            _wires.Remove(gone);
            if (_selectedLink == gone)
            {
                _selectedLink = null;
            }
        }

        foreach (var link in links)
        {
            if (!_views.TryGetValue(link.FromNode, out var from) || !_views.TryGetValue(link.ToNode, out var to) ||
                !from.OutputPorts.TryGetValue(link.FromPort, out var outPort) || to.InputPort is null)
            {
                continue;
            }

            if (!_wires.TryGetValue(link, out var wire))
            {
                wire = (CreateHitPath(link), new Path { IsHitTestVisible = false });
                _wireLayer.Children.Add(wire.Line);
                _wireLayer.Children.Add(wire.Hit);
                _wires[link] = wire;
            }

            wire.Line.Style = StyleOf(WireStyleKey(link));
            var start = PortCenter(from, outPort);
            var end = PortCenter(to, to.InputPort);
            wire.Line.Data = Bezier(start, end);
            wire.Hit.Data = Bezier(start, end);
        }
    }

    private Path CreateHitPath(AutomationLink link)
    {
        var hit = new Path
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            StrokeThickness = 14,
        };
        hit.Tapped += (_, e) =>
        {
            SelectLink(link);
            Focus(FocusState.Programmatic);
            e.Handled = true;
        };
        hit.RightTapped += (_, e) =>
        {
            SelectLink(link);
            var menu = new MenuFlyout();
            var delete = new MenuFlyoutItem { Text = "Delete connection", Icon = new FontIcon { Glyph = "" } };
            delete.Click += (_, _) => DeleteLink(link);
            menu.Items.Add(delete);
            menu.ShowAt(hit, e.GetPosition(hit));
            e.Handled = true;
        };
        hit.PointerPressed += (_, e) => e.Handled = true; // not a pan
        return hit;
    }

    private void SelectLink(AutomationLink? link)
    {
        _selectedLink = link;
        if (link is not null && _selectedNode is not null)
        {
            _selectedNode.IsSelected = false;
            _selectedNode = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        foreach (var (key, wire) in _wires)
        {
            wire.Line.Style = StyleOf(WireStyleKey(key));
        }
    }

    private void DeleteLink(AutomationLink link)
    {
        if (_automation?.Links.Remove(link) == true)
        {
            RedrawWires();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string WireStyleKey(AutomationLink link) =>
        link == _selectedLink ? "WireSelectedStyle" : link.FromPort switch
        {
            Ports.Yes => "WireYesStyle",
            Ports.No => "WireNoStyle",
            _ => "WireStyle",
        };

    /// <summary>A port's centre on the surface, from the node's model position (correct during a drag).</summary>
    private static Point PortCenter(NodeView view, FrameworkElement port)
    {
        if (port.ActualWidth > 0)
        {
            var inNode = port.TransformToVisual(view).TransformPoint(new Point(port.ActualWidth / 2, port.ActualHeight / 2));
            return new Point(view.Node.X + inNode.X, view.Node.Y + inNode.Y);
        }

        // Not laid out yet: the ports sit on the header's edges.
        var x = port == view.InputPort ? view.Node.X : view.Node.X + NodeView.NodeWidth;
        return new Point(x, view.Node.Y + 30);
    }

    private static Geometry Bezier(Point start, Point end)
    {
        var dx = Math.Max(48, Math.Abs(end.X - start.X) / 2);
        var figure = new PathFigure { StartPoint = start, IsFilled = false };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(start.X + dx, start.Y),
            Point2 = new Point(end.X - dx, end.Y),
            Point3 = end,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // ------------------------------------------------------------------ surface

    private void OnSurfacePressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_scroll);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        Select(null);
        SelectLink(null);
        Focus(FocusState.Programmatic);
        _panning = true;
        _panStart = point.Position;
        _panOrigin = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _surface.CapturePointer(e.Pointer);
    }

    private void OnSurfaceMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var position = e.GetCurrentPoint(_scroll).Position;
        _scroll.ChangeView(_panOrigin.X - (position.X - _panStart.X), _panOrigin.Y - (position.Y - _panStart.Y), null, true);
    }

    private void OnSurfaceReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            _surface.ReleasePointerCapture(e.Pointer);
        }
    }

    private void OnSurfaceRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || _automation is null)
        {
            return;
        }

        var position = e.GetPosition(_surface);
        var menu = NodeCatalog.CreateMenu(entry => AddNode(entry.Create(), new Point(position.X, position.Y)), grouped: true);
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = position });
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Only keys aimed at the canvas itself (not at a text box inside a node).
        if (e.OriginalSource != this)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Delete or VirtualKey.Back when _selectedLink is { } link:
                DeleteLink(link);
                e.Handled = true;
                break;
            case VirtualKey.Delete or VirtualKey.Back when _selectedNode is { } node:
                DeleteNode(node);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CancelWire();
                Select(null);
                SelectLink(null);
                e.Handled = true;
                break;
        }
    }

    private void UpdateSurfaceSize()
    {
        var right = _views.Count == 0 ? 0 : _views.Values.Max(v => v.Node.X + NodeView.NodeWidth);
        var bottom = _views.Count == 0 ? 0 : _views.Values.Max(v => v.Node.Y + Math.Max(v.ActualHeight, 200));
        var width = Math.Max(MinSurfaceWidth, Math.Ceiling((right + 1200) / GridSize) * GridSize);
        var height = Math.Max(MinSurfaceHeight, Math.Ceiling((bottom + 900) / GridSize) * GridSize);
        if (width != _surface.Width || height != _surface.Height)
        {
            _surface.Width = width;
            _surface.Height = height;
            DrawGrid();
        }
    }

    private void DrawGrid()
    {
        var group = new GeometryGroup();
        for (var x = GridSize; x < _surface.Width; x += GridSize)
        {
            group.Children.Add(new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, _surface.Height) });
        }

        for (var y = GridSize; y < _surface.Height; y += GridSize)
        {
            group.Children.Add(new LineGeometry { StartPoint = new Point(0, y), EndPoint = new Point(_surface.Width, y) });
        }

        _gridLines.Data = group;
    }

    private static double Snap(double value) => Math.Round(value / SnapSize) * SnapSize;

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];
}
