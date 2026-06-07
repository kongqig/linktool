@echo off
chcp 65001 >nul
echo ===== 构建发行版 =====
dotnet build "%~dp0linktool.csproj" -c Release
if %errorlevel% neq 0 (
    echo 构建失败！
    pause
    exit /b 1
)
echo.
echo 发行版构建完成：bin\Release\net8.0-windows\linktool.exe
pause
