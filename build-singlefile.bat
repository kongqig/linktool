@echo off
chcp 65001 >nul
echo ===== 构建单文件版 =====
if exist "%~dp0publish" rmdir /s /q "%~dp0publish"
dotnet publish "%~dp0linktool.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o "%~dp0publish"
if %errorlevel% neq 0 (
    echo 构建失败！
    pause
    exit /b 1
)
echo.
echo 单文件版构建完成：publish\linktool.exe
pause
