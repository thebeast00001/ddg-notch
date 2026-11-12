@echo off
rem Writes the summary, decisions, open questions and to-dos for a call transcript.
rem   summarize.cmd                         the made-up English sample call
rem   summarize.cmd out\call-....md         one of your own saved transcripts
rem   summarize.cmd --show-prompt           print exactly what would be sent, and send nothing
rem   summarize.cmd --cloud-notes           use Sarvam's chat model; the transcript text is sent to Sarvam
rem Without --cloud-notes only a model running on this PC (Ollama or LM Studio) is used.
rem Any other service: set FORTY_LLM_URL, FORTY_LLM_KEY and FORTY_LLM_MODEL first.
chcp 65001 >nul
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
"%~dp0SpeechSpike\bin\Release\net10.0\win-x64\SpeechSpike.exe" summarize %*
pause
