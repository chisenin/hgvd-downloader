@echo off
setlocal
rem ══════════════════════════════════════════════════════════════
rem  红果视频下载器 —— C# / Avalonia UI 版 构建脚本
rem
rem  用法：build.cmd            构建 Release（输出到 dist\ 与 pkg\）
rem        build.cmd verify     ★ 发版用：构建 + 界面自检 + 端到端真下载
rem        build.cmd selftest   只跑接口自检（Debug，不打包）
rem
rem  ★★ 发版形态：**B 案 —— 自包含目录版 + zip**（主推）
rem      dist\hgvd-downloader-vX.Y.Z-win-x64.zip
rem      解开就是一个文件夹：exe + 运行时 dll + ffmpeg\ + README.txt
rem      同时仍产出单文件 exe 作备用（dist\hgvd-downloader-vX.Y.Z.exe）。
rem      为什么主推 zip 见 docs/03 §3.6 与仓库 issue 讨论：
rem      单文件塞进 ffmpeg 后是"假单文件"（仍要解到 LOCALAPPDATA），
rem      而且"exe 解出 exe"容易被杀软误判。
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
    echo [失败] 找不到 dotnet —— 请安装 .NET 8 SDK，或设置 DOTNET_ROOT 后重试。
    exit /b 1
)
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

rem ★ 打包 zip 用 Python（tools\pack_zip.py）—— 比 Compress-Archive 快一个数量级
set "PY="
for /f "delims=" %%i in ('where python 2^>nul') do if not defined PY set "PY=%%i"
if not defined PY (
    echo [失败] 找不到 python —— 打 zip 需要它（tools\pack_zip.py）。
    exit /b 1
)

set "PROJ=src\Hgvd.Avalonia\Hgvd.Avalonia.csproj"
set "OUT=dist"
set "VER=1.3.0"
set "APP=hgvd-downloader-v%VER%"
set "PKG=pkg\%APP%"
set "ZIP=%OUT%\%APP%-win-x64.zip"
set "LOGDIR=%USERPROFILE%\.hgv_tool"
set "HGVD_VERIFY="

if /i "%~1"=="selftest" goto selftest
if /i "%~1"=="verify" goto verify

:build
echo [1/5] 准备内置 ffmpeg / ffprobe（已备好则秒过）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-ffmpeg.ps1" || goto :err

echo [2/5] 还原依赖（走 nuget.azure.cn 镜像）
rem ★ 必须带 -p:Configuration=Release —— 两个理由：
rem   ① csproj 里的 PublishTrimmed 只在 Release 下开；
rem   ② ILLink.Tasks 这个包是 **restore 阶段**按 PublishTrimmed 决定要不要下载的。
rem      restore 默认按 Debug 评估 ⇒ 漏了这个参数就拉不到 ILLink，
rem      publish 会**静默不裁剪**（产物 89 MB 而不是 41 MB，而且一个错都不报）。
"%DOTNET%" restore "%PROJ%" -p:Configuration=Release --configfile "%~dp0NuGet.config" -r win-x64 || goto :err

echo [3/5] 发布目录版（B 案主推，输出到 %PKG%）
rem ★ 目录版和单文件**必须分两次 publish**：PublishSingleFile 是 publish 期属性。
if exist "%PKG%" rd /s /q "%PKG%"
"%DOTNET%" publish "%PROJ%" -c Release --no-restore -o "%PKG%" -p:PublishSingleFile=false -p:PkgVersion=%VER% || goto :err

echo [4/5] 打 zip（顶层保留文件夹名 + 放入 README.txt）
"%PY%" "%~dp0..\tools\pack_zip.py" "%PKG%" "%ZIP%" "%~dp0PACK-README.txt" || goto :err

echo [5/5] 发布单文件（备用形态，输出到 %OUT%）
"%DOTNET%" publish "%PROJ%" -c Release --no-restore -o "%OUT%" -p:PkgVersion=%VER% || goto :err

echo.
echo 产物：
dir /b "%OUT%"
echo.
echo   目录版：%~dp0%PKG%\  （已打包成 %ZIP%）
echo   单文件：%~dp0%OUT%\%APP%.exe

if defined HGVD_VERIFY goto check
goto :eof

:verify
set "HGVD_VERIFY=1"
goto build

rem ══════════════════════════════════════════════════════════════
rem  发版门禁：界面自检 + 端到端真下载
rem  ★ 验的是**目录版**（B 案主推的那个），不是单文件 ——
rem    两者是两次独立 publish，验主推形态才有意义。
rem  ★ 两个开关都**不靠退出码**（程序是 WinExe，--uitest/--batchtest 恒返回 0），
rem    只能解析它们写出来的日志：
rem      %USERPROFILE%\.hgv_tool\uitest.log    末尾 === fail=N ===
rem      %USERPROFILE%\.hgv_tool\batchtest.log 末尾 === ok=N fail=M ===
rem    所以先删旧日志，跑完再确认日志存在 —— 否则会拿上一次的结果冒充通过。
rem ══════════════════════════════════════════════════════════════
:check
echo.
echo [验证 1/2] 界面自检
if exist "%LOGDIR%\uitest.log" del /q "%LOGDIR%\uitest.log"
"%PKG%\%APP%.exe" --uitest
if not exist "%LOGDIR%\uitest.log" (
    echo [失败] 自检没写出日志 —— 程序没起来，或启动就崩了。
    exit /b 1
)
findstr /C:"=== fail=0 ===" "%LOGDIR%\uitest.log" >nul
if errorlevel 1 (
    echo.
    echo [失败] 界面自检有失败项，详见 %LOGDIR%\uitest.log
    type "%LOGDIR%\uitest.log"
    exit /b 1
)

echo.
echo [验证 2/2] 端到端真下载（1 部 · 最多 2 集）
if exist "%LOGDIR%\batchtest.log" del /q "%LOGDIR%\batchtest.log"
"%PKG%\%APP%.exe" --batchtest 1
if not exist "%LOGDIR%\batchtest.log" (
    echo [失败] 批量测试没写出日志 —— 程序没起来，或启动就崩了。
    exit /b 1
)
rem ★ 注意这里的方向：找到 "=== ok=0" 才是**失败**
findstr /C:"=== ok=0" "%LOGDIR%\batchtest.log" >nul
if not errorlevel 1 (
    echo.
    echo [失败] 一集都没下下来，详见 %LOGDIR%\batchtest.log
    type "%LOGDIR%\batchtest.log"
    exit /b 1
)
type "%LOGDIR%\batchtest.log"

echo.
echo ═══ 验证通过：界面自检全绿 + 端到端下载成功 ═══
echo     目录版：%~dp0%PKG%\
echo     压缩包：%~dp0%ZIP%
echo     可以发版了（bash tools/publish_release.sh v%VER%）
goto :eof

:selftest
echo [自检] 无头跑一遍收藏夹 / 分集 / 播放地址
"%DOTNET%" run --project "%PROJ%" -c Debug --no-restore -- --selftest || goto :err
goto :eof

:err
echo.
echo [失败] 错误码 %errorlevel%
exit /b %errorlevel%
