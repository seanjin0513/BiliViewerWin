# =============================================================================
# BiliViewer Windows 版「绿色文件夹」构建脚本
#
#   1. 定位 dotnet（PATH 优先，其次 %LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe）
#   2. 生成应用图标 Assets\AppIcon.ico（多尺寸 16/24/32/48/64/128/256）
#   3. dotnet publish 到 dist\BiliViewer（默认自包含 + 绿色目录，非单文件）
#   4. 清理 *.pdb
#   5. 打印产物目录、exe 路径、总大小、文件数
#   6. -Installer 时用 makensis 打 dist\BiliViewer-Setup-1.0.0.exe
#      （默认 per-user 免管理员；加 -PerMachineInstaller 才打需要管理员的 per-machine 版）
#
# 用法：
#   pwsh -File .\build.ps1                      # 完整构建（自包含，约 150+ MB）
#   pwsh -File .\build.ps1 -FrameworkDependent  # 框架依赖（体积小，需装 .NET 8 运行时）
#   pwsh -File .\build.ps1 -SkipIcon            # 复用已有 Assets\AppIcon.ico
#   pwsh -File .\build.ps1 -NoPublish           # 只生成图标，不发布
#   pwsh -File .\build.ps1 -Installer           # 完整构建 + per-user 安装包（出货用，不弹 UAC）
#   pwsh -File .\build.ps1 -Installer -PerMachineInstaller   # 额外打 per-machine 版（需管理员）
#
# 说明：默认 --self-contained true，产物自带 .NET 运行时，
#       目标机器无需安装任何 .NET，解压后双击 dist\BiliViewer\BiliViewer.exe 即可运行。
#
# -Installer 需要 makensis（NSIS 3.x）。查找顺序：
#   %TEMP%\nsis\nsis-*\makensis.exe  →  PATH  →  Program Files\NSIS\makensis.exe
# 都没有时会抛错并提示「免管理员」的下载解压办法（详见 installer\README.md）。
#
# 安装包两种口径（同一份 .nsi，靠 -DPERMACHINE 切换）：
#   per-user（默认，出货）：RequestExecutionLevel user、卸载项写 HKCU、
#                           快捷方式只放当前用户、默认目录写不进 D: 时回退 %LOCALAPPDATA%\Programs\BiliViewer
#   per-machine（可选）：  RequestExecutionLevel admin、卸载项写 HKLM、所有用户快捷方式、
#                           回退 %ProgramFiles%\BiliViewer —— 需要管理员权限，本机当前登录令牌不含
#                           Administrators 组（UAC 只会要求输入管理员凭据），所以出货包必须是 per-user。
# =============================================================================

[CmdletBinding()]
param(
    # 跳过图标生成，直接复用已有的 Assets\AppIcon.ico
    [switch]$SkipIcon,
    # 只生成图标，不做发布
    [switch]$NoPublish,
    # 改成框架依赖发布（--self-contained false），产物小但目标机器需 .NET 8 Desktop Runtime
    [switch]$FrameworkDependent,
    # 额外用 NSIS 打安装包（默认 per-user 免管理员版 dist\BiliViewer-Setup-1.0.0.exe）
    [switch]$Installer,
    # 打安装包时改用 per-machine 口径（-DPERMACHINE，需要管理员权限），产物带 -permachine 后缀
    [switch]$PerMachineInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- 路径 -------------------------------------------------------------------
$root          = $PSScriptRoot
$projectPath   = Join-Path $root 'BiliViewerWin.csproj'
$toolProject   = Join-Path $root 'tools\MakeIcon'
$icoPath       = Join-Path $root 'Assets\AppIcon.ico'
$distRoot      = Join-Path $root 'dist'
$distDir       = Join-Path $distRoot 'BiliViewer'
$exePath       = Join-Path $distDir 'BiliViewer.exe'
$installerDir  = Join-Path $root 'installer'
# 安装包文件名不在这里写死：由 New-Installer 按 per-user / per-machine 决定
#   BiliViewer-Setup-1.0.0.exe            （默认，per-user 免管理员）
#   BiliViewer-Setup-1.0.0-permachine.exe （-PerMachineInstaller，需要管理员）

$dotnetExe     = $null

# ---- 辅助函数 ---------------------------------------------------------------

<#
    .SYNOPSIS  找 makensis.exe（NSIS 编译器），找不到返回 $null。
    查找顺序：缓存里的绿色版 → PATH → 常规安装位置
#>
function Resolve-Makensis {
    # 1) 免管理员绿色版（installer\README.md 约定的位置：%TEMP%\nsis\nsis-<ver>\makensis.exe）
    $cacheDir = Join-Path $env:TEMP 'nsis'
    if (Test-Path -LiteralPath $cacheDir) {
        $hit = Get-ChildItem -LiteralPath $cacheDir -Recurse -File -Filter 'makensis.exe' -ErrorAction SilentlyContinue |
               Sort-Object -Property FullName | Select-Object -First 1
        if ($null -ne $hit) { return $hit.FullName }
    }
    # 2) PATH
    $onPath = Get-Command makensis -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $onPath) { return $onPath.Source }
    # 3) 常规安装位置
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'),
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

<#
    .SYNOPSIS  用 NSIS 生成安装包
               默认 per-user 免管理员版 -> dist\BiliViewer-Setup-1.0.0.exe
               -PerMachine 时打 per-machine 版 -> dist\BiliViewer-Setup-1.0.0-permachine.exe
#>
function New-Installer {
    param([switch]$PerMachine)

    # 安装口径（与 installer\BiliViewer.nsi 里的 !ifdef PERMACHINE 保持一致）
    if ($PerMachine) {
        $scopeText = 'per-machine（所有用户 / 写 HKLM / 快捷方式放公共菜单 / 安装需要管理员权限）'
        $outName   = 'BiliViewer-Setup-1.0.0-permachine.exe'
        $defines   = @('-DPERMACHINE')
    }
    else {
        $scopeText = 'per-user（仅当前用户 / 写 HKCU / 快捷方式放当前用户 / 全程不弹 UAC）'
        $outName   = 'BiliViewer-Setup-1.0.0.exe'
        $defines   = @()
    }
    $outPath = Join-Path $distRoot $outName

    $nsiScript = Join-Path $installerDir 'BiliViewer.nsi'
    if (-not (Test-Path -LiteralPath $nsiScript)) {
        throw "找不到安装脚本: $nsiScript"
    }
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "找不到发布产物 $exePath，无法打安装包（先不要用 -NoPublish 跑一次完整构建）"
    }

    $makensis = Resolve-Makensis
    if ($null -eq $makensis) {
        throw @"
找不到 makensis（NSIS 编译器），无法生成安装包。请任选一种方式：
  A. 免管理员绿色版（推荐，与项目约定一致）：
       curl -L -o "$env:TEMP\nsis.zip" https://github.com/tauri-apps/binary-releases/releases/download/nsis-3.11/nsis-3.11.zip
       展开 "$env:TEMP\nsis.zip" 到 "$env:TEMP\nsis"
     然后重新运行 build.ps1 -Installer（脚本会自动找到 %TEMP%\nsis\nsis-*\makensis.exe）。
  B. 安装 NSIS 3.x 到默认位置，或把 makensis.exe 放进 PATH。
详细步骤见 installer\README.md。
"@
    }

    Write-Host "[6/6] 生成安装包 -> $outPath"
    Write-Host "      安装口径: $scopeText"
    Write-Host "      makensis: $makensis"
    if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force }

    # makensis 的相对路径按当前目录解析，所以切到 installer 目录执行
    Push-Location -LiteralPath $installerDir
    try {
        $log  = & $makensis /INPUTCHARSET UTF8 @defines 'BiliViewer.nsi' 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    $warns = @($log | Where-Object { "$_" -match 'warning' })
    if ($code -ne 0) {
        $log | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        throw "makensis 失败（退出码 $code），详见上面的输出"
    }
    if ($warns.Count -gt 0) {
        Write-Host "      [警告] makensis 输出 $($warns.Count) 条警告：" -ForegroundColor Yellow
        $warns | ForEach-Object { Write-Host "        $_" -ForegroundColor Yellow }
    }
    else {
        Write-Host "      makensis 0 warning"
    }

    if (-not (Test-Path -LiteralPath $outPath)) {
        throw "makensis 执行完毕但没有生成安装包: $outPath"
    }
    $size = (Get-Item -LiteralPath $outPath).Length
    Write-Host "      安装包: $outPath"
    Write-Host ("      安装包大小: {0:N1} MB ({1:N0} 字节)" -f ($size / 1MB), $size)
}

try {
    # -------------------------------------------------------------------------
    # 步骤 1：定位 dotnet（PATH 优先，其次用户目录下的 SDK 安装）
    # -------------------------------------------------------------------------
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $onPath) {
        $dotnetExe = $onPath.Source
    }
    else {
        $fallback = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $fallback) {
            $dotnetExe = $fallback
        }
        else {
            throw "找不到 dotnet：PATH 中没有 dotnet，且 '$fallback' 不存在。请安装 .NET 8 SDK。"
        }
    }
    Write-Host "[1/6] dotnet: $dotnetExe"

    # 减少 CLI 噪音
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    # -------------------------------------------------------------------------
    # 步骤 2：生成图标
    # -------------------------------------------------------------------------
    if ($SkipIcon) {
        if (-not (Test-Path -LiteralPath $icoPath)) {
            throw "使用了 -SkipIcon，但图标不存在: $icoPath （去掉 -SkipIcon 重新运行即可生成）"
        }
        Write-Host "[2/6] 跳过图标生成，复用: $icoPath"
    }
    else {
        Write-Host "[2/6] 生成图标 -> $icoPath"
        & $dotnetExe run --project $toolProject -c Release -- $icoPath
        if ($LASTEXITCODE -ne 0) {
            throw "图标生成失败（dotnet run 退出码 $LASTEXITCODE）"
        }
        if (-not (Test-Path -LiteralPath $icoPath)) {
            throw "图标工具执行完毕但没有产出文件: $icoPath"
        }
    }

    if ($NoPublish) {
        Write-Host "[3/6] 跳过发布（-NoPublish）"
        Write-Host "[4/6] 跳过清理"
        Write-Host "[5/6] 跳过统计"
        if ($Installer) {
            Write-Host "      （-NoPublish + -Installer：用现有 dist\BiliViewer 打包）"
            New-Installer -PerMachine:$PerMachineInstaller
        }
        else {
            Write-Host "[6/6] 跳过安装包（未指定 -Installer）"
        }
        Write-Host "构建完成" -ForegroundColor Green
        return
    }

    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "找不到主项目文件: $projectPath"
    }

    # -------------------------------------------------------------------------
    # 步骤 3：发布到绿色目录（默认自包含，非单文件）
    # -------------------------------------------------------------------------
    $selfContained = -not $FrameworkDependent
    $selfContainedArg = if ($selfContained) { 'true' } else { 'false' }

    # 先清空旧的发布目录，避免残留旧版本 dll 混进去
    if (Test-Path -LiteralPath $distDir) {
        $resolved = (Resolve-Path -LiteralPath $distDir).Path
        if (-not $resolved.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "安全校验失败：拒绝删除不在 dist 目录下的路径 '$resolved'"
        }
        Write-Host "      清理旧产物: $resolved"
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    $modeText = if ($selfContained) { '自包含（自带 .NET 运行时）' } else { '框架依赖（需 .NET 8 运行时）' }
    Write-Host "[3/6] dotnet publish -> $distDir  [$modeText]"
    & $dotnetExe publish $projectPath -c Release -r win-x64 --self-contained $selfContainedArg -o $distDir
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败（dotnet publish 退出码 $LASTEXITCODE）"
    }
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "发布后找不到可执行文件: $exePath"
    }

    # 图标补齐：csproj 的 Content 只把 ico 拷到产物里的 Assets\ 下，
    # 而主程序是按 AppContext.BaseDirectory\AppIcon.ico（产物根目录）加载窗口图标的，
    # 所以这里再补一份到 dist\BiliViewer\AppIcon.ico。
    $iconInDist = Join-Path $distDir 'AppIcon.ico'
    Copy-Item -LiteralPath $icoPath -Destination $iconInDist -Force
    if (-not (Test-Path -LiteralPath $iconInDist)) {
        throw "产物根目录缺少 AppIcon.ico: $iconInDist"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $distDir 'Assets\AppIcon.ico'))) {
        Write-Host "      [提示] csproj Content 没有把 ico 拷到 Assets\ 下（根目录那份已补齐，不影响运行）" -ForegroundColor Yellow
    }

    # -------------------------------------------------------------------------
    # 步骤 4：清理调试符号（Release 不需要 pdb）
    # -------------------------------------------------------------------------
    $pdbs = @(Get-ChildItem -LiteralPath $distDir -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue)
    if ($pdbs.Count -gt 0) {
        $pdbs | Remove-Item -Force
        Write-Host "[4/6] 清理 pdb: 删除 $($pdbs.Count) 个"
    }
    else {
        Write-Host "[4/6] 清理 pdb: 无需删除"
    }

    # -------------------------------------------------------------------------
    # 步骤 5：统计产物
    # -------------------------------------------------------------------------
    $files = @(Get-ChildItem -LiteralPath $distDir -Recurse -File)
    $bytes = 0L
    foreach ($f in $files) { $bytes += $f.Length }
    $mb = $bytes / 1MB

    Write-Host "[5/6] 构建统计"
    Write-Host "      产物目录: $distDir"
    Write-Host "      可执行文件: $exePath"
    Write-Host ("      总大小: {0:N1} MB ({1:N0} 字节)" -f $mb, $bytes)
    Write-Host "      文件数: $($files.Count)"
    Write-Host "      发布模式: $modeText"
    if ($selfContained) {
        Write-Host "      运行时要求: 无（自包含，双击 BiliViewer.exe 即可运行）"
    }
    else {
        Write-Host "      运行时要求: .NET 8 Desktop Runtime（框架依赖发布）"
    }

    # -------------------------------------------------------------------------
    # 步骤 6：安装包（可选）
    # -------------------------------------------------------------------------
    if ($Installer) {
        New-Installer -PerMachine:$PerMachineInstaller
    }
    else {
        Write-Host "[6/6] 跳过安装包（未指定 -Installer）"
    }

    Write-Host "构建完成" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "[错误] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "构建中断，未生成可用产物。" -ForegroundColor Red
    exit 1
}
