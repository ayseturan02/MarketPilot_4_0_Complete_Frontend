@echo off
cd /d "%~dp0src\MarketPilot.Web"
where dotnet >nul 2>nul
if errorlevel 1 (
 echo .NET 8 SDK bulunamadi. https://dotnet.microsoft.com/download/dotnet/8.0 adresinden yukleyin.
 pause
 exit /b 1
)
echo MarketPilot http://localhost:5080 adresinde baslatiliyor...
dotnet run --urls "http://localhost:5080"
pause
