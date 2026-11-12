@echo off
rem Starts the spike island so you can hover and click it. Right-click the island to quit.
rem   run-island.cmd        additive springs (the engine that won the S2 comparison)
rem   run-island.cmd a      Windows' built-in spring, for comparison
rem   run-island.cmd b      closed-form expression, for comparison
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
set FORTY_SPIKE=run
set FORTY_OUT=%~dp0out
if not "%1"=="" (set FORTY_ENGINE=%1) else (set FORTY_ENGINE=c)
start "" "%~dp0IslandSpike\bin\Release\net10.0-windows10.0.26100.0\win-x64\IslandSpike.exe"
