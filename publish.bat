@echo off
taskkill /F /IM TwinCord.exe 2>nul
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish
del /q publish\*.xml publish\*.pdb 2>nul
