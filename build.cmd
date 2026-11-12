@echo off
rem Builds the island and runs the tests, with no downloads (packages come from the local NuGet cache).
rem   build.cmd           build everything, then run the tests
rem   build.cmd probe     also print what each Windows adapter reports right now (read-only)
setlocal
set DOTNET_ROOT=%USERPROFILE%\tools\dotnet10
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET="%USERPROFILE%\tools\dotnet10\dotnet.exe"
cd /d "%~dp0"
%DOTNET% restore src\Forty.App\Forty.App.csproj --source "%USERPROFILE%\.nuget\packages" || goto :failed
%DOTNET% restore tests\Forty.Tests\Forty.Tests.csproj --source "%USERPROFILE%\.nuget\packages" || goto :failed
%DOTNET% build src\Forty.App\Forty.App.csproj -c Release --no-restore -v q -nologo || goto :failed
%DOTNET% build tests\Forty.Tests\Forty.Tests.csproj -c Release --no-restore -v q -nologo || goto :failed
"tests\Forty.Tests\bin\Release\net10.0-windows10.0.26100.0\Forty.Tests.exe" || goto :failed
if "%1"=="probe" "tests\Forty.Tests\bin\Release\net10.0-windows10.0.26100.0\Forty.Tests.exe" probe 3
exit /b 0
:failed
echo Build or tests failed.
exit /b 1
