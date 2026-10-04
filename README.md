# Home Control

A small WinUI 3 tray app for switching your Google Home devices on and off.

- **Left-click** the tray icon to open a flyout with a toggle for every device.
- **Right-click** for Refresh, Turn all off, Settings and Exit.
- **Global shortcuts.** Give each device its own shortcut (for example `Ctrl + Alt + 1`), plus one that opens the flyout.
- **Follows the Windows theme.** The tray icon matches the taskbar (white on a dark taskbar, black on a light one). The flyout and settings window follow the app light/dark mode and switch live when you change it.
- **Mica.** Both windows use Mica by default. Mica Alt, Acrylic or a plain background can be picked in Settings › General.
- Optional notification after a shortcut toggles a device, and an option to start with Windows.

## How it talks to Google Home

Google's [Home APIs](https://developers.home.google.com/apis) only ship SDKs for Android and iOS. Its [Home MCP server](https://developers.home.google.com/) needs a US Google Home Premium Advanced subscription. So Home Control uses two other routes:

### Google Home on the web (default)

Home Control signs in to [home.google.com](https://home.google.com) once, in a small browser window inside the app (Microsoft Edge WebView2). After that it keeps that page loaded in a hidden browser and sends the same requests the web app sends. Those requests go to Google's `googlehomefoyer-pa` API and are authorized from the page's own session.

- **Devices are listed automatically.** Every device that can be turned on and off (lights, plugs, switches, TVs and so on) is added, with its room. Devices added later in Google Home appear at the next start or after **Sync now**. If a sync fails (no network yet, for example), it is retried once Google Home answers again or when you open the flyout.
- **State is live.** The flyout reads every device's state in one request when it opens, and again every 10 seconds while it stays open. Offline devices are shown faded.
- **Your sign-in stays on this PC,** in a private browser profile for this app only: `%LOCALAPPDATA%\HomeControl\WebView2`. **Sign out** clears it. While you're signed out, devices from Google Home are switched through Google Assistant if it is set up.

The catch: this is the private interface of Google's own website, not an API Google offers to other apps. It can stop working whenever Google changes the site. Google may also refuse to sign in inside an embedded browser ("This browser or app may not be secure"). Use it for your own home only. If it breaks, Google Assistant (below) can take over.

### Google Assistant (optional fallback)

The **Google Assistant API** (`embeddedassistant.googleapis.com`) is an official API that Windows apps can use. Home Control sends it the same text commands you would say to a speaker, such as "turn on Kitchen light", and reads the answers to work out device state. Home Assistant's *Google Assistant SDK* integration works the same way.

When Assistant is set up, Home Control:

- **switches a Google Home device through Assistant, by name,** when the web session fails (this can be turned off);
- **lets you add devices or scenes by name,** for example `activate movie night`.

Google is replacing Google Assistant with Gemini on phones. The cloud Assistant API used here still works.

## Setting up

### Google Home (about 1 minute)

Open **Settings › Account** and click **Sign in** under *Google Home*, or click **Sign in** in the flyout. The window opens Google's sign-in page; sign in with the Google account that has your home. Back on home.google.com, Home Control loads your devices and the window says how many it found; then you can close it. If Google doesn't accept the session (for example after a password change), the window asks you to sign in again. Use **Settings › Devices** to hide devices from the tray, rename them, change their icons and add shortcuts. Shortcuts keep working for devices hidden from the tray.

If several Google accounts are signed in and the wrong home shows up, switch accounts on the page in the sign-in window, or set **Account index** under *Advanced* (`0` is the first account, `1` the second, …). If anything goes wrong, `%LOCALAPPDATA%\HomeControl\home-control.log` lists the pages the sign-in window loaded.

### Google Assistant (optional, about 5 minutes)

Google requires your own free OAuth client for the Assistant API. The Account page walks you through these steps, with links:

1. [Create a Google Cloud project](https://console.cloud.google.com/projectcreate).
2. Enable the [Google Assistant API](https://console.cloud.google.com/apis/library/embeddedassistant.googleapis.com) in it.
3. Configure the [OAuth consent screen](https://console.cloud.google.com/auth/overview). Choose audience *External* and add your Google account as a *test user*.
4. [Create an OAuth client](https://console.cloud.google.com/auth/clients) of type **Desktop app** and download its JSON file.
5. In Home Control open **Settings › Account**, click **Import JSON…**, then **Sign in with Google**.

Sign-in uses the standard installed-app OAuth flow: your browser, a loopback redirect to `127.0.0.1`, and PKCE. The refresh token and client secret are stored encrypted with Windows DPAPI in `%LOCALAPPDATA%\HomeControl\secrets.dat`.

> While the consent screen is in *Testing* mode, Google expires sign-ins after 7 days. To avoid that, set the app to *In production* on the consent screen; for your own account you can click through the "unverified app" warning. If commands don't work, turn on **Personal results** for your account in the Google Home app.

## Building

Requirements: Windows 10 1809 or later (Mica needs Windows 11), the .NET 10 SDK, and optionally Visual Studio 2022/2026 with the *WinUI application development* workload. Running it needs the Microsoft Edge WebView2 Runtime, which Windows 11 and up-to-date Windows 10 already include.

```powershell
# run from source
dotnet build src/HomeControl.App -p:Platform=x64
.\src\HomeControl.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\HomeControl.exe

# self-contained build you can copy anywhere (no .NET or Windows App SDK install needed)
dotnet publish src/HomeControl.App -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish
```

Use `-p:Platform=ARM64 -r win-arm64` for ARM devices. Every push also builds both architectures on GitHub Actions and attaches the published app to the run as an artifact. CI also launches the x64 build with `--smoke-test`. It opens the flyout and every settings page in light and dark theme, plus the Google sign-in window. It runs a script in the hidden Google Home page and checks that a sync without a sign-in fails cleanly. It fails on any runtime error and uploads screenshots (the *Screenshots* artifact).

The core library is cross-platform and has unit tests. It holds the Google Home protocol and device sync, the Assistant client, Google sign-in, settings and shortcuts.

```bash
dotnet test tests/HomeControl.Core.Tests
```

## Using it

| | |
|---|---|
| Left-click tray icon | Open or close the device flyout (Esc or clicking elsewhere closes it) |
| Right-click tray icon | Refresh device states, Turn all off, Sync devices, Settings, Exit |
| Device shortcut | Toggles that device; a notification confirms it (can be turned off) |
| Settings › Devices | Sync from Google Home, show or hide devices in the tray, rename, reorder, record shortcuts, **Try it** buttons; add Assistant devices by name |
| Settings › Account | Google Home sign-in and sync; Google Assistant setup, fallback, language and command phrases |
| Settings › General | Theme, window material, flyout shortcut, notifications, start with Windows |

Starting `HomeControl.exe` a second time opens the flyout of the running instance. With `--background` (used by *Start with Windows*) it starts silently in the tray.

### Assistant: other languages and special devices

Assistant commands are built from templates (`turn on {name}`, `turn off {name}`, `is {name} on?`). Answers are interpreted with regular expressions. All of these can be changed under **Settings › Account › Language and commands**, so Home Control works with any Assistant language. Each device can also override its phrases, for example `activate movie night` for a scene.

Settings live in `%LOCALAPPDATA%\HomeControl\settings.json`, next to a log file (`home-control.log`).

## Project layout

```
src/HomeControl.Core         Cross-platform logic: Google Home protocol, device sync and routing,
                             OAuth (loopback + PKCE), Assistant gRPC client, settings and secret
                             store, shortcut model
src/HomeControl.App          WinUI 3 app: Win32 tray icon and hotkeys, flyout, settings window,
                             the WebView2 Google Home session and sign-in window
tests/HomeControl.Core.Tests xUnit tests, including sample Google Home responses and an
                             in-process fake Assistant gRPC server
tools/generate_assets.py     Regenerates the icons from Fluent UI System Icons
```
