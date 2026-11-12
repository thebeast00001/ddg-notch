@echo off
rem Speak one sentence and see what each speech engine hears, and how long it takes.
rem   speak.cmd en      English
rem   speak.cmd hi      Hindi or Hinglish
rem The microphone is open only between the two Enter presses. The audio is not saved.
rem Sarvam is included when the SARVAM_API_KEY environment variable is set.
chcp 65001 >nul
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
if "%1"=="" (set SPEAK_LANG=en) else (set SPEAK_LANG=%1)
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" record %SPEAK_LANG%
pause
