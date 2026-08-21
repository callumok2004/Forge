@echo off
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

dotnet build "%~dp0Forge.App\Forge.App.csproj" -c %CONFIG% --nologo

if errorlevel 1 (
    echo.
    echo *** Build failed. ***
    exit /b 1
)

endlocal
