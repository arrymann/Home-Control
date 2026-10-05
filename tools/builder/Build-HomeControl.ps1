<#
.SYNOPSIS
    Builds Home Control in a window, with nothing to type.

.DESCRIPTION
    Double-click "Build Home Control.cmd" in the repository folder to run this. It finds the
    .NET SDK (or sets it up for your account), builds the app for this PC with "dotnet publish",
    puts it in a folder of your choice, adds it to the Start menu and starts it.

    -NoGui builds without the window and prints the output instead. -SmokeTest drives the
    window on its own and saves screenshots (both are used by CI).

.EXAMPLE
    .\Build-HomeControl.ps1 -NoGui -Platform x64 -Destination C:\Apps\HomeControl -NoStart
#>
[CmdletBinding()]
param(
    # Build without the window, printing the output.
    [switch] $NoGui,

    # x64 or ARM64. The default is this PC's processor.
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform,

    # The folder for HomeControl.exe. The default is %LOCALAPPDATA%\Programs\Home Control.
    [string] $Destination,

    # Don't add a Start menu shortcut.
    [switch] $NoShortcut,

    # Don't start the app when it's built.
    [switch] $NoStart,

    # CI: build in the window, save screenshots and builder-result.txt in this folder, then close.
    [string] $SmokeTest,

    # Internal: this copy was started without a console window.
    [switch] $Detached
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$ProjectPath = Join-Path $RepoRoot 'src\HomeControl.App\HomeControl.App.csproj'
$AppIconPath = Join-Path $RepoRoot 'src\HomeControl.App\Assets\AppIcon.ico'
$DataFolder = Join-Path $env:LOCALAPPDATA 'HomeControl'
$LogPath = Join-Path $DataFolder 'build.log'
$SettingsPath = Join-Path $DataFolder 'builder.json'
$DefaultDestination = Join-Path $env:LOCALAPPDATA 'Programs\Home Control'
$ShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Home Control.lnk'
$UserSdkFolder = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' # where dotnet-install.ps1 puts it by default
$SdkInstallScriptUrl = 'https://dot.net/v1/dotnet-install.ps1'
$SdkDownloadPage = 'https://dotnet.microsoft.com/download/dotnet/10.0'
$RequiredSdkMajor = 10
$Interactive = -not $NoGui -and -not $SmokeTest
$Ellipsis = [string][char]0x2026

# Quotes a path for a Windows command line (paths can't contain quotes; a trailing backslash
# would escape the closing quote, so it's doubled).
function ConvertTo-Argument([string] $Value) {
    '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}

# ------------------------------------------------------------------ start without a console

# Started from "Build Home Control.cmd": start again with the console hidden, so only the
# builder's window shows, and let the console close.
if ($Interactive -and -not $Detached) {
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File {0} -Detached' -f (ConvertTo-Argument $PSCommandPath)
    if ($Platform) { $arguments += " -Platform $Platform" }
    if ($Destination) { $arguments += ' -Destination ' + (ConvertTo-Argument $Destination) }
    if ($NoShortcut) { $arguments += ' -NoShortcut' }
    if ($NoStart) { $arguments += ' -NoStart' }
    Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments -WindowStyle Hidden
    exit 0
}

# ------------------------------------------------------------------ native helpers

# Written for the C# 5 compiler that Windows PowerShell 5.1 uses.
$NativeCode = @'
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace HomeControlBuilder
{
    public sealed class RunResult
    {
        public int ExitCode;
        public bool TimedOut;
        public string Output;
    }

    // Runs a console program without a window. Its output is collected on other threads into a
    // queue that PowerShell reads on the UI thread (PowerShell code can't run on those threads).
    public sealed class ProcessRunner
    {
        private readonly Process _process = new Process();
        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        private readonly ManualResetEvent _outputClosed = new ManualResetEvent(false);
        private readonly ManualResetEvent _errorClosed = new ManualResetEvent(false);

        public ProcessRunner(string fileName, string arguments, string workingDirectory, IDictionary environment)
        {
            ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            // What a console window would show: console programs write in the OEM code page.
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding((int)GetOEMCP());
            }
            catch (Exception)
            {
                encoding = Encoding.UTF8; // PowerShell 7 has no OEM code pages by default
            }

            info.StandardOutputEncoding = encoding;
            info.StandardErrorEncoding = encoding;

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                info.WorkingDirectory = workingDirectory;
            }

            if (environment != null)
            {
                foreach (DictionaryEntry entry in environment)
                {
                    info.EnvironmentVariables[Convert.ToString(entry.Key)] = Convert.ToString(entry.Value);
                }
            }

            _process.StartInfo = info;
            _process.OutputDataReceived += (sender, e) => OnLine(e.Data, _outputClosed);
            _process.ErrorDataReceived += (sender, e) => OnLine(e.Data, _errorClosed);
            _process.Start();
            _process.StandardInput.Close(); // nothing may wait for a key press
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public int Id
        {
            get { return _process.Id; }
        }

        public bool HasExited
        {
            get { return _process.HasExited; }
        }

        public string[] TakeLines()
        {
            List<string> lines = new List<string>();
            string line;
            while (_lines.TryDequeue(out line))
            {
                lines.Add(line);
            }

            return lines.ToArray();
        }

        // Once HasExited: waits for the last output and returns the exit code. Not forever: a
        // leftover child process can keep the output open.
        public int Finish()
        {
            _process.WaitForExit(5000);
            _outputClosed.WaitOne(5000);
            _errorClosed.WaitOne(5000);
            return _process.ExitCode;
        }

        // Stops the program and everything it started.
        public void Kill()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + _process.Id + " /T /F");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process taskkill = Process.Start(info))
                {
                    taskkill.WaitForExit(15000);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch (Exception)
            {
            }
        }

        // Runs a quick command to the end.
        public static RunResult Run(string fileName, string arguments, IDictionary environment, int timeoutMilliseconds)
        {
            ProcessRunner runner = new ProcessRunner(fileName, arguments, null, environment);
            RunResult result = new RunResult();
            if (runner._process.WaitForExit(timeoutMilliseconds))
            {
                result.ExitCode = runner.Finish();
            }
            else
            {
                runner.Kill();
                result.TimedOut = true;
                result.ExitCode = -1;
            }

            result.Output = string.Join("\n", runner.TakeLines());
            return result;
        }

        private void OnLine(string line, ManualResetEvent closed)
        {
            if (line == null)
            {
                closed.Set();
            }
            else
            {
                _lines.Enqueue(line);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }

    public static class Native
    {
        private const uint WM_APP = 0x8000;

        // Dark title bar to match the window (attribute 20; 19 before Windows 10 20H1).
        public static void SetDarkTitleBar(IntPtr window, bool dark)
        {
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(window, 20, ref value, 4) != 0)
            {
                DwmSetWindowAttribute(window, 19, ref value, 4);
            }
        }

        // Asks a running Home Control to exit, as "HomeControl.exe --exit" does (MessageWindow.WM_EXIT_APP).
        public static bool AskHomeControlToExit()
        {
            IntPtr window = FindWindowW("HomeControl.TrayHost", null);
            return window != IntPtr.Zero && PostMessageW(window, WM_APP + 3, IntPtr.Zero, IntPtr.Zero);
        }

        public static void CreateShortcut(string path, string target, string workingDirectory, string description)
        {
            IShellLinkW link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workingDirectory);
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(path, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string className, string windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
'@

# ------------------------------------------------------------------ state

# Hashtables, so event handlers (which run in their own scope) can change them.
$Ui = $null
$Sdk = @{ Path = $null; Version = $null; Text = $null; Older = $null }
$Job = @{
    Busy = $false
    Kind = $null        # Build or Sdk
    Stage = 'Idle'      # Closing, Building, Downloading, InstallingSdk
    Runner = $null
    Succeeded = $false
    Platform = $null
    Destination = $null
    Shortcut = $false
    Start = $false
    StartedAt = [DateTime]::UtcNow
    StageText = ''
    Deadline = [DateTime]::UtcNow
    Killed = $false
    Errors = $null
    ErrorCodes = $null
    Log = $null
    Download = $null
    Client = $null
    InstallScript = $null
    Failure = $null
}
$Smoke = @{ Stage = 'Waiting'; At = [DateTime]::UtcNow; Deadline = [DateTime]::UtcNow }

# ------------------------------------------------------------------ environment

function Get-NativePlatform {
    # The registry value is the PC's own processor even in an emulated process.
    $architecture = [Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'PROCESSOR_ARCHITECTURE', $null)
    if (-not $architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    switch ($architecture) {
        'ARM64' { return 'ARM64' }
        'AMD64' { return 'x64' }
    }

    return $null # 32-bit Windows, which Home Control doesn't support
}

function Get-ChildEnvironment([string] $DotNetFolder) {
    $environment = @{
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    }
    if ($DotNetFolder) {
        $environment['DOTNET_ROOT'] = $DotNetFolder
        $environment['PATH'] = $DotNetFolder + ';' + $env:PATH
    }

    return $environment
}

# Looks for a dotnet that has a .NET 10 (or later) SDK.
function Find-DotNetSdk {
    $Sdk.Path = $null
    $Sdk.Version = $null
    $Sdk.Older = $null

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    foreach ($command in @(Get-Command 'dotnet.exe' -CommandType Application -ErrorAction SilentlyContinue)) {
        $candidates.Add($command.Path)
    }
    foreach ($folder in @($env:ProgramW6432, $env:ProgramFiles)) {
        if ($folder) { $candidates.Add((Join-Path $folder 'dotnet\dotnet.exe')) }
    }
    $candidates.Add((Join-Path $UserSdkFolder 'dotnet.exe'))

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $candidates) {
        if (-not $seen.Add($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }

        $result = [HomeControlBuilder.ProcessRunner]::Run($path, '--list-sdks', (Get-ChildEnvironment (Split-Path -Parent $path)), 30000)
        foreach ($line in ($result.Output -split "`n")) {
            # "10.0.100 [C:\Program Files\dotnet\sdk]" or "10.0.100-rc.2.25502.107 [...]"
            if ($line -notmatch '^\s*(\d+)\.(\d+)\.(\d+)(\S*)\s') { continue }

            $version = [version]('{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3])
            $text = $Matches[1] + '.' + $Matches[2] + '.' + $Matches[3] + $Matches[4]
            if ($version.Major -ge $RequiredSdkMajor) {
                if ($null -eq $Sdk.Version -or $version -gt $Sdk.Version) {
                    $Sdk.Path = $path
                    $Sdk.Version = $version
                    $Sdk.Text = $text
                }
            }
            elseif ($null -eq $Sdk.Older -or $version -gt [version]$Sdk.Older) {
                $Sdk.Older = $version.ToString()
            }
        }

        if ($Sdk.Path) { return }
    }
}

# ------------------------------------------------------------------ settings

function Read-BuilderSettings {
    $settings = @{ Destination = $null; Shortcut = $true; Start = $true }
    try {
        if (Test-Path -LiteralPath $SettingsPath) {
            $saved = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
            foreach ($name in @('Destination', 'Shortcut', 'Start')) {
                $property = $saved.PSObject.Properties[$name]
                if ($property -and $null -ne $property.Value) { $settings[$name] = $property.Value }
            }
        }
    }
    catch {
        # A damaged file just means the defaults.
    }

    return $settings
}

# Remembers the window's choices for next time (runs without the window don't change them).
function Save-BuilderSettings {
    if (-not $Ui) { return }
    try {
        [void](New-Item -ItemType Directory -Force -Path $DataFolder)
        [pscustomobject]@{ Destination = $Job.Destination; Shortcut = $Job.Shortcut; Start = $Job.Start } |
            ConvertTo-Json | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
    }
    catch {
        Write-JobLine "Couldn't save the builder's settings: $($_.Exception.Message)"
    }
}

# ------------------------------------------------------------------ messages and progress

function Write-JobLine([string] $Line) {
    if ($Job.Log) {
        try { $Job.Log.WriteLine($Line) } catch { }
    }
    if ($Ui) {
        [void]$Ui.Pending.AppendLine($Line)
    }
    elseif ($NoGui) {
        Write-Host $Line
    }
}

# Shows what's happening. Kind: Busy, Success, Error or Info.
function Set-Status([string] $Title, [string] $Text, [string] $Kind) {
    if ($NoGui) {
        Write-Host ''
        Write-Host "== $Title"
        if ($Text) { Write-Host $Text }
        return
    }
    if (-not $Ui) { return }

    $glyph, $color = switch ($Kind) {
        'Success' { [char]0xE930, $Theme.Success; break }
        'Error' { [char]0xEA39, $Theme.Error; break }
        'Info' { [char]0xE946, $Theme.Accent; break }
        default { [char]0xE895, $Theme.Accent }
    }
    $Ui.StatusIcon.Text = [string]$glyph
    $Ui.StatusIcon.Foreground = New-Brush $color
    $Ui.StatusTitle.Text = $Title
    $Ui.StatusText.Text = $Text
    $Ui.StatusText.Visibility = if ($Text) { 'Visible' } else { 'Collapsed' }
    $Ui.StatusCard.Visibility = 'Visible'
    Show-InView $Ui.StatusCard
}

# Scrolls an element into view (on small screens the window scrolls).
function Show-InView($Element) {
    $Ui.Root.UpdateLayout()
    $Element.BringIntoView()
}

# A message box over the builder's window (Windows Forms' one has the current Windows look).
function Show-Dialog([string] $Text, [string] $Buttons, [string] $Icon) {
    $owner = New-Object System.Windows.Forms.NativeWindow
    if ($Ui -and $Ui.Handle -ne [IntPtr]::Zero) { $owner.AssignHandle($Ui.Handle) }
    try {
        return [System.Windows.Forms.MessageBox]::Show($owner, $Text, 'Build Home Control', $Buttons, $Icon)
    }
    finally {
        if ($owner.Handle -ne [IntPtr]::Zero) { $owner.ReleaseHandle() }
    }
}

# Asks the user; without the window (-NoGui, -SmokeTest) the answer is $Unattended.
function Confirm-Action([string] $Message, [bool] $Unattended) {
    if (-not $Interactive) { return $Unattended }
    return (Show-Dialog $Message 'YesNo' 'Question') -eq 'Yes'
}

function Show-Message([string] $Message) {
    if ($Interactive) {
        [void](Show-Dialog $Message 'OK' 'Information')
    }
    elseif ($NoGui) {
        Write-Host $Message
    }
}

# Keeps the window usable while a job runs, and the buttons in step with it.
function Update-Buttons {
    if (-not $Ui) { return }
    $busy = $Job.Busy
    $canBuild = [bool]$Sdk.Path -and $null -ne $NativePlatform
    $Ui.OptionsCard.IsEnabled = -not $busy
    $Ui.InstallSdkButton.IsEnabled = -not $busy
    $Ui.BuildButton.Visibility = if ($busy) { 'Collapsed' } else { 'Visible' }
    $Ui.BuildButton.IsEnabled = $canBuild
    $Ui.CloseButton.Content = if ($busy) { 'Stop' } else { 'Close' }
    $Ui.Progress.Visibility = if ($busy) { 'Visible' } else { 'Collapsed' }
    $done = -not $busy -and $Job.Kind -eq 'Build' -and $Job.Succeeded
    $Ui.OpenFolderButton.Visibility = if ($done) { 'Visible' } else { 'Collapsed' }
    $Ui.RunButton.Visibility = if ($done) { 'Visible' } else { 'Collapsed' }
    if ($done) { $Ui.BuildButton.Content = 'Build again' }
}

function Update-SdkCard {
    if (-not $Ui) { return }
    $Ui.InstallSdkButton.Visibility = 'Collapsed'
    $Ui.SdkLinkText.Visibility = 'Collapsed'
    if ($null -eq $NativePlatform) {
        $Ui.SdkIcon.Text = [string][char]0xEA39
        $Ui.SdkIcon.Foreground = New-Brush $Theme.Error
        $Ui.SdkTitle.Text = 'This PC runs 32-bit Windows'
        $Ui.SdkText.Text = 'Home Control needs 64-bit Windows 10 or 11 (x64 or ARM64).'
    }
    elseif ($Sdk.Path) {
        $Ui.SdkIcon.Text = [string][char]0xE930
        $Ui.SdkIcon.Foreground = New-Brush $Theme.Success
        $Ui.SdkTitle.Text = ".NET SDK $($Sdk.Text) is ready"
        $Ui.SdkText.Text = "Microsoft's toolkit that turns the code into the app. Found in $(Split-Path -Parent $Sdk.Path)."
    }
    else {
        $Ui.SdkIcon.Text = [string][char]0xE7BA
        $Ui.SdkIcon.Foreground = New-Brush $Theme.Warning
        $Ui.SdkTitle.Text = if ($Sdk.Older) { "A newer .NET SDK is needed (this PC has $($Sdk.Older))" } else { 'The .NET 10 SDK is needed' }
        $Ui.SdkText.Text = "It's Microsoft's free toolkit that turns the code into the app. " +
            '"Set it up" downloads about 300 MB and installs it just for your account, without administrator rights.'
        $Ui.InstallSdkButton.Visibility = 'Visible'
        $Ui.SdkLinkText.Visibility = 'Visible'
    }
    Update-Buttons
}

# ------------------------------------------------------------------ building

function Get-SelectedPlatform {
    if ($Ui) {
        if ($Ui.Arm64Option.IsChecked) { return 'ARM64' }
        return 'x64'
    }
    if ($Platform) { return $Platform }
    return $NativePlatform
}

# The folder for HomeControl.exe, or $null when the user cancelled. Throws with a message for
# folders that can't be used.
function Resolve-Destination([string] $Path) {
    $Path = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"').Trim())
    if (-not $Path) { throw 'Choose a folder for Home Control.' }
    if ($Path -notmatch '^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)') {
        throw "Enter a full folder path, such as $DefaultDestination."
    }

    try {
        $full = [IO.Path]::GetFullPath($Path)
    }
    catch {
        throw "$Path isn't a folder path that Windows accepts."
    }
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    if (Test-Path -LiteralPath $full -PathType Leaf) { throw "$full is a file, not a folder." }

    # Don't fill a folder that holds other things (Desktop, Documents, a drive) with the app's files.
    if ((Test-Path -LiteralPath $full -PathType Container) -and
        -not (Test-Path -LiteralPath (Join-Path $full 'HomeControl.exe')) -and
        @(Get-ChildItem -LiteralPath $full -Force | Select-Object -First 1).Count -gt 0) {
        $inside = Join-Path $full 'Home Control'
        $question = "$full already has other files in it. Put Home Control in a new folder inside it instead?`n`n$inside"
        if (-not (Confirm-Action $question $false)) {
            if ($Interactive) { return $null }
            throw "$full isn't empty."
        }

        $full = $inside
        if ((Test-Path -LiteralPath $full -PathType Container) -and
            -not (Test-Path -LiteralPath (Join-Path $full 'HomeControl.exe')) -and
            @(Get-ChildItem -LiteralPath $full -Force | Select-Object -First 1).Count -gt 0) {
            throw "$full isn't empty either. Choose an empty folder."
        }
    }

    # Can we write there?
    try {
        [void](New-Item -ItemType Directory -Force -Path $full)
        $probe = Join-Path $full ('.write-test-' + [guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($probe, '')
        Remove-Item -LiteralPath $probe -Force
    }
    catch {
        throw "Home Control can't be put in $full (Windows says: $($_.Exception.Message)). Choose another folder, such as $DefaultDestination."
    }

    return $full
}

# Wrap calls in @(): a function returns one process as itself, not as an array.
function Get-RunningHomeControl {
    Get-Process -Name 'HomeControl' -ErrorAction SilentlyContinue
}

function Get-ProcessPath($Process) {
    try { return $Process.Path } catch { return $null }
}

function Start-Build {
    if ($Job.Busy) { return }
    if (-not $Sdk.Path) { throw 'The .NET SDK is needed first.' }

    $folder = if ($Ui) { $Ui.DestinationBox.Text } elseif ($Destination) { $Destination } else { $DefaultDestination }
    try {
        $resolved = Resolve-Destination $folder
    }
    catch {
        $message = $_.Exception.Message
        if (-not $Interactive) { throw }
        Show-Message $message
        return
    }
    if ($null -eq $resolved) { return }

    $shortcut = if ($Ui) { [bool]$Ui.ShortcutOption.IsChecked } else { -not $NoShortcut }
    $start = if ($Ui) { [bool]$Ui.StartOption.IsChecked } else { -not $NoStart }

    # Files in use can't be replaced, and a second copy can't start while one runs (it would
    # just open the running one), so close it first.
    $exe = Join-Path $resolved 'HomeControl.exe'
    $running = @(Get-RunningHomeControl)
    $inFolder = @($running | Where-Object { (Get-ProcessPath $_) -eq $exe })
    $mustClose = $inFolder.Count -gt 0 -or ($start -and $running.Count -gt 0)
    if ($mustClose) {
        $question = 'Home Control is running. Close it so the new build can take its place?'
        if ($start) { $question += ' It starts again when the build is done.' }
        if (-not (Confirm-Action $question $true)) { return }
    }

    if ($Ui) {
        $Ui.DestinationBox.Text = $resolved
        $Ui.LogBox.Clear()
    }

    $Job.Kind = 'Build'
    $Job.Platform = Get-SelectedPlatform
    $Job.Destination = $resolved
    $Job.Shortcut = $shortcut
    $Job.Start = $start
    Initialize-Job

    Write-JobLine "Building Home Control for $($Job.Platform) into $resolved"
    Write-JobLine "Source: $RepoRoot"
    Write-JobLine ".NET SDK: $($Sdk.Text) ($($Sdk.Path))"
    if ($mustClose) {
        Set-Status ('Closing Home Control' + $Ellipsis) 'So its files can be replaced.' 'Busy'
        [void][HomeControlBuilder.Native]::AskHomeControlToExit()
        $Job.Stage = 'Closing'
        $Job.Deadline = [DateTime]::UtcNow.AddSeconds(8) # builds without the exit message are closed by force after this
        $Job.Killed = $false
    }
    else {
        Start-Publish
    }
}

# Common start of a build or an SDK set-up.
function Initialize-Job {
    $Job.Busy = $true
    $Job.Succeeded = $false
    $Job.Failure = $null
    $Job.StartedAt = [DateTime]::UtcNow
    $Job.Errors = New-Object System.Collections.Generic.List[string]
    $Job.ErrorCodes = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    try {
        [void](New-Item -ItemType Directory -Force -Path $DataFolder)
        $Job.Log = New-Object IO.StreamWriter($LogPath, $false, (New-Object Text.UTF8Encoding($false)))
        $Job.Log.AutoFlush = $true
        $Job.Log.WriteLine("Home Control builder, $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
    }
    catch {
        $Job.Log = $null
    }
    Update-Buttons
}

function Start-Publish {
    $rid = if ($Job.Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

    # --disable-build-servers: no MSBuild or compiler processes are left running afterwards.
    $arguments = 'publish {0} -c Release -p:Platform={1} -r {2} --self-contained -o {3} --nologo --disable-build-servers' -f (ConvertTo-Argument $ProjectPath), $Job.Platform, $rid, (ConvertTo-Argument $Job.Destination)
    Write-JobLine "> dotnet $arguments"
    $Job.StageText = 'Getting ready' + $Ellipsis
    $Job.Runner = New-Object HomeControlBuilder.ProcessRunner -ArgumentList @(
        $Sdk.Path, $arguments, $RepoRoot, (Get-ChildEnvironment (Split-Path -Parent $Sdk.Path)))
    $Job.Stage = 'Building'
    Set-Status ('Building Home Control' + $Ellipsis) $Job.StageText 'Busy'
}

# Reads the build's output: progress, and errors to explain at the end.
function Read-BuildLine([string] $Line) {
    Write-JobLine $Line

    if ($Line -match '\berror\s+([A-Z]{2,}\d+)\s*:\s*(.+)$') {
        [void]$Job.ErrorCodes.Add($Matches[1])
        $message = ($Matches[1] + ': ' + ($Matches[2] -replace '\s*\[[^\]]+\]\s*$', '')).Trim()
        if (-not $Job.Errors.Contains($message)) { $Job.Errors.Add($message) }
    }

    $stage = $null
    if ($Line -match 'Determining projects to restore') {
        $stage = 'Getting the packages it needs. The first build downloads a few hundred MB, so this can take a while.'
    }
    elseif ($Line -match '^\s*(Restored |All projects are up-to-date)') {
        $stage = 'Compiling' + $Ellipsis
    }
    elseif ($Line -match '^\s*HomeControl\.Core\s+->') {
        $stage = 'Compiling the app' + $Ellipsis
    }
    elseif ($Line -match '^\s*HomeControl\.App\s+->') {
        $stage = 'Copying the app to its folder' + $Ellipsis
    }
    if ($stage -and $stage -ne $Job.StageText) {
        $Job.StageText = $stage
        if ($NoGui) { Write-Host "== $stage" }
    }
}

function Get-FailureHint {
    $codes = $Job.ErrorCodes
    $text = $Job.Errors -join "`n"
    if ($codes.Contains('NETSDK1045') -or $codes.Contains('NETSDK1209')) {
        return 'This .NET SDK is too old for Home Control. Set up the .NET 10 SDK and try again.'
    }
    if ($codes.Contains('NU1301') -or $codes.Contains('NU1101') -or $codes.Contains('NU1102') -or $text -match 'service index') {
        return "The packages it needs couldn't be downloaded. Check the internet connection and try again."
    }
    if ($codes.Contains('MSB3021') -or $codes.Contains('MSB3026') -or $codes.Contains('MSB3027') -or $text -match 'used by another process') {
        return 'A file is in use. Close Home Control (right-click its tray icon, Exit) and try again.'
    }
    if ($text -match 'too long|PathTooLong|MAX_PATH') {
        return 'The folder path is too long for some build tools. Move this folder somewhere shorter, such as C:\HomeControl, and try again.'
    }
    if ($codes.Count -gt 0) {
        return "The code didn't build. If you didn't change it, download it again; the details below say what went wrong."
    }
    return 'The details below say what went wrong.'
}

function Complete-Build([int] $ExitCode) {
    $exe = Join-Path $Job.Destination 'HomeControl.exe'
    if ($ExitCode -ne 0 -or -not (Test-Path -LiteralPath $exe)) {
        $hint = Get-FailureHint
        $errors = @($Job.Errors | Select-Object -First 3) -join "`n"
        Complete-Job $false "The build didn't work" ($hint + $(if ($errors) { "`n`n$errors" } else { '' }))
        if ($Ui) { Show-Details $true }
        return
    }

    $notes = New-Object System.Collections.Generic.List[string]
    $notes.Add("It's in $($Job.Destination).")
    if ($Job.Shortcut) {
        try {
            [HomeControlBuilder.Native]::CreateShortcut($ShortcutPath, $exe, $Job.Destination, 'Turn your Google Home devices on and off from the tray')
            Write-JobLine "Start menu shortcut: $ShortcutPath"
            $notes.Add("You'll find it in the Start menu.")
        }
        catch {
            Write-JobLine "The Start menu shortcut couldn't be added: $($_.Exception.Message)"
            $notes.Add("The Start menu shortcut couldn't be added.")
        }
    }
    if ($Job.Start) {
        try {
            Start-Process -FilePath $exe -WorkingDirectory $Job.Destination
            Write-JobLine 'Started Home Control.'
            $notes.Add("It's running now: its icon is in the notification area at the right end of the taskbar (select ^ if you don't see it).")
        }
        catch {
            Write-JobLine "Home Control didn't start: $($_.Exception.Message)"
            $notes.Add("It didn't start: $($_.Exception.Message)")
        }
    }

    Save-BuilderSettings
    Complete-Job $true 'Home Control is ready' ($notes -join ' ')
}

function Complete-Job([bool] $Succeeded, [string] $Title, [string] $Text) {
    $Job.Busy = $false
    $Job.Stage = 'Idle'
    $Job.Runner = $null
    $Job.Succeeded = $Succeeded
    if (-not $Succeeded) { $Job.Failure = $Title }
    if ($Job.Log) {
        Write-JobLine ''
        Write-JobLine "$Title. $Text"
        try { $Job.Log.Dispose() } catch { }
        $Job.Log = $null
    }
    if ($Ui) {
        Flush-Output
        if (-not $Succeeded) { $Text += "`n`nThe details are also saved in $LogPath." }
    }
    Set-Status $Title $Text $(if ($Succeeded) { 'Success' } else { 'Error' })
    Update-Buttons
}

function Stop-CurrentJob {
    if (-not $Job.Busy) { return }
    if ($Job.Runner) { $Job.Runner.Kill() }
    if ($Job.Client) { try { $Job.Client.CancelAsync() } catch { } }
    Write-JobLine 'Stopped.'
    Complete-Job $false 'Stopped' 'Build again whenever you like.'
}

# ------------------------------------------------------------------ setting up the SDK

function Start-SdkSetup {
    if ($Job.Busy) { return }
    $question = "Set up the .NET 10 SDK for your account?`n`nIt downloads about 300 MB from Microsoft and goes in $UserSdkFolder. No administrator rights are needed."
    if (-not (Confirm-Action $question $true)) { return }

    $Job.Kind = 'Sdk'
    Initialize-Job
    $Job.InstallScript = Join-Path ([IO.Path]::GetTempPath()) ('dotnet-install-' + [guid]::NewGuid().ToString('N') + '.ps1')
    Write-JobLine "Downloading $SdkInstallScriptUrl"

    # Windows PowerShell may not offer TLS 1.2 by default.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $Job.Client = New-Object Net.WebClient
    $Job.Download = $Job.Client.DownloadFileTaskAsync([uri]$SdkInstallScriptUrl, $Job.InstallScript)
    $Job.Stage = 'Downloading'
    $Job.StageText = "Getting Microsoft's installer$Ellipsis"
    Set-Status ('Setting up the .NET SDK' + $Ellipsis) $Job.StageText 'Busy'
}

function Start-SdkInstaller {
    $download = $Job.Download
    $Job.Download = $null
    $Job.Client.Dispose()
    $Job.Client = $null
    if ($download.IsFaulted -or $download.IsCanceled) {
        $reason = if ($download.Exception) { $download.Exception.GetBaseException().Message } else { 'cancelled' }
        Write-JobLine "Download failed: $reason"
        Complete-Job $false "The .NET SDK couldn't be set up" ("Microsoft's installer couldn't be downloaded ($reason). Check the internet connection, or get the SDK from Microsoft with the link above.")
        return
    }

    $hostPath = (Get-Process -Id $PID).Path
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File {0} -Channel 10.0 -InstallDir {1} -NoPath' -f (ConvertTo-Argument $Job.InstallScript), (ConvertTo-Argument $UserSdkFolder)
    Write-JobLine "> dotnet-install.ps1 -Channel 10.0 -InstallDir $UserSdkFolder"
    $Job.Runner = New-Object HomeControlBuilder.ProcessRunner -ArgumentList @($hostPath, $arguments, $null, (Get-ChildEnvironment $null))
    $Job.Stage = 'InstallingSdk'
    $Job.StageText = 'Downloading and unpacking. This can take a few minutes.'
}

function Complete-SdkSetup([int] $ExitCode) {
    try { Remove-Item -LiteralPath $Job.InstallScript -Force -ErrorAction SilentlyContinue } catch { }
    Find-DotNetSdk
    Update-SdkCard
    if ($Sdk.Path) {
        Complete-Job $true 'The .NET SDK is set up' 'Now choose Build.'
    }
    else {
        Complete-Job $false "The .NET SDK couldn't be set up" ("Microsoft's installer stopped with code $ExitCode. The details below say why; or get the SDK from Microsoft with the link above.")
        if ($Ui) { Show-Details $true }
    }
}

# ------------------------------------------------------------------ the job, step by step

# Called a few times a second (by the window's timer, or the loop for -NoGui).
function Step-Job {
    if (-not $Job.Busy) { return }

    switch ($Job.Stage) {
        'Closing' {
            if (@(Get-RunningHomeControl).Count -eq 0) {
                Write-JobLine 'Home Control closed.'
                Start-Publish
            }
            elseif ([DateTime]::UtcNow -gt $Job.Deadline) {
                if (-not $Job.Killed) {
                    # An older build that doesn't know the exit message.
                    Write-JobLine "Home Control didn't close on request; closing it."
                    foreach ($process in Get-RunningHomeControl) { try { $process.Kill() } catch { } }
                    $Job.Killed = $true
                    $Job.Deadline = [DateTime]::UtcNow.AddSeconds(5)
                }
                else {
                    Complete-Job $false "Home Control didn't close" 'Close it from its tray icon (right-click, Exit) and try again.'
                }
            }
            break
        }

        'Building' {
            foreach ($line in $Job.Runner.TakeLines()) { Read-BuildLine $line }
            if ($Job.Runner.HasExited) {
                $exitCode = $Job.Runner.Finish()
                foreach ($line in $Job.Runner.TakeLines()) { Read-BuildLine $line }
                Write-JobLine "dotnet exited with code $exitCode."
                Complete-Build $exitCode
            }
            break
        }

        'Downloading' {
            if ($Job.Download.IsCompleted) { Start-SdkInstaller }
            break
        }

        'InstallingSdk' {
            foreach ($line in $Job.Runner.TakeLines()) { Write-JobLine $line }
            if ($Job.Runner.HasExited) {
                $exitCode = $Job.Runner.Finish()
                foreach ($line in $Job.Runner.TakeLines()) { Write-JobLine $line }
                Write-JobLine "The installer exited with code $exitCode."
                Complete-SdkSetup $exitCode
            }
            break
        }
    }

    if ($Job.Busy -and $Ui) {
        $elapsed = [DateTime]::UtcNow - $Job.StartedAt
        $Ui.StatusText.Text = '{0}  ({1:m\:ss})' -f $Job.StageText, $elapsed
    }
}

# ------------------------------------------------------------------ -NoGui

if ($NoGui) {
    try {
        Add-Type -TypeDefinition $NativeCode -Language CSharp
        $NativePlatform = Get-NativePlatform
        if (-not $Platform -and -not $NativePlatform) { throw 'Home Control needs 64-bit Windows (x64 or ARM64).' }
        Find-DotNetSdk
        if (-not $Sdk.Path) {
            throw "The .NET $RequiredSdkMajor SDK wasn't found. Install it from $SdkDownloadPage, or run this without -NoGui to set it up."
        }

        Start-Build
        while ($Job.Busy) {
            Step-Job
            Start-Sleep -Milliseconds 200
        }
    }
    catch {
        Write-Host "Error: $($_.Exception.Message)"
        exit 2
    }

    if ($Job.Succeeded) { exit 0 }
    exit 1
}

# ------------------------------------------------------------------ the window

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()

function New-Brush([string] $Color) {
    $brush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($Color))
    $brush.Freeze()
    return $brush
}

function ConvertTo-Hex([Windows.Media.Color] $Color) {
    '#{0:X2}{1:X2}{2:X2}{3:X2}' -f $Color.A, $Color.R, $Color.G, $Color.B
}

# Light or dark like Windows' app mode, with the accent color; system colors in high contrast.
function Get-Theme {
    if ([System.Windows.SystemParameters]::HighContrast) {
        $window = ConvertTo-Hex ([System.Windows.SystemColors]::WindowColor)
        $text = ConvertTo-Hex ([System.Windows.SystemColors]::WindowTextColor)
        $highlight = ConvertTo-Hex ([System.Windows.SystemColors]::HighlightColor)
        $highlightText = ConvertTo-Hex ([System.Windows.SystemColors]::HighlightTextColor)
        $button = ConvertTo-Hex ([System.Windows.SystemColors]::ControlColor)
        return @{
            Dark = $false
            Bg = $window; Card = $window; CardStroke = $text; Text = $text; Secondary = $text
            Stroke = $text; StrokeHover = $highlight; InputBg = $window; Track = $text
            ButtonBg = $button; ButtonHover = $button; ButtonPressed = $button
            Accent = $highlight; AccentHover = $highlight; AccentPressed = $highlight; AccentText = $highlightText
            Success = $text; Error = $text; Warning = $text
        }
    }

    $appsUseLightTheme = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize', 'AppsUseLightTheme', $null)
    $dark = $null -ne $appsUseLightTheme -and [int]$appsUseLightTheme -eq 0

    # Accent buttons use the accent's "dark 1" shade in light mode and "light 2" in dark mode, as WinUI does.
    $accent = if ($dark) { '#FF60CDFF' } else { '#FF005FB8' }
    $palette = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent', 'AccentPalette', $null)
    if ($palette -is [byte[]] -and $palette.Length -ge 32) {
        $index = if ($dark) { 1 } else { 4 }
        $accent = '#FF{0:X2}{1:X2}{2:X2}' -f $palette[$index * 4], $palette[$index * 4 + 1], $palette[$index * 4 + 2]
    }
    $accentHover = '#E6' + $accent.Substring(3)
    $accentPressed = '#CC' + $accent.Substring(3)

    if ($dark) {
        return @{
            Dark = $true
            Bg = '#FF202020'; Card = '#FF2B2B2B'; CardStroke = '#FF353535'; Text = '#FFFFFFFF'; Secondary = '#FFC5C5C5'
            Stroke = '#FF4A4A4A'; StrokeHover = '#FF9A9A9A'; InputBg = '#FF323232'; Track = '#FF505050'
            ButtonBg = '#FF2F2F2F'; ButtonHover = '#FF383838'; ButtonPressed = '#FF292929'
            Accent = $accent; AccentHover = $accentHover; AccentPressed = $accentPressed; AccentText = '#FF000000'
            Success = '#FF6CCB5F'; Error = '#FFFF99A4'; Warning = '#FFFCE100'
        }
    }

    return @{
        Dark = $false
        Bg = '#FFF3F3F3'; Card = '#FFFBFBFB'; CardStroke = '#FFE5E5E5'; Text = '#FF1B1B1B'; Secondary = '#FF5D5D5D'
        Stroke = '#FFD0D0D0'; StrokeHover = '#FF8A8A8A'; InputBg = '#FFFFFFFF'; Track = '#FFD6D6D6'
        ButtonBg = '#FFFFFFFF'; ButtonHover = '#FFF6F6F6'; ButtonPressed = '#FFEDEDED'
        Accent = $accent; AccentHover = $accentHover; AccentPressed = $accentPressed; AccentText = '#FFFFFFFF'
        Success = '#FF0F7B0F'; Error = '#FFC42B1C'; Warning = '#FF9D5D00'
    }
}

$WindowXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Build Home Control" Width="640" SizeToContent="Height" ResizeMode="CanMinimize"
        WindowStartupLocation="CenterScreen" Background="@Bg@" Foreground="@Text@" UseLayoutRounding="True"
        FontFamily="Segoe UI Variable Text, Segoe UI" FontSize="14">
  <Window.Resources>
    <FontFamily x:Key="Icons">Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>

    <Style x:Key="FocusRing">
      <Setter Property="Control.Template">
        <Setter.Value>
          <ControlTemplate>
            <Border Margin="-3" CornerRadius="7" BorderThickness="2" BorderBrush="@Text@" />
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Card" TargetType="Border">
      <Setter Property="Background" Value="@Card@" />
      <Setter Property="BorderBrush" Value="@CardStroke@" />
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="CornerRadius" Value="8" />
      <Setter Property="Padding" Value="20,16" />
    </Style>

    <Style x:Key="Heading" TargetType="TextBlock">
      <Setter Property="FontWeight" Value="SemiBold" />
      <Setter Property="TextWrapping" Value="Wrap" />
    </Style>

    <Style x:Key="Secondary" TargetType="TextBlock">
      <Setter Property="Foreground" Value="@Secondary@" />
      <Setter Property="FontSize" Value="13" />
      <Setter Property="TextWrapping" Value="Wrap" />
    </Style>

    <Style x:Key="Glyph" TargetType="TextBlock">
      <Setter Property="FontFamily" Value="{StaticResource Icons}" />
      <Setter Property="FontSize" Value="20" />
    </Style>

    <Style TargetType="Hyperlink">
      <Setter Property="Foreground" Value="@Accent@" />
    </Style>

    <Style x:Key="Button" TargetType="Button">
      <Setter Property="Foreground" Value="@Text@" />
      <Setter Property="Background" Value="@ButtonBg@" />
      <Setter Property="BorderBrush" Value="@Stroke@" />
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="Padding" Value="16,6" />
      <Setter Property="MinWidth" Value="96" />
      <Setter Property="MinHeight" Value="32" />
      <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}" />
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsEnabled" Value="False">
                <Setter TargetName="Chrome" Property="Opacity" Value="0.45" />
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
      <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter Property="Background" Value="@ButtonHover@" />
        </Trigger>
        <Trigger Property="IsPressed" Value="True">
          <Setter Property="Background" Value="@ButtonPressed@" />
        </Trigger>
      </Style.Triggers>
    </Style>

    <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource Button}">
      <Setter Property="Foreground" Value="@AccentText@" />
      <Setter Property="Background" Value="@Accent@" />
      <Setter Property="BorderBrush" Value="@Accent@" />
      <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter Property="Background" Value="@AccentHover@" />
        </Trigger>
        <Trigger Property="IsPressed" Value="True">
          <Setter Property="Background" Value="@AccentPressed@" />
        </Trigger>
      </Style.Triggers>
    </Style>

    <Style x:Key="LinkButton" TargetType="Button">
      <Setter Property="Foreground" Value="@Accent@" />
      <Setter Property="Background" Value="Transparent" />
      <Setter Property="Cursor" Value="Hand" />
      <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}" />
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border Background="{TemplateBinding Background}" Padding="0,2">
              <ContentPresenter RecognizesAccessKey="True" />
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
      <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter Property="Opacity" Value="0.8" />
        </Trigger>
      </Style.Triggers>
    </Style>

    <Style TargetType="CheckBox">
      <Setter Property="Foreground" Value="@Text@" />
      <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}" />
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="CheckBox">
            <Grid Background="Transparent">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
              </Grid.ColumnDefinitions>
              <Border x:Name="Box" Width="20" Height="20" CornerRadius="4" BorderThickness="1"
                      BorderBrush="@StrokeHover@" Background="@InputBg@" VerticalAlignment="Center">
                <TextBlock x:Name="Mark" Text="&#xE73E;" FontFamily="{StaticResource Icons}" FontSize="12"
                           Foreground="@AccentText@" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed" />
              </Border>
              <ContentPresenter Grid.Column="1" Margin="10,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True" />
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Box" Property="BorderBrush" Value="@Text@" />
              </Trigger>
              <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Box" Property="Background" Value="@Accent@" />
                <Setter TargetName="Box" Property="BorderBrush" Value="@Accent@" />
                <Setter TargetName="Mark" Property="Visibility" Value="Visible" />
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.45" />
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType="RadioButton">
      <Setter Property="Foreground" Value="@Text@" />
      <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}" />
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="RadioButton">
            <Grid Background="Transparent">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
              </Grid.ColumnDefinitions>
              <Grid Width="20" Height="20" VerticalAlignment="Center">
                <Ellipse x:Name="Ring" Stroke="@StrokeHover@" StrokeThickness="1" Fill="@InputBg@" />
                <Ellipse x:Name="Dot" Width="10" Height="10" Fill="@AccentText@" Visibility="Collapsed" />
              </Grid>
              <ContentPresenter Grid.Column="1" Margin="10,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True" />
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Ring" Property="Stroke" Value="@Text@" />
              </Trigger>
              <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Ring" Property="Fill" Value="@Accent@" />
                <Setter TargetName="Ring" Property="Stroke" Value="@Accent@" />
                <Setter TargetName="Dot" Property="Visibility" Value="Visible" />
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Opacity" Value="0.45" />
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key="Input" TargetType="TextBox">
      <Setter Property="Foreground" Value="@Text@" />
      <Setter Property="Background" Value="@InputBg@" />
      <Setter Property="BorderBrush" Value="@Stroke@" />
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="Padding" Value="8,5" />
      <Setter Property="CaretBrush" Value="@Text@" />
      <Setter Property="SelectionBrush" Value="@Accent@" />
      <Setter Property="VerticalContentAlignment" Value="Center" />
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="TextBox">
            <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4">
              <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                            VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                            HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" />
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsKeyboardFocused" Value="True">
                <Setter TargetName="Frame" Property="BorderBrush" Value="@Accent@" />
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter TargetName="Frame" Property="Opacity" Value="0.45" />
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Border x:Name="Root" Background="@Bg@">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height="*" />
        <RowDefinition Height="Auto" />
      </Grid.RowDefinitions>

      <!-- Scrolls on small screens; the buttons below stay in view. -->
      <ScrollViewer x:Name="Scroller" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
    <StackPanel Margin="28,24,28,4">
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto" />
          <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>
        <Image x:Name="Logo" Width="40" Height="40" Margin="0,0,16,0" VerticalAlignment="Center" />
        <StackPanel Grid.Column="1" VerticalAlignment="Center">
          <TextBlock Text="Build Home Control" FontFamily="Segoe UI Variable Display, Segoe UI" FontSize="26" FontWeight="SemiBold" />
          <TextBlock Style="{StaticResource Secondary}" FontSize="14" Margin="0,2,0,0"
                     Text="Turns the code in this folder into the app, ready to use. There's nothing to type." />
        </StackPanel>
      </Grid>

      <Border Style="{StaticResource Card}" Margin="0,20,0,0">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
          </Grid.ColumnDefinitions>
          <TextBlock x:Name="SdkIcon" Style="{StaticResource Glyph}" Text="&#xE946;" Foreground="@Secondary@" Margin="0,1,14,0" VerticalAlignment="Top" />
          <StackPanel Grid.Column="1" VerticalAlignment="Center">
            <TextBlock x:Name="SdkTitle" Style="{StaticResource Heading}" Text="Looking for the .NET SDK&#x2026;" />
            <TextBlock x:Name="SdkText" Style="{StaticResource Secondary}" Margin="0,2,0,0" />
            <TextBlock x:Name="SdkLinkText" Style="{StaticResource Secondary}" Margin="0,6,0,0" Visibility="Collapsed">
              <Hyperlink x:Name="SdkLink"><Run Text="Or get it from Microsoft yourself" /></Hyperlink>
            </TextBlock>
          </StackPanel>
          <Button x:Name="InstallSdkButton" Grid.Column="2" Style="{StaticResource Button}" Content="Set it up"
                  Margin="16,0,0,0" VerticalAlignment="Center" Visibility="Collapsed" />
        </Grid>
      </Border>

      <Border x:Name="OptionsCard" Style="{StaticResource Card}" Margin="0,8,0,0">
        <StackPanel>
          <TextBlock Style="{StaticResource Heading}" Text="Build for" />
          <RadioButton x:Name="X64Option" GroupName="Platform" Margin="0,10,0,0" Content="x64: an Intel or AMD processor" />
          <RadioButton x:Name="Arm64Option" GroupName="Platform" Margin="0,8,0,0" Content="ARM64: a Snapdragon or other ARM processor" />
          <TextBlock x:Name="PlatformHint" Style="{StaticResource Secondary}" Margin="30,6,0,0" />

          <TextBlock Style="{StaticResource Heading}" Text="Put it in" Margin="0,18,0,0" />
          <Grid Margin="0,8,0,0">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="*" />
              <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBox x:Name="DestinationBox" Style="{StaticResource Input}" AutomationProperties.Name="Folder for Home Control" />
            <Button x:Name="BrowseButton" Grid.Column="1" Style="{StaticResource Button}" Content="Browse&#x2026;" Margin="8,0,0,0" />
          </Grid>
          <TextBlock Style="{StaticResource Secondary}" Margin="0,6,0,0"
                     Text="Your settings and Google sign-in are stored separately, so building again keeps them." />

          <CheckBox x:Name="ShortcutOption" Margin="0,16,0,0" Content="Add Home Control to the Start menu" />
          <CheckBox x:Name="StartOption" Margin="0,10,0,0" Content="Start Home Control when it's ready" />
        </StackPanel>
      </Border>

      <Border x:Name="StatusCard" Style="{StaticResource Card}" Margin="0,8,0,0" Visibility="Collapsed">
        <StackPanel>
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="StatusIcon" Style="{StaticResource Glyph}" Margin="0,1,14,0" VerticalAlignment="Top" />
            <StackPanel Grid.Column="1">
              <TextBlock x:Name="StatusTitle" Style="{StaticResource Heading}" />
              <TextBlock x:Name="StatusText" Style="{StaticResource Secondary}" Margin="0,2,0,0" />
            </StackPanel>
          </Grid>
          <ProgressBar x:Name="Progress" Height="4" Margin="0,14,0,0" IsIndeterminate="True"
                       Foreground="@Accent@" Background="@Track@" BorderThickness="0" />
          <Button x:Name="DetailsButton" Style="{StaticResource LinkButton}" Content="Show details" HorizontalAlignment="Left" Margin="0,12,0,0" />
          <TextBox x:Name="LogBox" Style="{StaticResource Input}" Visibility="Collapsed" Margin="0,8,0,0" Height="200"
                   IsReadOnly="True" IsReadOnlyCaretVisible="True" FontFamily="Cascadia Mono, Consolas" FontSize="12"
                   TextWrapping="NoWrap" VerticalContentAlignment="Stretch"
                   VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                   AutomationProperties.Name="Build details" />
        </StackPanel>
      </Border>

    </StackPanel>
      </ScrollViewer>

      <Grid Grid.Row="1" Margin="28,16,28,24">
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Left">
          <Button x:Name="OpenFolderButton" Style="{StaticResource Button}" Content="Open folder" Visibility="Collapsed" />
          <Button x:Name="RunButton" Style="{StaticResource Button}" Content="Start Home Control" Margin="8,0,0,0" Visibility="Collapsed" />
        </StackPanel>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
          <Button x:Name="BuildButton" Style="{StaticResource AccentButton}" Content="Build" IsDefault="True" MinWidth="120" />
          <Button x:Name="CloseButton" Style="{StaticResource Button}" Content="Close" Margin="8,0,0,0" />
        </StackPanel>
      </Grid>
    </Grid>
  </Border>
</Window>
'@

# Appends the output collected since the last tick (one update per tick keeps it quick).
function Flush-Output {
    if (-not $Ui -or $Ui.Pending.Length -eq 0) { return }
    $Ui.LogBox.AppendText($Ui.Pending.ToString())
    $Ui.LogBox.ScrollToEnd()
    [void]$Ui.Pending.Clear()
}

function Show-Details([bool] $Show) {
    $Ui.LogBox.Visibility = if ($Show) { 'Visible' } else { 'Collapsed' }
    $Ui.DetailsButton.Content = if ($Show) { 'Hide details' } else { 'Show details' }
    if ($Show) { Show-InView $Ui.LogBox }
}

# The window grows as the status and details appear: keep it within the screen.
function Limit-WindowToScreen {
    $area = [System.Windows.SystemParameters]::WorkArea
    $window = $Ui.Window
    $window.MaxHeight = $area.Height
    if ($window.Top + $window.ActualHeight -gt $area.Bottom) {
        $window.Top = [Math]::Max($area.Top, $area.Bottom - $window.ActualHeight)
    }
}

function Select-Folder {
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Choose the folder for Home Control. An empty folder is best.'
    $dialog.ShowNewFolderButton = $true
    $current = $Ui.DestinationBox.Text.Trim()
    try {
        if ($current -and (Test-Path -LiteralPath $current -PathType Container)) { $dialog.SelectedPath = $current }
        elseif ($current -and (Test-Path -LiteralPath (Split-Path -Parent $current) -PathType Container)) { $dialog.SelectedPath = Split-Path -Parent $current }
    }
    catch {
        # Not a usable path: start from the default place.
    }

    $owner = New-Object System.Windows.Forms.NativeWindow
    $owner.AssignHandle($Ui.Handle)
    try {
        if ($dialog.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) {
            $Ui.DestinationBox.Text = $dialog.SelectedPath
        }
    }
    finally {
        $owner.ReleaseHandle()
        $dialog.Dispose()
    }
}

function Save-Screenshot([string] $Name) {
    $root = $Ui.Root
    $root.UpdateLayout()
    $dpi = [Windows.Media.VisualTreeHelper]::GetDpi($root)
    $width = [int][Math]::Ceiling($root.ActualWidth * $dpi.DpiScaleX)
    $height = [int][Math]::Ceiling($root.ActualHeight * $dpi.DpiScaleY)
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width, $height, $dpi.PixelsPerInchX, $dpi.PixelsPerInchY, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($root)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $SmokeTest $Name))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Complete-SmokeTest([string] $Result) {
    $Smoke.Stage = 'Done'
    try { Save-Screenshot 'builder-done.png' } catch { $Result += " (screenshot: $($_.Exception.Message))" }
    Set-Content -LiteralPath (Join-Path $SmokeTest 'builder-result.txt') -Value $Result -Encoding UTF8
    $Ui.Window.Close()
}

# -SmokeTest: screenshot, build, screenshot, close. Called by the timer.
function Step-SmokeTest {
    switch ($Smoke.Stage) {
        'Ready' {
            if ([DateTime]::UtcNow -lt $Smoke.At) { return }
            Save-Screenshot 'builder-ready.png'
            Show-Details $true
            $Smoke.Stage = 'Building'
            $Smoke.Deadline = [DateTime]::UtcNow.AddMinutes(25)
            Start-Build
            if (-not $Job.Busy) { Complete-SmokeTest "FAILED: the build didn't start ($($Ui.StatusTitle.Text))" }
        }
        'Building' {
            if ($Job.Busy) {
                if ([DateTime]::UtcNow -gt $Smoke.Deadline) {
                    Stop-CurrentJob
                    Complete-SmokeTest 'FAILED: timed out'
                }
                return
            }
            $Smoke.Stage = 'Finishing'
            $Smoke.At = [DateTime]::UtcNow.AddSeconds(1) # let it render the result
        }
        'Finishing' {
            if ([DateTime]::UtcNow -lt $Smoke.At) { return }
            if ($Job.Succeeded) { Complete-SmokeTest 'OK' }
            else { Complete-SmokeTest "FAILED: $($Ui.StatusTitle.Text): $($Ui.StatusText.Text)" }
        }
    }
}

# Event handlers run outside any try/catch of the script: report errors instead of vanishing.
function Invoke-Safely([scriptblock] $Action) {
    try {
        & $Action
    }
    catch {
        Show-Crash $_
    }
}

function Show-Crash($ErrorRecord) {
    if ($Ui -and $Ui.ShowingError) { return } # e.g. the timer failing again behind the message
    $message = "$($ErrorRecord.Exception.Message)`n`n$($ErrorRecord.ScriptStackTrace)"
    try {
        [void](New-Item -ItemType Directory -Force -Path $DataFolder)
        Add-Content -LiteralPath $LogPath -Value "Builder error: $message" -Encoding UTF8
    }
    catch {
    }

    if ($Job.Busy) {
        if ($Job.Runner) { try { $Job.Runner.Kill() } catch { } }
        $Job.Busy = $false
        $Job.Stage = 'Idle'
        $Job.Runner = $null
        try { Update-Buttons } catch { }
    }

    if ($SmokeTest) {
        try {
            Set-Content -LiteralPath (Join-Path $SmokeTest 'builder-result.txt') -Value "FAILED: $message" -Encoding UTF8
            $Ui.Window.Close()
        }
        catch {
            exit 3
        }
        return
    }

    $text = "Something went wrong in the builder:`n`n$message`n`nThis is also saved in $LogPath."
    try {
        if ($Ui) { $Ui.ShowingError = $true }
        [void](Show-Dialog $text 'OK' 'Error')
    }
    finally {
        if ($Ui) { $Ui.ShowingError = $false }
    }
}

try {
    Add-Type -TypeDefinition $NativeCode -Language CSharp
    $NativePlatform = Get-NativePlatform
    $Theme = Get-Theme

    $xaml = $WindowXaml
    foreach ($key in $Theme.Keys) { $xaml = $xaml.Replace("@$key@", [string]$Theme[$key]) }
    $window = [Windows.Markup.XamlReader]::Parse($xaml)

    $Ui = @{ Window = $window; Handle = [IntPtr]::Zero; Pending = New-Object Text.StringBuilder; ShowingError = $false }
    foreach ($name in @(
            'Root', 'Scroller', 'Logo', 'SdkIcon', 'SdkTitle', 'SdkText', 'SdkLinkText', 'SdkLink', 'InstallSdkButton',
            'OptionsCard', 'X64Option', 'Arm64Option', 'PlatformHint', 'DestinationBox', 'BrowseButton',
            'ShortcutOption', 'StartOption', 'StatusCard', 'StatusIcon', 'StatusTitle', 'StatusText',
            'Progress', 'DetailsButton', 'LogBox', 'OpenFolderButton', 'RunButton', 'BuildButton', 'CloseButton')) {
        $Ui[$name] = $window.FindName($name)
    }

    if (Test-Path -LiteralPath $AppIconPath) {
        $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create([uri]$AppIconPath, 'None', 'OnLoad')
        $window.Icon = $decoder.Frames[0]
        $Ui.Logo.Source = $decoder.Frames | Sort-Object { [Math]::Abs($_.PixelWidth - 80) } | Select-Object -First 1
    }

    # Options: the parameters, else what was used last time, else the defaults.
    $saved = Read-BuilderSettings
    $selected = if ($Platform) { $Platform } elseif ($NativePlatform) { $NativePlatform } else { 'x64' }
    $Ui.X64Option.IsChecked = $selected -eq 'x64'
    $Ui.Arm64Option.IsChecked = $selected -eq 'ARM64'
    $Ui.PlatformHint.Text = switch ($NativePlatform) {
        'x64' { 'This PC has an x64 processor.'; break }
        'ARM64' { 'This PC has an ARM64 processor.'; break }
        default { '' }
    }
    $Ui.DestinationBox.Text = if ($Destination) { $Destination } elseif ($saved.Destination) { [string]$saved.Destination } else { $DefaultDestination }
    $Ui.ShortcutOption.IsChecked = if ($NoShortcut) { $false } else { [bool]$saved.Shortcut }
    $Ui.StartOption.IsChecked = if ($NoStart) { $false } else { [bool]$saved.Start }

    $window.Add_SourceInitialized({
            Invoke-Safely {
                $Ui.Handle = (New-Object Windows.Interop.WindowInteropHelper($Ui.Window)).Handle
                [HomeControlBuilder.Native]::SetDarkTitleBar($Ui.Handle, [bool]$Theme.Dark)
            }
        })

    $window.MaxHeight = [System.Windows.SystemParameters]::WorkArea.Height
    $window.Add_SizeChanged({ Invoke-Safely { Limit-WindowToScreen } })

    $window.Add_ContentRendered({
            Invoke-Safely {
                if ($Smoke.Stage -ne 'Waiting') { return } # once
                $Smoke.Stage = 'Started'
                $Ui.Window.Cursor = [System.Windows.Input.Cursors]::Wait
                try { Find-DotNetSdk } finally { $Ui.Window.Cursor = $null }
                Update-SdkCard
                if ($SmokeTest) {
                    [void](New-Item -ItemType Directory -Force -Path $SmokeTest)
                    $Smoke.Stage = 'Ready'
                    $Smoke.At = [DateTime]::UtcNow.AddSeconds(1)
                }
                elseif ($Ui.BuildButton.IsEnabled) {
                    [void]$Ui.BuildButton.Focus()
                }
            }
        })

    $window.Add_Closing({
            param($sender, $e)
            Invoke-Safely {
                if ($Job.Busy) {
                    if ($SmokeTest -or (Confirm-Action 'A build is running. Stop it and close?' $true)) {
                        Stop-CurrentJob
                    }
                    else {
                        $e.Cancel = $true
                    }
                }
            }
        })

    $Ui.BuildButton.Add_Click({ Invoke-Safely { Start-Build } })
    $Ui.CloseButton.Add_Click({
            Invoke-Safely {
                if ($Job.Busy) { Stop-CurrentJob } else { $Ui.Window.Close() }
            }
        })
    $Ui.BrowseButton.Add_Click({ Invoke-Safely { Select-Folder } })
    $Ui.InstallSdkButton.Add_Click({ Invoke-Safely { Start-SdkSetup } })
    $Ui.SdkLink.Add_Click({ Invoke-Safely { Start-Process $SdkDownloadPage } })
    $Ui.DetailsButton.Add_Click({ Invoke-Safely { Show-Details ($Ui.LogBox.Visibility -ne 'Visible') } })
    $Ui.OpenFolderButton.Add_Click({ Invoke-Safely { Start-Process -FilePath $Job.Destination } })
    $Ui.RunButton.Add_Click({
            Invoke-Safely {
                Start-Process -FilePath (Join-Path $Job.Destination 'HomeControl.exe') -WorkingDirectory $Job.Destination
            }
        })

    $timer = New-Object Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(200)
    $timer.Add_Tick({
            Invoke-Safely {
                Step-Job
                Flush-Output
                if ($SmokeTest) { Step-SmokeTest }
            }
        })
    $timer.Start()

    Update-Buttons
    [void]$window.ShowDialog()
    $timer.Stop()
}
catch {
    Show-Crash $_
    exit 1
}
