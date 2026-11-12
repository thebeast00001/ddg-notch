@echo off
rem Checks everything the daily flow depends on, and shows which engine does which job:
rem keys, both services (with a two-second test sentence), the models on this PC, the island.
rem   check.cmd           as start-forty.cmd runs it: English
rem   check.cmd hi        Hindi
rem Run this first if anything feels slow or fails.
chcp 65001 >nul
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" check --cloud %*
pause
