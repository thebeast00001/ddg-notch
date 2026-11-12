@echo off
rem Starts the island (phase 1 build). Hover it, click it, or press Ctrl+Alt+Space; Esc closes the panel.
rem The panel has a Quit link. Build first with build.cmd.
rem   set FORTY_DEMO=1        plays a scripted sequence and saves pictures to FORTY_OUT, then quits
rem   set FORTY_GLASS=1       turns on the Glass experiment (spike S6; it does not blur yet)
rem   set FORTY_FAULT=all-down switches every Windows adapter off, to check nothing breaks
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
start "" "%~dp0src\Forty.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\Forty.exe"
