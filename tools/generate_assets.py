#!/usr/bin/env python3
"""Generates the app's icon assets from Fluent UI System Icons (MIT licensed).

Outputs:
  src/HomeControl.App/Assets/AppIcon.ico          colored app icon (exe, windows)
  src/HomeControl.App/Assets/TrayIcon.Dark.ico    white glyph for a dark taskbar
  src/HomeControl.App/Assets/TrayIcon.Light.ico   black glyph for a light taskbar
  src/HomeControl.App/Controls/DeviceIconData.g.cs  XAML path data for device icons

Requirements: pip install cairosvg svg.path pillow
Usage:        python3 tools/generate_assets.py [path/to/@fluentui/svg-icons/icons]
Without an argument the npm package is downloaded to a temporary folder.
"""

import io
import json
import os
import re
import struct
import sys
import tarfile
import tempfile
import urllib.request

import cairosvg
from PIL import Image
from svg.path import Arc, Close, CubicBezier, Line, Move, QuadraticBezier, parse_path

PACKAGE = "@fluentui/svg-icons"
PACKAGE_VERSION = "1.1.343"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(ROOT, "src", "HomeControl.App")

# DeviceKind -> (icon when off, icon when on)
DEVICE_ICONS = {
    "Light": ("lightbulb_24_regular", "lightbulb_24_filled"),
    "Outlet": ("plug_connected_24_regular", "plug_connected_24_filled"),
    "Switch": ("toggle_left_24_regular", "toggle_right_24_filled"),
    "Tv": ("tv_24_regular", "tv_24_filled"),
    "Speaker": ("speaker_2_24_regular", "speaker_2_24_filled"),
    "Climate": ("weather_snowflake_24_regular", "weather_snowflake_24_filled"),
    "Heater": ("temperature_24_regular", "temperature_24_filled"),
    "Coffee": ("drink_coffee_24_regular", "drink_coffee_24_filled"),
    "Scene": ("sparkle_24_regular", "sparkle_24_filled"),
    "Other": ("power_24_regular", "power_24_filled"),
}

TRAY_SIZES = [16, 20, 24, 32, 40, 48, 64]
APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def fetch_icons_dir():
    if len(sys.argv) > 1:
        return sys.argv[1]
    meta_url = f"https://registry.npmjs.org/{PACKAGE}/{PACKAGE_VERSION}"
    with urllib.request.urlopen(meta_url) as response:
        tarball = json.load(response)["dist"]["tarball"]
    target = tempfile.mkdtemp(prefix="fluent-icons-")
    with urllib.request.urlopen(tarball) as response:
        with tarfile.open(fileobj=io.BytesIO(response.read()), mode="r:gz") as archive:
            archive.extractall(target, filter="data")
    return os.path.join(target, "package", "icons")


def read_svg(icons_dir, name):
    with open(os.path.join(icons_dir, name + ".svg"), encoding="utf-8") as f:
        return f.read()


def path_data(svg):
    return " ".join(re.findall(r'<path[^>]* d="([^"]+)"', svg))


def fmt(value):
    text = f"{value:.3f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


def point(c):
    return f"{fmt(c.real)},{fmt(c.imag)}"


def to_xaml_path(d):
    """Re-serializes SVG path data with absolute commands and explicit separators,
    which the XAML path mini-language parses reliably. F1 = nonzero fill like SVG."""
    out = ["F1"]
    for seg in parse_path(d):
        if isinstance(seg, Move):
            out.append("M" + point(seg.end))
        elif isinstance(seg, Close):
            out.append("Z")
        elif isinstance(seg, Line):
            out.append("L" + point(seg.end))
        elif isinstance(seg, CubicBezier):
            out.append(f"C{point(seg.control1)} {point(seg.control2)} {point(seg.end)}")
        elif isinstance(seg, QuadraticBezier):
            out.append(f"Q{point(seg.control)} {point(seg.end)}")
        elif isinstance(seg, Arc):
            out.append(
                f"A{fmt(seg.radius.real)},{fmt(seg.radius.imag)} {fmt(seg.rotation)} "
                f"{1 if seg.arc else 0} {1 if seg.sweep else 0} {point(seg.end)}"
            )
        else:
            raise ValueError(f"Unsupported segment {seg!r}")
    return " ".join(out)


def render(svg, size):
    png = cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def write_ico(path, images):
    """Writes an .ico with 32-bit BMP entries (PNG for 256 px), largest last."""
    entries = []
    for img in sorted(images, key=lambda i: i.width):
        w, h = img.size
        if w >= 256:
            buffer = io.BytesIO()
            img.save(buffer, format="PNG")
            data = buffer.getvalue()
        else:
            header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
            pixels = bytearray()
            for y in range(h - 1, -1, -1):  # bottom-up rows, BGRA
                for x in range(w):
                    r, g, b, a = img.getpixel((x, y))
                    pixels += bytes((b, g, r, a))
            mask_stride = ((w + 31) // 32) * 4
            data = header + bytes(pixels) + bytes(mask_stride * h)
        entries.append((w, h, data))

    offset = 6 + 16 * len(entries)
    directory = b""
    blobs = b""
    for w, h, data in entries:
        directory += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(data), offset + len(blobs))
        blobs += data
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(entries)) + directory + blobs)


def tray_svg(icons_dir, size, color):
    # Use the variant drawn for the closest design size that is not larger than the target.
    design = max(s for s in (16, 20, 24, 28, 32, 48) if s <= max(size, 16))
    svg = read_svg(icons_dir, f"home_{design}_regular")
    return svg.replace("<path ", f'<path fill="{color}" ', 1)


def app_svg(icons_dir):
    home = path_data(read_svg(icons_dir, "home_24_filled"))
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#47B1F5"/>
      <stop offset="1" stop-color="#0063B1"/>
    </linearGradient>
  </defs>
  <rect x="2" y="2" width="44" height="44" rx="10" fill="url(#bg)"/>
  <g transform="translate(9.6 9.2) scale(1.2)"><path d="{home}" fill="#FFFFFF"/></g>
  <circle cx="34.5" cy="13.5" r="4.5" fill="#FFD54A" stroke="#0063B1" stroke-width="1.5"/>
</svg>"""


def main():
    icons_dir = fetch_icons_dir()
    assets = os.path.join(APP, "Assets")
    os.makedirs(assets, exist_ok=True)

    for name, color in (("TrayIcon.Dark.ico", "#FFFFFF"), ("TrayIcon.Light.ico", "#000000")):
        write_ico(os.path.join(assets, name), [render(tray_svg(icons_dir, s, color), s) for s in TRAY_SIZES])

    app = app_svg(icons_dir)
    write_ico(os.path.join(assets, "AppIcon.ico"), [render(app, s) for s in APP_SIZES])
    render(app, 256).save(os.path.join(assets, "AppIcon.png"))

    lines = [
        "// <auto-generated>",
        "// Generated by tools/generate_assets.py from Fluent UI System Icons",
        f"// ({PACKAGE} {PACKAGE_VERSION}, MIT license, https://github.com/microsoft/fluentui-system-icons).",
        "// Do not edit by hand.",
        "// </auto-generated>",
        "",
        "using HomeControl.Core.Models;",
        "",
        "namespace HomeControl.Controls;",
        "",
        "internal static class DeviceIconData",
        "{",
        f'    public const string Home = "{to_xaml_path(path_data(read_svg(icons_dir, "home_24_regular")))}";',
        "",
        "    /// <summary>Path data (24x24 viewbox) for a device kind; the filled variant is used while the device is on.</summary>",
        "    public static string Get(DeviceKind kind, bool filled) => (kind, filled) switch",
        "    {",
    ]
    for kind, (regular, filled) in DEVICE_ICONS.items():
        for is_filled, icon in ((False, regular), (True, filled)):
            data = to_xaml_path(path_data(read_svg(icons_dir, icon)))
            lines.append(f'        (DeviceKind.{kind}, {"true" if is_filled else "false"}) => "{data}",')
    lines += [
        '        (_, false) => Get(DeviceKind.Other, false),',
        '        (_, true) => Get(DeviceKind.Other, true),',
        "    };",
        "}",
        "",
    ]
    with open(os.path.join(APP, "Controls", "DeviceIconData.g.cs"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))

    print("Assets written to", assets)


if __name__ == "__main__":
    main()
