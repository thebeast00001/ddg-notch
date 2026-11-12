@echo off
rem Push-to-talk dictation. Leave this window open, click into any app, hold Right Ctrl, speak, let go.
rem   dictate.cmd                          English, on this PC (Parakeet)
rem   dictate.cmd hi                       Hindi: Sarvam if its key is set, else ElevenLabs, else on this PC
rem   dictate.cmd hi --mode codemix        Hinglish through Sarvam: Hindi in Devanagari, English words in Latin
rem   dictate.cmd hi --engine omnilingual  Hindi on this PC, nothing leaves it
rem   dictate.cmd en --engine moonshine    the small fast English model
rem   dictate.cmd --key ralt               use Right Alt instead of Right Ctrl (also: f9)
rem The microphone is open only while the key is held. Audio is never saved. Ctrl+C quits.
chcp 65001 >nul
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" dictate %*
pause
