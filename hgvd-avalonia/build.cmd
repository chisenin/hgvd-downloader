@echo off
setlocal
rem ══════════════════════════════════════════════════════════════
rem  红果视频下载器 —— C# / Avalonia UI 版 构建脚本
rem  用法：build.cmd            构建 Release（输出到 ..\dist）
rem        build.cmd selftest   只构建并跑签名链/界面自检
rem ══════════════════════════════════════════════════════════════

cd /d "%~dp0"

rem ★ 找 dotnet：DOTNET_ROOT → 常见安装位置 → PATH
set "DOTNET="
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET for /f "delims=" %%i in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%i"
if not defined DOTNET (
    echo [失败] 找不到 dotnet —— 请安装 .NET 8 SDK，或设置 DOTNET_ROOT 后重试。
    exit /b 1
)
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

set "PROJ=src\Hgvd.Avalonia\Hgvd.Avalonia.csproj"
set "OUT=dist"
set "VER=1.0.0"

if /i "%~1"=="selftest" goto selftest

echo [1/3] 还原依赖（走 nuget.azure.cn 镜像）
"%DOTNET%" restore "%PROJ%" --configfile "%~dp0NuGet.config" -r win-x64 || goto :err

echo [2/3] 发布 Release
"%DOTNET%" publish "%PROJ%" -c Release --no-restore -o "%OUT%" -p:PkgVersion=%VER% || goto :err

echo [3/3] 产物
dir /b "%OUT%"
echo.
echo 完成：%~dp0%OUT%\hgvd-downloader-v%VER%.exe
goto :eof

:selftest
echo [自检] 无头跑一遍收藏夹 / 分集 / 播放地址
"%DOTNET%" run --project "%PROJ%" -c Debug --no-restore -- --selftest || goto :err
goto :eof

:err
echo.
echo [失败] 错误码 %errorlevel%
exit /b %errorlevel%
