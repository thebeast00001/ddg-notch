@echo off
rem One-time setup: asks for your Sarvam and ElevenLabs keys, checks each with its service, and
rem stores them encrypted for your Windows account. Nothing you type is shown on screen.
rem   setup-keys.cmd             enter or replace keys (Enter alone keeps the current one)
rem   setup-keys.cmd --status    show which keys are set, never the keys themselves
rem   setup-keys.cmd --forget    remove the stored keys
chcp 65001 >nul
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" keys %*
pause
