@echo off
setlocal
rem ══════════════════════════════════════════════════════════════
rem  红果视频下载器 —— C# / Avalonia UI 版 构建脚本
rem
rem  用法：build.cmd            构建 Release 目录版（输出到 pkg\）
rem        build.cmd verify     ★ 发版用：构建 + 界面自检 + 端到端真下载
rem        build.cmd selftest   只跑接口自检（Debug，不打包）
rem
rem  ★★ 发版形态（2026-11-21 用户拍板）：**本地只出目录版**
rem      pkg\hgvd-downloader-vX.Y.Z\
rem      解开就是一个文件夹：exe + 运行时 dll + ffmpeg\ + README.txt
rem
rem      ★ **本地不再打 zip、不再出单文件 exe**（用户 2026-11-21 决定）：
rem        目录版启动快、ffmpeg 可替换、出问题能直接看目录里有什么；
rem        单文件塞进 ffmpeg 后是"假单文件"（仍要解到 LOCALAPPDATA），
rem        而且"exe 解出 exe"容易被杀软误判。
rem      ⇒ 本地自用直接跑 pkg\ 里的 exe 即可，不必再打包。
rem      ★ **GitHub Release 的产物不受此影响**：CI 的 release.yml 仍照旧产出
rem        zip + 单文件 exe（远端给公众下载的形态不变）。
rem
rem  ★★ 发版前必须跑 verify。理由：IL 裁剪（PublishTrimmed）的破坏是**静默**的 ——
rem     编译、启动、开窗、读收藏夹全都正常，只有点到某个功能才炸。
rem     光"能构建"不算通过，必须界面自检全绿 + 真下一集。
rem ══════════════════════════════════════════════════════════════

cd /d "%~dp0"

rem ★ 找 dotnet：DOTNET_ROOT → 常见安装位置 → PATH
set "DOTNET="
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET for /f "delims=" %%i in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%i"
if not defined DOTNET (
    echo [FAIL] dotnet not found -- please install .NET 8 SDK or set DOTNET_ROOT.
    exit /b 1
)
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

set "PROJ=src\Hgvd.Avalonia\Hgvd.Avalonia.csproj"
set "VER=1.6.0"
set "APP=hgvd-downloader-v%VER%"
set "PKG=pkg\%APP%"
set "LOGDIR=%USERPROFILE%\.hgv_tool"
set "HGVD_VERIFY="

if /i "%~1"=="selftest" goto selftest
if /i "%~1"=="verify" goto verify

:build
echo [1/3] Preparing embedded ffmpeg / ffprobe (skipped if cached)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-ffmpeg.ps1" || goto :err

echo [2/3] Restoring packages (via nuget.azure.cn mirror)
rem ★ 必须带 -p:Configuration=Release —— 两个理由：
rem   ① csproj 里的 PublishTrimmed 只在 Release 下开；
rem   ② ILLink.Tasks 这个包是 **restore 阶段**按 PublishTrimmed 决定要不要下载的。
rem      restore 默认按 Debug 评估 ⇒ 漏了这个参数就拉不到 ILLink，
rem      publish 会**静默不裁剪**（产物 89 MB 而不是 41 MB，而且一个错都不报）。
"%DOTNET%" restore "%PROJ%" -p:Configuration=Release --configfile "%~dp0NuGet.config" -r win-x64 || goto :err

echo [3/3] Publishing directory build (local product form) to %PKG%
rem ★ 目录版用 PublishSingleFile=false —— 运行时 dll 与 ffmpeg 都摊在目录里，
rem   启动快、可单独替换 ffmpeg、出问题能直接看目录里有什么。
if exist "%PKG%" rd /s /q "%PKG%"
"%DOTNET%" publish "%PROJ%" -c Release --no-restore -o "%PKG%" -p:PublishSingleFile=false -p:PkgVersion=%VER% || goto :err

echo.
echo Build output (local product form = directory build):
echo   %~dp0%PKG%\
echo.
echo   * No more zip / single-file exe locally since 2026-11-21.
echo     Just run the exe inside that directory.
echo     GitHub Release still ships zip + single-file exe (built by CI).

if defined HGVD_VERIFY goto check
goto :eof

:verify
set "HGVD_VERIFY=1"
goto build

rem ══════════════════════════════════════════════════════════════
rem  发版门禁：界面自检 + 证书链 + 下载链路（本地可控，不依赖 App 点播）
rem  ★ 验的是**目录版**（本地唯一产物形态）。
rem  ★ 两个开关都**不靠退出码**（程序是 WinExe，--uitest/--batchtest 恒返回 0），
rem    只能解析它们写出来的日志：
rem      %USERPROFILE%\.hgv_tool\uitest.log    末尾 === fail=N ===
rem      %USERPROFILE%\.hgv_tool\tlsprobe.log  末尾 === ok=1 fail=0 ===
rem      %USERPROFILE%\.hgv_tool\dlprobe.log   末尾 === ok=1 fail=0 ===
rem    所以先删旧日志，跑完再确认日志存在 —— 否则会拿上一次的结果冒充通过。
rem ══════════════════════════════════════════════════════════════
:check
echo.
echo [verify 1/3] UI self-test
if exist "%LOGDIR%\uitest.log" del /q "%LOGDIR%\uitest.log"
"%PKG%\%APP%.exe" --uitest
if not exist "%LOGDIR%\uitest.log" (
echo [FAIL] self-test wrote no log -- app did not start, or crashed on startup.
    exit /b 1
)
findstr /C:"=== fail=0 ===" "%LOGDIR%\uitest.log" >nul
if errorlevel 1 (
    echo.
echo [FAIL] UI self-test has failures, see %LOGDIR%\uitest.log
    type "%LOGDIR%\uitest.log"
    exit /b 1
)

echo.
echo [verify 2/3] MITM certificate chain (local TLS handshake)
if exist "%LOGDIR%\tlsprobe.log" del /q "%LOGDIR%\tlsprobe.log"
"%PKG%\%APP%.exe" --tlsprobe 12
if not exist "%LOGDIR%\tlsprobe.log" (
echo [FAIL] tls probe wrote no log -- app did not start, or crashed on startup.
    exit /b 1
)
findstr /C:"=== ok=1 fail=0 ===" "%LOGDIR%\tlsprobe.log" >nul
if errorlevel 1 (
    echo.
echo [FAIL] certificate chain gate failed, see %LOGDIR%\tlsprobe.log
    type "%LOGDIR%\tlsprobe.log"
    exit /b 1
)
type "%LOGDIR%\tlsprobe.log"

echo.
echo [verify 3/3] Download pipeline (local small file -> download -> ffprobe)
if exist "%LOGDIR%\dlprobe.log" del /q "%LOGDIR%\dlprobe.log"
"%PKG%\%APP%.exe" --dlprobe
if not exist "%LOGDIR%\dlprobe.log" (
echo [FAIL] dl probe wrote no log -- app did not start, or crashed on startup.
    exit /b 1
)
findstr /C:"=== ok=1 fail=0 ===" "%LOGDIR%\dlprobe.log" >nul
if errorlevel 1 (
    echo.
echo [FAIL] download pipeline gate failed, see %LOGDIR%\dlprobe.log
    type "%LOGDIR%\dlprobe.log"
    exit /b 1
)
type "%LOGDIR%\dlprobe.log"

echo.
echo === VERIFY PASSED: UI self-test + cert chain + download pipeline all green ===
echo     Directory build: %~dp0%PKG%
echo     Ready to release (bash tools/publish_release.sh v%VER%)
goto :eof

:selftest
echo [selftest] headless: shelf -> episodes -> playback url
"%DOTNET%" run --project "%PROJ%" -c Debug --no-restore -- --selftest || goto :err
goto :eof

:err
echo.
echo [FAIL] exit code %errorlevel%
exit /b %errorlevel%
