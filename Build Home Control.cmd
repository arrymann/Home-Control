@echo off
rem Builds Home Control: double-click this file. A window opens; there's nothing to type.
rem It runs tools\builder\Build-HomeControl.ps1 with Windows PowerShell.
setlocal
set "BUILDER=%~dp0tools\builder\Build-HomeControl.ps1"
if not exist "%BUILDER%" goto missing
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%BUILDER%"
if errorlevel 1 goto failed
exit /b 0

:missing
echo The builder wasn't found: "%BUILDER%"
echo Download the whole Home Control folder again, then try once more.
echo.
pause
exit /b 1

:failed
echo.
echo The builder couldn't start. The message above says why.
echo.
pause
exit /b 1
