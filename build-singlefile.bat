@echo off
echo ===== Build SingleFile =====
if exist "%~dp0publish" rmdir /s /q "%~dp0publish"
dotnet publish "%~dp0linktool.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o "%~dp0publish"
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo SingleFile build done: publish\linktool.exe
pause
