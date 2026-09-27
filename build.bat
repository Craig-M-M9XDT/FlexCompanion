@echo off
setlocal
REM Builds a single-file FlexCompanion.exe into .\publish next to this script.
REM Requires the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

REM Always work from the folder this script lives in, however it was started.
cd /d "%~dp0"

if not exist "FlexCompanion.csproj" (
    echo.
    echo  FlexCompanion.csproj was not found next to build.bat.
    echo  Current folder: %CD%
    echo.
    echo  If you double-clicked build.bat inside the zip, Windows only copied that one
    echo  file to a temp folder. Right-click FlexCompanion.zip ^> Extract All..., then
    echo  run build.bat from the extracted FlexCompanion folder.
    echo.
    pause
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo  The .NET SDK was not found. Install the .NET 8 SDK ^(not just the runtime^) from
    echo  https://dotnet.microsoft.com/download/dotnet/8.0 and run this again.
    echo.
    pause
    exit /b 1
)

dotnet publish "FlexCompanion.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0publish"
if errorlevel 1 (
    echo.
    echo  Build failed - please copy the errors above and send them over.
    pause
    exit /b 1
)

echo.
echo  Done: %~dp0publish\FlexCompanion.exe
pause
