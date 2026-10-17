@echo off
cd /d "%~dp0.."
taskkill /F /IM TwinCord.exe 2>nul
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo [ERROR] dotnet publish failed. The stale exe may remain. 1>&2
  exit /b 1
)
del /q publish\*.xml publish\*.pdb 2>nul
