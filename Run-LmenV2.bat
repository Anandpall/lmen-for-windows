@echo off
title Lmen v2 - Ambient Music Companion
cd /d "%~dp0"

:: Ensure local dotnet path is accessible
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
    set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
)

echo Starting Lmen v2...
dotnet run --project LmenV2.csproj
if %ERRORLEVEL% neq 0 (
    echo.
    echo Application exited with error code %ERRORLEVEL%.
    pause
)
