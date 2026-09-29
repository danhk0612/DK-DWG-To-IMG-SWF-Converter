@echo off
setlocal
cd /d "%~dp0"
dotnet restore || exit /b 1
dotnet build -c Release || exit /b 1
pause
