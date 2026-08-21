@echo off
setlocal

set CONFIG=Release
set RUNTIME=win-x64
set OUTPUT=%~dp0artifacts\publish\%RUNTIME%

if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"

echo.
echo === Publishing (%CONFIG%, %RUNTIME%, self-contained) ===
dotnet publish "%~dp0Forge.App\Forge.App.csproj" ^
    -c %CONFIG% ^
    -r %RUNTIME% ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -p:DebugType=none ^
    -o "%OUTPUT%"

if errorlevel 1 (
    echo.
    echo *** Publish failed. ***
    exit /b 1
)

echo === Done ===

endlocal
