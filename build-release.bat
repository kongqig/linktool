@echo off
echo ===== Build Release =====
dotnet build "%~dp0linktool.csproj" -c Release
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo Release build done: bin\Release\net8.0-windows\linktool.exe
pause
