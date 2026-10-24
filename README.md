# Home Control

A small WinUI 3 tray app for switching your Google Home devices on and off.

- **Left-click** the tray icon to open a flyout with a toggle for every device. Under each name it shows the device's room, plus its home if you have more than one. The flyout opens next to the taskbar, on whichever edge the taskbar is.
- **Right-click** for Refresh, Turn all off, Settings and Exit.
- **Global shortcuts.** Give each device its own shortcut (for example `Ctrl + Alt + 1`), plus one that opens the flyout.
- **Follows the Windows theme.** The tray icon matches the taskbar (white on a dark taskbar, black on a light one). The flyout and settings window follow the app light/dark mode and switch live when you change it.
- **Mica.** Both windows use Mica by default. Mica Alt, Acrylic or a plain background can be picked in Settings › General.
- **Scenes.** Group devices under one toggle in the tray (Settings › Scenes), with an optional shortcut.
- **Automations.** A node-based editor (Settings › Automations) switches devices on their own: at a time of day or at sunrise, sunset and twilight for your location, when the PC is locked, wakes up or sits idle, when Windows shuts down, or when you clap.
- **Clap to switch.** Optionally, clap two, three or four times in a row to run an automation, for example to toggle a lamp. Nothing you say or play is kept (see [Clap listening](#clap-listening)).
- Optional notification after a shortcut toggles a device, and an option to start with Windows.

## How it talks to Google Home

Google's [Home APIs](https://developers.home.google.com/apis) only ship SDKs for Android and iOS. Its [Home MCP server](https://developers.home.google.com/) needs a US Google Home Premium Advanced subscription. So Home Control uses two other routes:

### Google Home on the web (default)

Home Control signs in to [home.google.com](https://home.google.com) once, in a small browser window inside the app (Microsoft Edge WebView2). After that it keeps that page loaded in a hidden browser and sends the same requests the web app sends. Those requests go to Google's `googlehomefoyer-pa` API and are authorized from the page's own session.

- **Devices are listed automatically.** Every device that can be turned on and off (lights, plugs, switches, TVs and so on) is added, with its room. Devices added later in Google Home appear at the next start or after **Sync now**. If a sync fails (no network yet, for example), it is retried once Google Home answers again or when you open the flyout.
- **State is live.** The flyout reads every device's state in one request when it opens, and again every 10 seconds while it stays open. Offline devices are shown faded. Some devices, such as TVs, take a while to act on a command. Until such a device reports its new state, its toggle stays greyed out in the new position, but you can still click it to send another command. Home Control checks again for up to a minute, then shows what the device reports. Google's on/off value counts even when Google lists a device as offline, because its cloud does that for some TVs that are on. Such a device shows *Offline* next to its state; hover over it for what that means. (The Google Home phone app may also see TVs over your Wi-Fi, which Home Control and home.google.com can't, so it can show a TV as online when Google's cloud lists it as offline.) A device Google can't ask whether it is on (its integration sets `commandOnlyOnOff`) is shown as switched as soon as Google takes the command, without the wait. Scenes count a device Google lists as offline by its on/off value, as its row shows it. If Google returns several records for one device, they are combined by the rule of Google's Home APIs: online if any record is, offline if all are, otherwise unknown. The log (`home-control.log`) records what Google's device list says about each TV and Google's raw answer for TVs and for devices that are offline or report an error, each time it changes. It also notes commands a device didn't confirm.
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

## Scenes

A scene groups devices under one toggle. Turning it on turns every device in it on, and turning it off turns them all off. It shows as on while any of its devices is on, as a room does in Google Home. Create scenes under **Settings › Scenes**: pick the devices, an icon and an optional shortcut. Choose whether the scene shows in the tray flyout, where it appears above the devices. **Move up/down** sets the order there. Each device in a scene switches through its own row, so a slow device (a TV, say) waits for confirmation in the same way.

## Automations

Each automation is a small graph. **Triggers** (when) start it, **conditions** (if) choose a path through their *Yes* and *No* outputs, and **actions** (then) do the work. **New automation** starts an empty one. Add nodes with the editor's **Trigger**, **Condition** and **Action** buttons, or right-click the canvas. Wire nodes together by dragging from the dot on the right of one node to the dot on the left of the next. You can also drop a wire on an empty spot to add a connected node there. The editor has undo, zoom, and **Run now**, which runs the automation from a trigger and skips waits. Problems such as a missing device, a node that isn't connected, or sun times without a location are flagged on the nodes.

| Node | Kind | What it does |
|---|---|---|
| **Time of day** | Trigger | A set time, or sunrise, sunset, dawn/dusk (civil twilight), nautical or astronomical twilight, or solar noon, with an offset (for example 15 min before sunset), on chosen days |
| **PC event** | Trigger | The PC is locked or unlocked, goes to sleep or wakes up, has been idle for N minutes or you're back, the display turns off or on, or it switches to battery or is plugged in; also when Home Control starts |
| **PC shutdown** | Trigger | Windows shuts down or restarts, or you sign out |
| **Claps** | Trigger | You clap 2, 3 or 4 times in a row, while clap listening is on (see below) |
| **Time window** | Condition | Between two times of day, which can be sun times and can wrap past midnight (for example sunset to sunrise) |
| **Day of the week** | Condition | Only on some days |
| **Device state** | Condition | A device is on or off |
| **PC state** | Condition | Locked or unlocked, idle or in use, on battery or plugged in, display on or off |
| **Device** | Action | Turn a device on or off, or toggle it |
| **Everything off** | Action | Turn every device in the tray off |
| **Wait** | Action | Wait before the next step |
| **Notification** | Action | Show a Windows notification |
| **PC power** | Action | Lock, sleep, turn the display off, or shut down or restart after a one-minute warning (cancel it with **Cancel shutdown** in the tray menu). Apps with unsaved work can still ask before they close, as with a normal shutdown. |

**Location.** Sun times are calculated on the PC (NOAA's equations, accurate to about a minute) for the location set on the Automations page. You can find a town by name, use Windows' location (if Location is turned on for desktop apps), or type coordinates. Searching sends the name you type to the free [Open-Meteo geocoding API](https://open-meteo.com/en/docs/geocoding-api); the location you pick is only stored on this PC. Where the sun doesn't rise or set on a day (polar day or night), those triggers don't fire that day.

**At shutdown**, Home Control asks Windows to notify it early. While the shutdown automations run, Windows shows *“Home Control is switching devices before Windows shuts down”* for a few seconds; waits are skipped and the PC power action is ignored. *Goes to sleep* automations get about a second and a half, also without waits or retries; what hasn't finished by then is dropped rather than done after waking. *Wakes up* means someone woke the PC: wakes for updates or timers that go back to sleep unseen don't count. Actions that fail because the network isn't back yet after waking up are retried twice.

**Polar day and night.** Where the sun doesn't set or rise on a day, a window such as *sunset to sunrise* follows where the sun actually is.

**Missed times.** Time triggers fire while Home Control runs. One that was missed by more than two minutes, because the PC was asleep or the app wasn't running, is skipped rather than run late. Starting an automation again while it is still running (for example inside a *Wait*) restarts it; turning it off or deleting it stops it. Automations are saved in `%LOCALAPPDATA%\HomeControl\automations.json`, and failures show a notification.

## Clap listening

Clap two, three or four times in a row to run an automation that starts with a **Claps** trigger, for example *Claps (2) → Device: toggle Lamp*. Turn it on under **Settings › General › Listen for claps**. It is off until you do.

- **When the microphone is open.** Only while clap listening is on *and* an enabled automation has a Claps trigger wired to something. It closes while the PC sleeps and during Remote Desktop sessions (where the microphone would be the remote one), and optionally while the PC is locked. The settings page and the Claps node say whether it is listening and when it last heard you. Windows shows its microphone icon while it is open. The tray menu has **Stop listening for claps**. While the microphone is open, Windows usually counts it as audio in use and won't go to sleep on its own; turn on *Pause while the PC is locked* (the PC then sleeps as usual once it locks) or turn listening off if that matters.
- **Nothing is kept.** Sound is analysed as it arrives and then forgotten. Each sample goes through a few filters into three running sums, and every 10 ms these become three loudness figures (2–7 kHz, 500 Hz–1.2 kHz and below 2 kHz). The detector reads the sound where Windows delivers it, without copying it out; a microphone with several channels is averaged a few milliseconds at a time in a small buffer that is wiped straight after. It remembers only a few numbers: background levels, the last two loudness figures, filter state and the timing of the current claps, and these are wiped when listening stops. The settings page lists the last few sounds it judged with a few loudness figures each (see below); that list is only in memory and is cleared when listening stops. The clap event log (see below) saves the detector's own figures, never sound. No sound is ever recorded, written to disk, logged or sent anywhere, and the microphone isn't connected to anything else in the app. What the PC plays on its speakers is analysed the same way (see below) and isn't kept either. The log notes only when listening starts or stops (with the microphone's name), problems such as the microphone being blocked, and how many claps were heard. Windows' audio engine keeps its own fraction-of-a-second capture buffer, as for any app that uses the microphone. The unit tests check that the detector has nowhere to keep sound (no arrays, lists or buffers), that the clap event log is only ever handed single figures, that resetting the detector leaves nothing from the sound, and that it allocates nothing while it listens.
- **What counts as a clap.** A sharp sound that dies away like a clap in a room, from bright claps to dull, cupped ones, with a body: hands meeting sound from about 500 Hz up, while clicks, taps and a machine's ticks are only high-pitched. Speech and music last too long, clicks next to the microphone stop dead, and knocks and slams are too low-pitched. So the ticks and whirs of a 3D printer don't count, and faint ones don't interrupt your claps either. Claps in a row must be 0.2–0.5 s apart with a steady rhythm. A sequence starts only after a short quiet moment (or straight after a finished one, so you can repeat a pattern; and if a try was spoiled, the next one may follow straight away), and not in the second after other sharp sounds nearly as loud as your claps (typing, tidying up, things put down): deliberate claps stand out. Any other sound that stands out cancels it, though music or talking far quieter than your claps doesn't. It is reported once it is clearly over, about half a second after the last clap, so three claps never also count as two. **Sensitivity** (Low, Medium or High) trades quiet or distant claps against false alarms. Finger snaps, and claps from a TV or another device, sound like claps, so they can trigger it too. Loud sound around 500 Hz–1.2 kHz in the room (music, a TV, a motor's whine) can hide the body of very bright claps; hollow, cupped claps are heard best. The thresholds were checked on real recordings of claps next to a running 3D printer, and on those recordings made quieter, more echoey or noisier, as a microphone further away hears them, and then on clap events marked right, wrong or missed (see below; the recordings and events were used only to measure; they are not part of the app or the repository).
- **What it hears.** *Settings › General › Listen for claps › What it hears* lists the last few sounds it judged and why each did or didn't count (for example "not a clap: no lower body (a click, a machine's tick) · high +27 dB, body +5 dB, …"), and **Copy** copies the list. Use it to see why your claps don't count, or what set it off.
- **Clap events: mark what it got wrong.** When it hears claps, and when you tell it it missed some, it saves a clap event: when, what happened, the settings, and what the detector worked out over the half minute before. Mark each one under *Settings › General › Listen for claps › Clap events*: your claps (it was right), not claps (it was wrong; add a note such as "3D printer" or "door"), or claps it missed. The tray menu has **Those last claps weren't mine** and **I clapped and it didn't hear** for the moment it happens (the second saves the last half minute). **Export…** writes them all into a zip to send, so the detection can be improved on real cases: the `tools/ClapReplay` program runs exported events through the current detector and tells which wrong ones a change fixes and which right ones it would lose. An event holds figures, no sound: every sound the detector judged, with its verdict and loudness figures; for those it judged as possible claps (not speech or music, which keep just their verdict), the loudness of its three pitch bands every 10 ms from just before the sound until shortly after its verdict, under half a second; and every half second the room's background level and the loudest moment. That is far too little to play anything back or make out what was said. To cut those pieces out, the figures of every 10 ms are held in memory for 0.64 s; the pieces themselves, for the last half minute. Events are kept in `%LOCALAPPDATA%\HomeControl\Clap events` (one small JSON file each): the newest 150 unmarked and up to 400 in all, marked ones the longest. Turn **Save clap events** off to save none; **Delete all** removes them.
- **Sounds from the PC's own speakers don't count.** Home Control also follows what Windows sends to each speaker device (loopback), with the same analysis and nothing kept. When the speakers play a sharp sound, a clap that the microphone hears in the following fraction of a second (up to 0.2 s, 0.3 s for HDMI or DisplayPort monitors, 0.5 s for Bluetooth) is taken for theirs, if it rises no more sharply than theirs did: the way through the room can soften a sound but not sharpen it. So a clap in a video counts as the video's, while your own claps over music still count, as long as they stand out more than its beat. Each speaker channel is followed on its own, so a sound from one speaker isn't softened by the others. When the speakers play sounds that start out of silence (clean speech in a call or a podcast, a notification), a clap of yours in the same fraction of a second is still taken for theirs. If the microphone can't be used raw, Windows' noise suppression may sharpen what it lets through, so the speakers get more benefit of the doubt. Headphones and headsets aren't followed, since the room can't hear them. If another app has a speaker to itself (exclusive mode), Windows can't show what it plays; claps still count, and the settings page says that sounds from it may be taken for claps.
- **Microphone.** The Windows default recording device, or one you pick. Windows' voice processing (noise suppression, automatic gain) is bypassed where the device allows it, because it flattens claps. If Windows blocks microphone access for desktop apps (*Settings › Privacy & security › Microphone › Let desktop apps access your microphone*), the settings page says so and links there.

## Building

### Without typing anything

In the Home Control folder, double-click **Build Home Control.cmd**. A window opens that:

- checks for the .NET 10 SDK. If it's missing, **Set it up** installs it for your account (about 300 MB from Microsoft, no administrator rights needed);
- builds the app for your PC's processor (x64 or ARM64);
- puts it in `%LOCALAPPDATA%\Programs\Home Control`, or a folder you choose. It adds Home Control to the Start menu and starts it.

Running it again later updates the app in place. If Home Control is running, the builder closes it first and starts the new one. Your settings and sign-in are kept. The first build downloads a few hundred MB of packages and takes several minutes; later builds are quicker. If Windows asks whether to run the file because it was downloaded, choose **Run** (or **More info › Run anyway**). The build output is saved in `%LOCALAPPDATA%\HomeControl\build.log`.

### From the command line

Requirements: Windows 10 1809 or later (Mica needs Windows 11), the .NET 10 SDK, and optionally Visual Studio 2022/2026 with the *WinUI application development* workload. Running it needs the Microsoft Edge WebView2 Runtime, which Windows 11 and up-to-date Windows 10 already include.

Run these from the repository root, the folder that contains `HomeControl.sln` (not from `src`):

```powershell
# run from source
dotnet build src/HomeControl.App -p:Platform=x64
.\src\HomeControl.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\HomeControl.exe

# self-contained build you can copy anywhere (no .NET or Windows App SDK install needed)
dotnet publish src/HomeControl.App -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish
.\publish\HomeControl.exe
```

Use `-p:Platform=ARM64 -r win-arm64` for ARM devices. Every push also builds both architectures on GitHub Actions and attaches the published app to the run as an artifact. CI also launches the x64 build with `--smoke-test`. It opens the flyout and every settings page in light and dark theme, the automation editor with every kind of node, and the Google sign-in window. It runs a script in the hidden Google Home page and checks that a sync without a sign-in fails cleanly. It fails on any runtime error and uploads screenshots (the *Screenshots* artifact). CI also runs the builder twice. Once it builds without its window. Once it builds through its window, which adds the Start menu shortcut and starts the app. A rebuild then has to close the running app, and `--exit` is checked.

The core library is cross-platform and has unit tests. It holds the Google Home protocol and device sync, the Assistant client, Google sign-in, settings and shortcuts.

```bash
dotnet test tests/HomeControl.Core.Tests
```

## Using it

| | |
|---|---|
| Left-click tray icon | Open or close the device flyout (Esc or clicking elsewhere closes it) |
| Right-click tray icon | Refresh device states, Turn all off, Sync devices, stop or start listening for claps (when an automation uses claps), mark the last claps as not yours or say it missed yours (clap events), Settings, Exit |
| Device shortcut | Toggles that device; a notification confirms it (can be turned off) |
| Settings › Devices | Devices grouped by home and room. Groups collapse, and Home Control remembers which. Sync from Google Home, show or hide devices in the tray, rename, record shortcuts, **Try it** buttons; add Assistant devices by name. **Move up/down** sets the order within a room, which is also the order in the flyout. |
| Settings › Scenes | Groups of devices with one toggle: devices, icon, shortcut, shown in the tray or not |
| Settings › Automations | Location, automation list and the node editor |
| Settings › Account | Google Home sign-in and sync; Google Assistant setup, fallback, language and command phrases |
| Settings › General | Theme, window material, flyout shortcut, notifications, start with Windows, clap listening and clap events |

Starting `HomeControl.exe` a second time opens the flyout of the running instance. With `--background` (used by *Start with Windows*) it starts silently in the tray. `HomeControl.exe --exit` closes the running instance (the builder uses this before it replaces the app's files).

### Assistant: other languages and special devices

Assistant commands are built from templates (`turn on {name}`, `turn off {name}`, `is {name} on?`). Answers are interpreted with regular expressions. All of these can be changed under **Settings › Account › Language and commands**, so Home Control works with any Assistant language. Each device can also override its phrases, for example `activate movie night` for a scene.

Settings live in `%LOCALAPPDATA%\HomeControl\settings.json`, next to a log file (`home-control.log`) and the clap events (figures only). No audio is ever stored there or anywhere else.

## Project layout

```
src/HomeControl.Core         Cross-platform logic: automations (node graph, sun times, scheduling,
                             runner), clap detector, Google Home protocol, device sync and routing,
                             OAuth (loopback + PKCE), Assistant gRPC client, settings and secret
                             store, shortcut model
src/HomeControl.App          WinUI 3 app: Win32 tray icon and hotkeys, flyout, settings window,
                             node editor, PC state/shutdown monitor, microphone capture for
                             claps (NAudio, WASAPI), the WebView2 Google Home session and
                             sign-in window
tests/HomeControl.Core.Tests xUnit tests, including sun times checked against an independent
                             implementation, sample Google Home responses, an in-process
                             fake Assistant gRPC server and synthesized claps, speech, music,
                             knocks and typing for the clap detector (made in memory, no recordings)
tools/builder                The window behind "Build Home Control.cmd" (Windows PowerShell + WPF)
tools/ClapReplay             Runs exported clap events through the clap detector as it is now:
                             dotnet run --project tools/ClapReplay -- events.zip [--sounds]
tools/generate_assets.py     Regenerates the icons from Fluent UI System Icons
```
