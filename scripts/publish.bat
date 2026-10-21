@echo off
cd /d "%~dp0.."
taskkill /F /IM TwinCord.exe 2>nul
if exist publish rmdir /s /q publish
if exist publish (
  echo [ERROR] could not clean publish folder. 1>&2
  exit /b 1
)
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo [ERROR] dotnet publish failed. The stale exe may remain. 1>&2
  exit /b 1
)
del /q publish\*.xml publish\*.pdb 2>nul
