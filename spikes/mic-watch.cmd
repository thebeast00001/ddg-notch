@echo off
rem F136 check: shows each change of the default microphone for a minute while you plug a headset
rem in or out (or turn Bluetooth on and off). Nothing is opened or recorded.
rem   mic-watch.cmd          one minute
rem   mic-watch.cmd 180      three minutes
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" mic-watch %1
pause
