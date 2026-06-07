@echo off
chcp 65001 >nul
echo ===== 构建调试版 =====
dotnet build "%~dp0linktool.csproj" -c Debug
if %errorlevel% neq 0 (
    echo 构建失败！
    pause
    exit /b 1
)
echo.
echo 调试版构建完成：bin\Debug\net8.0-windows\linktool.exe
pause
