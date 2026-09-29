@echo off
setlocal
cd /d "%~dp0"
dotnet restore || exit /b 1
if exist publish\win-x64 rmdir /s /q publish\win-x64
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o publish\win-x64 || exit /b 1
del /s /q publish\win-x64\*.pdb >nul 2>&1
pause
