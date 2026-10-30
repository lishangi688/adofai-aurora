# Aurora —— 一键打包（发布版 + 安装包 + 资源站用合并包）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.2
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.2 -Installer
#
# 产物：
#   dist\Aurora\                       绿色版（解压即用）
#   dist\Aurora-v{版本}-win-x64.zip    绿色版压缩包（GitHub 发布用）
#   dist\installer\Aurora-Setup-{版本}.exe   安装包（GitHub 发布用，需要 -Installer）
#   dist\Aurora-{版本}-all.zip                        资源站用「合并包」（需要 -Installer）
#       结构：portable\（绿色版文件） + Setup.exe + 说明.txt + update.json
#       原因：资源站一个版本只能挂一个 zip，所以把两种形态打在一起；
#             客户端按自己的形态取用（绿色版用 portable\，安装版取 Setup.exe）。

param(
    [string]$Version = "0.1.11",
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# PowerShell 5.1 里用 ZipFile / ZipArchiveMode 需要先加载这两个程序集
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 打 zip 的辅助函数。
# 为什么不用 Compress-Archive / ZipFile.CreateFromDirectory：
# 它们在 PowerShell 5.1（.NET Framework）下会用反斜杠做 zip 内部路径分隔符，
# 不符合 zip 规范 —— 有的解压工具会把 "portable\a.dll" 当成一个带反斜杠的文件名。
# 这里手动写入条目，强制用 "/" 分隔 + UTF-8 文件名。
function New-ZipArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$ZipPath
    )

    $source = (Resolve-Path $SourceDir).Path.TrimEnd('\', '/')
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

    $zip = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem $source -Recurse -File)) {
            $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/') -replace '\\', '/'
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $fileStream = [IO.File]::OpenRead($file.FullName)
            try {
                $fileStream.CopyTo($entryStream)
            }
            finally {
                $fileStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

Write-Host "==> 1/3 发布自包含版本 (v$Version)" -ForegroundColor Cyan
if (Test-Path 'dist\Aurora') { Remove-Item 'dist\Aurora' -Recurse -Force }
dotnet publish 'src\AdofaiModManager\AdofaiModManager.csproj' `
    -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:Version=$Version `
    -o 'dist\Aurora'
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host "==> 2/3 生成绿色版 zip" -ForegroundColor Cyan
$zip = "dist\Aurora-v$Version-win-x64.zip"
Get-ChildItem 'dist' -Filter '*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force
New-ZipArchive -SourceDir 'dist\Aurora' -ZipPath (Join-Path (Get-Location) $zip)
Write-Host "    $zip"

# 开发阶段默认只出绿色版；需要安装包时加 -Installer
if (-not $Installer) {
    Write-Host ''
    Write-Host '完成 ✅（本次只生成绿色版；需要安装包请加 -Installer）' -ForegroundColor Green
    Get-ChildItem 'dist' -Filter '*.zip' | ForEach-Object { "  绿色版: {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB) }
    exit 0
}

Write-Host "==> 3/3 生成安装包 (Inno Setup)" -ForegroundColor Cyan
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning '未找到 ISCC.exe（Inno Setup）。请先安装：winget install JRSoftware.InnoSetup'
    Write-Warning '绿色版已生成，安装包跳过。'
    exit 0
}

# 先清掉旧版本的安装包：否则合并包那里按通配符取"第一个"时，
# 可能拿到上一个版本留下的 exe（踩过一次：0.1.3 的合并包里混进了 0.1.2 的安装包）
Get-ChildItem 'dist\installer' -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force

& $iscc "installer\setup.iss" "/DAppVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw 'ISCC 编译失败' }

Write-Host ''
Write-Host "==> 4/4 生成资源站用「合并包」" -ForegroundColor Cyan

# 结构：ADOFAI Mod Manager\ + Setup.exe + 说明.txt + update.json
# 绿色版客户端解压「ADOFAI Mod Manager」文件夹即可；安装版客户端取 Setup.exe 运行。
# 文件夹刻意用产品全名，方便用户从资源站下载后直接拖到自己想放的位置。
$portableFolder = 'Aurora'
$stage = "dist\_allinone"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path "$stage\$portableFolder" -Force | Out-Null

Copy-Item 'dist\Aurora\*' "$stage\$portableFolder" -Recurse -Force
# 只取"本次版本"的安装包（不要用通配符随便取第一个）
$setupExe = Get-ChildItem "dist\installer\Aurora-Setup-$Version.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $setupExe) { throw "找不到本版本的安装包：dist\installer\Aurora-Setup-$Version.exe" }
Copy-Item $setupExe.FullName "$stage\$($setupExe.Name)" -Force

$readme = @"
Aurora v$Version

这个压缩包里同时包含两种形态，按你的需要取用：

【绿色版（解压即用）】
  把「$portableFolder」文件夹整个拖到你想要的位置即可，双击里面的
  Aurora.exe 就能用。（也可以把它里面的文件覆盖到已有的程序目录来更新。）

【安装版】
  直接运行 $($setupExe.Name)，按提示安装（会覆盖旧版本，设置与收藏保留）。

发布页：https://github.com/lishangi688/adofai-aurora/releases
"@
[IO.File]::WriteAllText("$stage\说明.txt", $readme, (New-Object System.Text.UTF8Encoding $false))

# 给自动更新用的元信息（客户端据此确认"这就是我要的版本"、并知道绿色版文件夹叫什么）
$manifest = @{
    name       = 'Aurora'
    version    = $Version
    portable   = $portableFolder
    installer  = $setupExe.Name
    builtAt    = (Get-Date).ToString('s')
} | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText("$stage\update.json", $manifest, (New-Object System.Text.UTF8Encoding $false))

$allZip = "dist\Aurora-$Version-all.zip"
New-ZipArchive -SourceDir $stage -ZipPath (Join-Path (Get-Location) $allZip)
Remove-Item $stage -Recurse -Force

Write-Host ''
Write-Host '完成 ✅' -ForegroundColor Green
Get-ChildItem 'dist\installer' -Filter '*.exe' | ForEach-Object { "  安装包(GitHub): {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB) }
Get-ChildItem 'dist' -Filter '*.zip' | ForEach-Object { "  压缩包: {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB) }
