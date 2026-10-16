@echo off
taskkill /F /IM TwinCord.exe 2>nul
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish-selfcontained
del /q publish-selfcontained\*.xml publish-selfcontained\*.pdb 2>nul
