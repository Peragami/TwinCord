@echo off
rem Release build: folder layout + TwinCord.zip (portable, self-contained, no .NET needed)
cd /d "%~dp0.."
taskkill /F /IM TwinCord.exe 2>nul
rem 1) Build the release files into a clean staging folder. User data never exists here,
rem    so the zip can never contain it (settings, logs, Discord login data).
if exist publish-zip rmdir /s /q publish-zip
dotnet publish -c Release -r win-x64 --self-contained true -o publish-zip
if errorlevel 1 (
  echo [ERROR] dotnet publish failed. 1>&2
  exit /b 1
)
del /q publish-zip\*.xml publish-zip\*.pdb 2>nul
mkdir publish-zip\data 2>nul
copy /y scripts\data-readme.txt publish-zip\data\README.txt >nul
rem 2) Zip the staging folder. This zip is the release asset (TwinCord.zip).
if exist TwinCord.zip del /q TwinCord.zip
tar -a -c -f TwinCord.zip -C publish-zip .
if errorlevel 1 (
  echo [ERROR] zip creation failed. 1>&2
  exit /b 1
)
rem 3) Refresh the local run folder (publish-portable) the same way the in-app updater does.
rem    data\ and mods\ are excluded on both sides, so user data survives.
robocopy publish-zip publish-portable /MIR /XD "%CD%\publish-zip\data" "%CD%\publish-zip\mods" "%CD%\publish-portable\data" "%CD%\publish-portable\mods" /R:3 /W:1 /NFL /NDL /NJH /NJS >nul
if %ERRORLEVEL% GEQ 8 (
  echo [ERROR] run folder sync failed. See publish-zip. 1>&2
  exit /b 1
)
rmdir /s /q publish-zip
echo [OK] TwinCord.zip is ready to attach to the release.
