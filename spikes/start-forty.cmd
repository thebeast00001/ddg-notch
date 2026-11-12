@echo off
rem Starts the island, push-to-talk dictation and call notes together.
rem First time: run setup-keys.cmd once. If anything feels slow or fails: run check.cmd.
rem
rem   start-forty.cmd                    English
rem   start-forty.cmd hi                 Hindi
rem   start-forty.cmd hi --mode codemix  Hinglish through Sarvam
rem   start-forty.cmd --no-mic           call notes record only the other side; your microphone stays closed for calls
rem   start-forty.cmd --local            nothing about a call leaves this PC: no ElevenLabs, no summary
rem
rem Who does what:
rem   English dictation   the model on this PC. No internet, no cost.
rem   Hindi dictation     Sarvam. If it has not answered in about a second, ElevenLabs is asked too and
rem                       the first answer wins. If neither answers, the model on this PC does.
rem   Call transcript     ElevenLabs, because it tells the other voices apart ("Speaker 1", "Speaker 2").
rem                       Silence is cut out first. ElevenLabs keeps the audio it is sent. If it fails,
rem                       the standby engine writes the transcript, without telling voices apart.
rem   Call summary        Sarvam's chat model, from the transcript text.
rem
rem Dictation: hold Right Ctrl in any app, speak, let go. The island shows listening, working and done.
rem Call notes: when a call starts, the island asks "Take notes?". Nothing is recorded or sent before
rem   you press Yes. The island then shows a recording capsule with Stop. Notes are saved in the out
rem   folder, and the island shows "Notes ready" with an Open button for a few seconds.
rem   Tell the people on the call that you are taking notes.
rem Quit: Ctrl+C in this window, then right-click the island to close it.
chcp 65001 >nul
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
set FORTY_SPIKE=run
set FORTY_ENGINE=c
set FORTY_OUT=%~dp0out
start "" "%~dp0IslandSpike\bin\Release\net10.0-windows10.0.26100.0\win-x64\IslandSpike.exe"
timeout /t 2 /nobreak >nul
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" dictate --calls --cloud %*
pause
