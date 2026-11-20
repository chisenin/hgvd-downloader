# ────────────────────────────────────────────────────────────────
#  准备内置的 ffmpeg / ffprobe（供单文件 exe 打包）
#
#  做什么：
#    1) 从 npmmirror（淘宝镜像，国内快）取官方打好的 Windows 版
#       @ffmpeg-installer/win32-x64 与 @ffprobe-installer/win32-x64；
#    2) 解出 ffmpeg.exe / ffprobe.exe 放到仓库根 .cache/ffmpeg/；
#    3) 用 gzip 压成 ffmpeg.exe.gz / ffprobe.exe.gz，交给 csproj 当 EmbeddedResource。
#
#  产物（都进 .gitignore，不进仓库）：
#    .cache/ffmpeg/ffmpeg.exe.gz   （约 25 MB）
#    .cache/ffmpeg/ffprobe.exe.gz  （约 16 MB）
#
#  用法：powershell -NoProfile -ExecutionPolicy Bypass -File prepare-ffmpeg.ps1
#        （已备好时秒过；加 -Force 可强制重新下载/压缩）
# ────────────────────────────────────────────────────────────────
param([switch]$Force)

$ErrorActionPreference = 'Stop'

$here  = Split-Path -Parent $MyInvocation.MyCommand.Path          # hgvd-avalonia/
$root  = Split-Path -Parent $here                                  # 仓库根
$cache = Join-Path $root '.cache\ffmpeg'

# ★ 版本固定：ffmpeg / ffprobe 都是 4.1.0（同一批构建，行为一致）
$pkgs = @(
    @{ Name = 'ffmpeg';  Url = 'https://registry.npmmirror.com/@ffmpeg-installer/win32-x64/-/win32-x64-4.1.0.tgz' },
    @{ Name = 'ffprobe'; Url = 'https://registry.npmmirror.com/@ffprobe-installer/win32-x64/-/win32-x64-4.1.0.tgz' }
)

New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Compress-Exe([string]$src, [string]$dst) {
    $tmp = "$dst.tmp"
    $in  = [System.IO.File]::OpenRead($src)
    try {
        $out = [System.IO.File]::Create($tmp)
        try {
            $gz = New-Object System.IO.Compression.GZipStream(
                $out, [System.IO.Compression.CompressionLevel]::Optimal, $true)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
    Move-Item -LiteralPath $tmp -Destination $dst -Force
}

# ── 已经压好就跳过 ──
$gz = @{}
foreach ($p in $pkgs) { $gz[$p.Name] = Join-Path $cache "$($p.Name).exe.gz" }
$allDone = $true
foreach ($p in $pkgs) { if (-not (Test-Path $gz[$p.Name])) { $allDone = $false } }
if (-not $Force -and $allDone) {
    Write-Host "[ffmpeg] 已内置：$cache"
    exit 0
}

foreach ($p in $pkgs) {
    $exe = Join-Path $cache "$($p.Name).exe"

    # ── 1. 取包并解出 exe ──
    if ($Force -or -not (Test-Path $exe)) {
        $tgz = Join-Path $cache "$($p.Name).tgz"
        if ($Force -or -not (Test-Path $tgz)) {
            Write-Host "[ffmpeg] 下载 $($p.Name)（包约 16-22 MB）…"
            $ProgressPreference = 'SilentlyContinue'
            & curl.exe -L --retry 6 --retry-delay 3 -s -o $tgz $p.Url
            if ($LASTEXITCODE -ne 0) { throw "下载失败（curl 退出码 $LASTEXITCODE）：$($p.Url)" }
        }

        Write-Host ("[ffmpeg] 解开 $($p.Name).tgz（{0:0.1} MB）…" -f ((Get-Item $tgz).Length / 1MB))
        $tmpDir = Join-Path $cache "x-$($p.Name)"
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
        # ★ Windows 10+ 自带 tar.exe（bsdtar），Git Bash / MSYS 里也有
        $tar = (Get-Command tar.exe -ErrorAction SilentlyContinue).Source
        if (-not $tar) { $tar = (Get-Command tar -ErrorAction SilentlyContinue).Source }
        if (-not $tar) { throw "找不到 tar（Windows 10+ 自带 tar.exe）" }
        & $tar -xzf $tgz -C $tmpDir
        if ($LASTEXITCODE -ne 0) { throw "解压 tar 失败（退出码 $LASTEXITCODE）" }

        $found = Get-ChildItem $tmpDir -Recurse -Filter "$($p.Name).exe" | Select-Object -First 1
        if (-not $found) { throw "tgz 里找不到 $($p.Name).exe" }
        Move-Item -LiteralPath $found.FullName -Destination $exe -Force
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ("[ffmpeg] {0}.exe {1:0.1} MB" -f $p.Name, ((Get-Item $exe).Length / 1MB))
    }

    # ── 2. gzip ──
    if ($Force -or -not (Test-Path $gz[$p.Name])) {
        Compress-Exe $exe $gz[$p.Name]
        Write-Host ("[ffmpeg] {0}.exe.gz {1:0.1} MB" -f $p.Name, ((Get-Item $gz[$p.Name]).Length / 1MB))
    }
}

Write-Host '[ffmpeg] 完成'
