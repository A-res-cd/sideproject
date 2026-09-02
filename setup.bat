@echo off
setlocal
title WindowsTranscriber Project Setup

echo ============================================
echo     WindowsTranscriber Project Setup
echo ============================================
echo.

:: Check if .NET SDK is installed
where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] .NET SDK was not found.
    echo Install the .NET SDK first, then run this script again.
    pause
    exit /b 1
)

echo [1/8] Creating project directory...

if exist "WindowsTranscriber" (
    echo [ERROR] WindowsTranscriber directory already exists.
    echo Remove or rename it before running this script.
    pause
    exit /b 1
)

mkdir WindowsTranscriber
cd WindowsTranscriber

echo.
echo [2/8] Creating solution...

dotnet new sln -n WindowsTranscriber

set "SOLUTION_FILE="
if exist "WindowsTranscriber.slnx" set "SOLUTION_FILE=WindowsTranscriber.slnx"
if not defined SOLUTION_FILE if exist "WindowsTranscriber.sln" set "SOLUTION_FILE=WindowsTranscriber.sln"

if not defined SOLUTION_FILE (
    echo [ERROR] The .NET SDK did not create a solution file.
    goto :build_failed
)

mkdir src
mkdir tests
mkdir native
mkdir models
mkdir installer
mkdir scripts

echo.
echo [3/8] Creating .NET projects...

dotnet new wpf ^
    -n WindowsTranscriber.App ^
    -o src\WindowsTranscriber.App ^
    -f net8.0

dotnet new classlib ^
    -n WindowsTranscriber.Core ^
    -o src\WindowsTranscriber.Core ^
    -f net8.0

dotnet new classlib ^
    -n WindowsTranscriber.Audio ^
    -o src\WindowsTranscriber.Audio ^
    -f net8.0

dotnet new classlib ^
    -n WindowsTranscriber.Transcription ^
    -o src\WindowsTranscriber.Transcription ^
    -f net8.0

dotnet new classlib ^
    -n WindowsTranscriber.Data ^
    -o src\WindowsTranscriber.Data ^
    -f net8.0

dotnet new classlib ^
    -n WindowsTranscriber.Export ^
    -o src\WindowsTranscriber.Export ^
    -f net8.0

echo.
echo [4/8] Adding projects to solution...

dotnet sln "%SOLUTION_FILE%" add ^
    src\WindowsTranscriber.App\WindowsTranscriber.App.csproj

dotnet sln "%SOLUTION_FILE%" add ^
    src\WindowsTranscriber.Core\WindowsTranscriber.Core.csproj

dotnet sln "%SOLUTION_FILE%" add ^
    src\WindowsTranscriber.Audio\WindowsTranscriber.Audio.csproj

dotnet sln "%SOLUTION_FILE%" add ^
    src\WindowsTranscriber.Transcription\WindowsTranscriber.Transcription.csproj

dotnet sln "%SOLUTION_FILE%" add ^
    src\WindowsTranscriber.Data\WindowsTranscriber.Data.csproj

dotnet sln "%SOLUTION_FILE%" add ^
    src\WindowsTranscriber.Export\WindowsTranscriber.Export.csproj

echo.
echo [5/8] Adding project references...

:: App dependencies
dotnet add src\WindowsTranscriber.App reference ^
    src\WindowsTranscriber.Core

dotnet add src\WindowsTranscriber.App reference ^
    src\WindowsTranscriber.Audio

dotnet add src\WindowsTranscriber.App reference ^
    src\WindowsTranscriber.Transcription

dotnet add src\WindowsTranscriber.App reference ^
    src\WindowsTranscriber.Data

dotnet add src\WindowsTranscriber.App reference ^
    src\WindowsTranscriber.Export

:: Core references
dotnet add src\WindowsTranscriber.Audio reference ^
    src\WindowsTranscriber.Core

dotnet add src\WindowsTranscriber.Transcription reference ^
    src\WindowsTranscriber.Core

dotnet add src\WindowsTranscriber.Data reference ^
    src\WindowsTranscriber.Core

dotnet add src\WindowsTranscriber.Export reference ^
    src\WindowsTranscriber.Core

echo.
echo [6/8] Creating project folders...

:: App
mkdir src\WindowsTranscriber.App\Views
mkdir src\WindowsTranscriber.App\ViewModels
mkdir src\WindowsTranscriber.App\Controls
mkdir src\WindowsTranscriber.App\Converters
mkdir src\WindowsTranscriber.App\Services

mkdir src\WindowsTranscriber.App\Resources
mkdir src\WindowsTranscriber.App\Resources\Styles
mkdir src\WindowsTranscriber.App\Resources\Icons
mkdir src\WindowsTranscriber.App\Resources\Images

:: Core
mkdir src\WindowsTranscriber.Core\Models
mkdir src\WindowsTranscriber.Core\Interfaces
mkdir src\WindowsTranscriber.Core\Enums
mkdir src\WindowsTranscriber.Core\Constants

:: Audio
mkdir src\WindowsTranscriber.Audio\Capture
mkdir src\WindowsTranscriber.Audio\Processing
mkdir src\WindowsTranscriber.Audio\Processes
mkdir src\WindowsTranscriber.Audio\Interop

:: Transcription
mkdir src\WindowsTranscriber.Transcription\Whisper
mkdir src\WindowsTranscriber.Transcription\Processing
mkdir src\WindowsTranscriber.Transcription\Native

:: Data
mkdir src\WindowsTranscriber.Data\Database
mkdir src\WindowsTranscriber.Data\Repositories
mkdir src\WindowsTranscriber.Data\Entities

:: Native Whisper
mkdir native\whisper
mkdir native\whisper\win-x64

echo.
echo [7/8] Cleaning generated placeholder files...

if exist src\WindowsTranscriber.Core\Class1.cs (
    del src\WindowsTranscriber.Core\Class1.cs
)

if exist src\WindowsTranscriber.Audio\Class1.cs (
    del src\WindowsTranscriber.Audio\Class1.cs
)

if exist src\WindowsTranscriber.Transcription\Class1.cs (
    del src\WindowsTranscriber.Transcription\Class1.cs
)

if exist src\WindowsTranscriber.Data\Class1.cs (
    del src\WindowsTranscriber.Data\Class1.cs
)

if exist src\WindowsTranscriber.Export\Class1.cs (
    del src\WindowsTranscriber.Export\Class1.cs
)

:: Keep otherwise-empty folders in Git
type nul > models\.gitkeep
type nul > native\whisper\win-x64\.gitkeep
type nul > installer\.gitkeep
type nul > scripts\.gitkeep
type nul > tests\.gitkeep

echo.
echo [8/8] Restoring and building solution...

dotnet restore

if %ERRORLEVEL% NEQ 0 goto :build_failed

dotnet build

if %ERRORLEVEL% NEQ 0 goto :build_failed

echo.
echo ============================================
echo           SETUP COMPLETE
echo ============================================
echo.
echo Project:
echo %CD%
echo.
echo To open in VS Code:
echo     code .
echo.
echo To run the application:
echo     dotnet run --project src\WindowsTranscriber.App
echo.
echo ============================================

pause
exit /b 0


:build_failed
echo.
echo ============================================
echo             SETUP FAILED
echo ============================================
echo.
echo The project was created, but restore/build
echo encountered an error. Review the output above.
echo.
pause
exit /b 1
