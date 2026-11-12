@echo off
rem Takes notes on a call that is already running: the call app's audio and your microphone are
rem recorded as two separate streams, then written out as a transcript with "You" and "Others".
rem   call-notes.cmd                    finds the app that is in a call, English
rem   call-notes.cmd hi                 Hindi (Sarvam if its key is set, else the local model)
rem   call-notes.cmd --app chrome       listen to a named app
rem   call-notes.cmd --minutes 5        stop by itself after five minutes (default ten)
rem   call-notes.cmd --no-mic           their side only; your microphone is not opened
rem   call-notes.cmd --keep-audio       also save the two recordings, for checking echo by ear
rem   call-notes.cmd --cloud            ElevenLabs transcribes and tells the other voices apart (it keeps
rem                                     the audio it is sent), and Sarvam's chat model writes the summary
rem   call-notes.cmd --cloud-notes      only the summary from Sarvam; the transcript stays on this PC
rem Press Enter to stop. Without --keep-audio the audio is never written to disk.
rem A summary, decisions and to-dos are added above the transcript when a language model is there:
rem one running on this PC (Ollama or LM Studio) is found by itself; a hosted one needs --cloud-notes.
rem Tell the people on the call that you are taking notes.
chcp 65001 >nul
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" call-notes %*
pause
