@echo off
echo ===== Build Debug =====
dotnet build "%~dp0linktool.csproj" -c Debug
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo Debug build done: bin\Debug\net8.0-windows\linktool.exe
pause
