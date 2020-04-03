@echo off
del packages\*.nupkg
for /f %%f in ('dir /b *.nuspec') do nuget pack %%f -OutputDirectory packages
pause
