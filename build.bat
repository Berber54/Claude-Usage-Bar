@echo off
rem Builds ClaudeUsageBar.exe using the C# compiler that ships with Windows and
rem installs it to %LOCALAPPDATA%\ClaudeUsageBar, outside this source folder.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

if not exist "ClaudeUsageBar.cs" goto nosrc

set "DEST=%LOCALAPPDATA%\ClaudeUsageBar"
if not exist "%DEST%" mkdir "%DEST%"

rem Stop a running instance so the exe can be overwritten.
taskkill /f /im ClaudeUsageBar.exe >nul 2>&1

echo Compiling...
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:app.ico /out:"%DEST%\ClaudeUsageBar.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ClaudeUsageBar.cs
if errorlevel 1 goto fail

echo.
echo Build OK: %DEST%\ClaudeUsageBar.exe

rem Create/refresh the Start Menu shortcut so it shows in the app list and search.
rem The icon is read from the exe itself, so the shortcut survives moving this folder.
echo Creating Start Menu shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Usage Bar.lnk'; $exe = Join-Path $env:LOCALAPPDATA 'ClaudeUsageBar\ClaudeUsageBar.exe'; $w = New-Object -ComObject WScript.Shell; $s = $w.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = Split-Path $exe; $s.IconLocation = \"$exe,0\"; $s.Description = 'Claude token usage battery in the system tray'; $s.Save()"

echo.
choice /m "Run it now"
if errorlevel 2 goto end
start "" "%DEST%\ClaudeUsageBar.exe"
goto end

:nocsc
echo ERROR: Could not find csc.exe. .NET Framework 4.x is required.
echo It is preinstalled on Windows 10 and 11, so this should not normally happen.
goto end

:nosrc
echo ERROR: ClaudeUsageBar.cs not found in this folder: %CD%
echo Put build.bat and ClaudeUsageBar.cs in the same folder.
goto end

:fail
echo.
echo Build FAILED - see the compiler errors above.

:end
echo.
pause
