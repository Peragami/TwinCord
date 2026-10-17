@echo off
cd /d "%~dp0.."
taskkill /F /IM TwinCord.exe 2>nul
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish-selfcontained
if errorlevel 1 (
  echo [ERROR] dotnet publish failed. The stale exe may remain. 1>&2
  exit /b 1
)
del /q publish-selfcontained\*.xml publish-selfcontained\*.pdb 2>nul
