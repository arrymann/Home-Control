using HomeControl.Controls;
using HomeControl.Core.Models;
using HomeControl.Core.Settings;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace HomeControl.Helpers;

/// <summary>Small functions for x:Bind.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>Unreachable devices are drawn faded.</summary>
    public static double OnlineOpacity(bool online) => online ? 1.0 : 0.5;

    /// <summary>A new geometry for a device icon (geometries cannot be shared between elements).</summary>
    public static Geometry DeviceGeometry(DeviceKind kind, bool filled) => CreateGeometry(DeviceIconData.Get(kind, filled));

    /// <summary>Outline icon, shown while the device is off.</summary>
    public static Geometry DeviceIcon(DeviceKind kind) => DeviceGeometry(kind, false);

    /// <summary>Filled icon, shown while the device is on.</summary>
    public static Geometry DeviceIconOn(DeviceKind kind) => DeviceGeometry(kind, true);

    public static Geometry HomeGeometry() => CreateGeometry(DeviceIconData.Home);

    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Light => "Light",
        DeviceKind.Outlet => "Plug / outlet",
        DeviceKind.Switch => "Switch",
        DeviceKind.Tv => "TV",
        DeviceKind.Speaker => "Speaker",
        DeviceKind.Climate => "Fan / air conditioner",
        DeviceKind.Heater => "Heater / thermostat",
        DeviceKind.Coffee => "Coffee maker",
        DeviceKind.Scene => "Scene / routine",
        _ => "Other",
    };

    private static Geometry CreateGeometry(string data)
    {
        // Tiny invisible marks at (0,0) and (24,24) give every icon the same 24x24 bounds,
        // so icons scale consistently inside a Viewbox or a fixed-size icon slot.
        const string bounds = " M0,0 L0.01,0 L0,0.01 Z M24,24 L23.99,24 L24,23.99 Z";
        try
        {
            return (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data + bounds);
        }
        catch (Exception)
        {
            // Fallback: let the XAML parser build the geometry and detach it from its Path.
            var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
                $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{data}{bounds}\"/>");
            var geometry = path.Data;
            path.Data = null;
            return geometry;
        }
    }
}

internal static class Backdrops
{
    /// <summary>Creates the window material, falling back where Mica is unavailable (Windows 10).</summary>
    public static SystemBackdrop? Create(BackdropKind kind) => kind switch
    {
        BackdropKind.None => null,
        BackdropKind.Mica when MicaController.IsSupported() => new MicaBackdrop { Kind = MicaKind.Base },
        BackdropKind.MicaAlt when MicaController.IsSupported() => new MicaBackdrop { Kind = MicaKind.BaseAlt },
        _ when DesktopAcrylicController.IsSupported() => new DesktopAcrylicBackdrop(),
        _ => null,
    };
}
