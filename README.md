# Home Control

A small WinUI 3 tray app for switching your Google Home devices on and off.

- **Left-click** the tray icon to open a flyout with a toggle for every device.
- **Right-click** for Refresh, Turn all off, Settings and Exit.
- **Global shortcuts.** Give each device its own shortcut (for example `Ctrl + Alt + 1`), plus one that opens the flyout.
- **Follows the Windows theme.** The tray icon matches the taskbar (white on a dark taskbar, black on a light one). The flyout and settings window follow the app light/dark mode and switch live when you change it.
- **Mica.** Both windows use Mica by default. Mica Alt, Acrylic or a plain background can be picked in Settings › General.
- Optional notification after a shortcut toggles a device, and an option to start with Windows.

## How it talks to Google Home

Google's [Home APIs](https://developers.home.google.com/apis) only ship SDKs for Android and iOS. There is no Windows or REST version. The only Google API a Windows app can use to switch *any* device in your Google Home is the **Google Assistant API** (`embeddedassistant.googleapis.com`). Home Control sends it the same text commands you would say to a speaker, such as "turn on Kitchen light". It then reads the answers ("The kitchen light is on.") to work out device state. Home Assistant's *Google Assistant SDK* integration uses the same approach.

What this means in practice:

- You add devices by their **name in the Google Home app**. The API can't list your devices.
- Device state is known after you switch a device, or after **Refresh** (which asks "is Kitchen light on?"). Changes made elsewhere show up on the next refresh. You can also turn on *Check device states when opening*.
- Google is replacing Google Assistant with Gemini on phones (September 2026). The cloud Assistant API used here still works. If Google retires it, the device layer (`IDeviceController`) is the only part that needs replacing.

## Setting up Google access (once, about 5 minutes)

Google requires your own free OAuth client for the Assistant API. The Account page in the app walks you through these steps with links:

1. [Create a Google Cloud project](https://console.cloud.google.com/projectcreate).
2. Enable the [Google Assistant API](https://console.cloud.google.com/apis/library/embeddedassistant.googleapis.com) in it.
3. Configure the [OAuth consent screen](https://console.cloud.google.com/auth/overview). Choose audience *External* and add your Google account as a *test user*.
4. [Create an OAuth client](https://console.cloud.google.com/auth/clients) of type **Desktop app** and download its JSON file.
5. In Home Control open **Settings › Account**, click **Import JSON…**, then **Sign in with Google**.

Sign-in uses the standard installed-app OAuth flow: your browser, a loopback redirect to `127.0.0.1`, and PKCE. The refresh token and client secret are stored encrypted with Windows DPAPI in `%LOCALAPPDATA%\HomeControl\secrets.dat`.

> While the consent screen is in *Testing* mode, Google expires sign-ins after 7 days. To avoid that, set the app to *In production* on the consent screen; for your own account you can click through the "unverified app" warning. If commands don't work, turn on **Personal results** for your account in the Google Home app.

## Building

Requirements: Windows 10 1809 or later (Mica needs Windows 11), the .NET 10 SDK, and optionally Visual Studio 2022/2026 with the *WinUI application development* workload.

```powershell
# run from source
dotnet build src/HomeControl.App -p:Platform=x64
.\src\HomeControl.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\HomeControl.exe

# self-contained build you can copy anywhere (no .NET or Windows App SDK install needed)
dotnet publish src/HomeControl.App -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish
```

Use `-p:Platform=ARM64 -r win-arm64` for ARM devices. Every push also builds both architectures on GitHub Actions and attaches the published app to the run as an artifact. CI also launches the x64 build with `--smoke-test`, which opens the flyout and every settings page in light and dark theme, fails on any runtime error, and uploads screenshots (the *Screenshots* artifact).

The core library (Google sign-in, Assistant client, settings, shortcuts) is cross-platform and has unit tests:

```bash
dotnet test tests/HomeControl.Core.Tests
```

## Using it

| | |
|---|---|
| Left-click tray icon | Open or close the device flyout (Esc or clicking elsewhere closes it) |
| Right-click tray icon | Refresh device states, Turn all off, Settings, Exit |
| Device shortcut | Toggles that device; a notification confirms it (can be turned off) |
| Settings › Devices | Add, edit, reorder and remove devices; record shortcuts; **Try it** buttons |
| Settings › Account | OAuth client, sign in/out, test the connection, language and command phrases |
| Settings › General | Theme, window material, flyout shortcut, notifications, start with Windows |

Starting `HomeControl.exe` a second time opens the flyout of the running instance. With `--background` (used by *Start with Windows*) it starts silently in the tray.

### Other languages and special devices

Commands are built from templates (`turn on {name}`, `turn off {name}`, `is {name} on?`). Answers are interpreted with regular expressions. All of these can be changed under **Settings › Account › Language and commands**, so Home Control works with any Assistant language. Each device can also override its phrases, for example `activate movie night` for a scene.

Settings live in `%LOCALAPPDATA%\HomeControl\settings.json`, next to a log file (`home-control.log`).

## Project layout

```
src/HomeControl.Core         Cross-platform logic: OAuth (loopback + PKCE), Assistant gRPC client,
                             device controller, settings and secret store, shortcut model
src/HomeControl.App          WinUI 3 app: Win32 tray icon and hotkeys, flyout, settings window
tests/HomeControl.Core.Tests xUnit tests, including an in-process fake Assistant gRPC server
tools/generate_assets.py     Regenerates the icons from Fluent UI System Icons
```
