@echo off
setlocal
cd /d "%~dp0"
dotnet restore || exit /b 1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64 || exit /b 1
pause
