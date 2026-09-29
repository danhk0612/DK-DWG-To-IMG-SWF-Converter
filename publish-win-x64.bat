@echo off
setlocal
cd /d "%~dp0"
dotnet restore || exit /b 1
if exist publish\win-x64 rmdir /s /q publish\win-x64
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o publish\win-x64 || exit /b 1
pause
