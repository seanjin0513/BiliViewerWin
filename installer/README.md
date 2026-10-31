# BiliViewer Windows 安装程序（NSIS）

`BiliViewer.nsi` 用 **NSIS 3 + MUI2（简体中文）** 把 `..\dist\BiliViewer\**`
（build.ps1 生成的绿色文件夹）打成一个安装包。

同一份脚本靠 `-DPERMACHINE` 切换两种安装口径：

| 口径 | 输出 | 权限 | 卸载项 | 快捷方式 |
| --- | --- | --- | --- | --- |
| **per-user（默认，出货用）** | `..\dist\BiliViewer-Setup-1.0.0.exe` | `RequestExecutionLevel user`，**全程不弹 UAC** | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\BiliViewer` | 当前用户（`%APPDATA%`、`桌面`） |
| per-machine（可选，`-DPERMACHINE`） | `..\dist\BiliViewer-Setup-1.0.0-permachine.exe` | `RequestExecutionLevel admin`，**会弹 UAC** | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BiliViewer` | 所有用户（`%ProgramData%`、`Public\Desktop`） |

两者默认安装目录都是 **`D:\Program Files\BiliViewer`**：安装前用「建目录 + 写探针文件」确认当前账户
真的可写才用；不可写时回退——per-user 回退 `%LOCALAPPDATA%\Programs\BiliViewer`，
per-machine 回退 `%ProgramFiles%\BiliViewer`。

## 1. 为什么出货包必须是 per-user

本机（开发机）当前登录令牌**不含管理员组**：

- `[Security.Principal.WindowsIdentity]::GetCurrent().Groups` 只有 10 个组，没有 `S-1-5-32-544`
- `New-Item HKLM:\SOFTWARE\...\Uninstall\__bvtest` → `Requested registry access is not allowed`
- `IsInRole(Administrator)` = `False`

磁盘上 `Get-LocalGroupMember Administrators` 里确实有当前用户，但**当前 token 是过滤过的**，
没有提权能力；UAC 只会弹「输入管理员凭据」，而本机没人能输入。
所以「需要管理员」的安装包在这台机器上**根本装不了**，出货口径必须是 per-user。

验证方式（编译产物自证）：per-user 版 exe 的清单里是 `level="asInvoker"`，
per-machine 版是 `level="requireAdministrator"`。可以用
`[IO.File]::ReadAllBytes($exe)` 搜字符串确认。

## 2. 准备 makensis（免管理员，绿色版）

不用安装 NSIS、不碰 Program Files、不弹 UAC，直接把官方绿色 zip 解到 `%TEMP%\nsis`：

```powershell
$url = 'https://github.com/tauri-apps/binary-releases/releases/download/nsis-3.11/nsis-3.11.zip'
Invoke-WebRequest -Uri $url -OutFile "$env:TEMP\nsis-3.11.zip"
Expand-Archive -LiteralPath "$env:TEMP\nsis-3.11.zip" -DestinationPath "$env:TEMP\nsis" -Force
& "$env:TEMP\nsis\nsis-3.11\makensis.exe" /VERSION   # 期望输出 v3.11
```

`build.ps1 -Installer` 会按这个顺序自动找编译器：

1. `%TEMP%\nsis\nsis-*\makensis.exe`（上面这套，推荐）
2. `PATH` 里的 `makensis`
3. `%ProgramFiles(x86)%\NSIS\makensis.exe`、`%ProgramFiles%\NSIS\makensis.exe`

都找不到时 build.ps1 会直接报错并打印上面的下载命令。

> 版本：本项目实测 **NSIS 3.11**（x86-unicode）。3.10 的官方 zip 在 SourceForge，被反爬拦了
> （`downloads./master./netcologne.` 三个镜像全 403），所以用 GitHub 上的官方 3.11。
> 脚本本身不依赖具体小版本。

## 3. 编译

**必须在 `installer` 目录下执行**（脚本里的 `..\dist\...`、`..\Assets\AppIcon.ico` 是相对路径），
并且先跑过一次 `build.ps1`（需要有 `..\dist\BiliViewer\BiliViewer.exe` 和 `..\Assets\AppIcon.ico`）。

```powershell
cd installer

# 出货包：per-user，免管理员（默认，不带任何 define）
& "$env:TEMP\nsis\nsis-3.11\makensis.exe" /INPUTCHARSET UTF8 BiliViewer.nsi

# 可选：per-machine 版（所有用户，需要管理员权限）
& "$env:TEMP\nsis\nsis-3.11\makensis.exe" /INPUTCHARSET UTF8 -DPERMACHINE BiliViewer.nsi
```

`/INPUTCHARSET UTF8` 是必须的：脚本是 UTF-8，里面有中文界面文字；缺了会按系统 ACP 解析，
`!include`/`File` 里的中文路径就会「can't open file」。

也可以走构建脚本（会先 publish 再打安装包）：

```powershell
pwsh -File .\build.ps1 -Installer                            # 完整构建 + 出货包（per-user）
pwsh -File .\build.ps1 -SkipIcon -NoPublish -Installer       # 不重新 publish，用现有 dist 打出货包
pwsh -File .\build.ps1 -Installer -PerMachineInstaller       # 额外打一个 per-machine 版（本机装不了，仅留档/给有管理员的机器用）
```

## 4. 安装行为（出货包 = per-user）

| 项 | 值 |
| --- | --- |
| 默认安装目录 | `D:\Program Files\BiliViewer`（探针确认可写才用；否则 `%LOCALAPPDATA%\Programs\BiliViewer`） |
| 目录页 | MUI 目录页，用户可自行改；`/D=` 时不探测 |
| 组件页 | 「哔哩哔哩 BiliViewer（必需）」+「创建桌面快捷方式」（默认勾选，可取消） |
| 开始菜单 | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\哔哩哔哩\BiliViewer.lnk`、`…\卸载 BiliViewer.lnk` |
| 完成页 | 「立即运行 BiliViewer」默认勾选 |
| 卸载登记 | `HKCU\…\Uninstall\BiliViewer`（Windows 设置「应用」里可见） |
| 安装前检查 | `BiliViewer.exe` 在运行则提示关闭并中止（静默模式退出码 `2`） |

注册表值（10 个）：`DisplayName`＝哔哩哔哩 BiliViewer、`DisplayVersion`＝1.0.0、`Publisher`＝BiliViewer、
`DisplayIcon`＝`…\BiliViewer.exe,0`、`InstallLocation`、`EstimatedSize`（KB）、
`UninstallString`、`QuietUninstallString`（带 `/S`）、`NoModify`=1、`NoRepair`=1。

D: 盘探测：`IfFileExists "D:\*.*"` 判盘 → `CreateDirectory "D:\Program Files\BiliViewer"` →
`FileOpen` 写 `.bv_write_test` 探针 → 成功后删探针文件并 `RMDir` 掉临时目录。
任何一步失败就走回退目录，不会装到一半失败。

## 5. 命令行

```powershell
# 静默安装到默认目录（D: 可写就是 D:\Program Files\BiliViewer）
BiliViewer-Setup-1.0.0.exe /S

# 静默安装到指定目录（/D= 必须是最后一个参数、不能带引号）
BiliViewer-Setup-1.0.0.exe /S /D=C:\Apps\BiliViewer

# 静默卸载
"C:\Apps\BiliViewer\Uninstall.exe" /S
```

`/LANG=2052` 是唯一语言（简体中文），无需指定；`/D=` 存在时不会被默认目录探测覆盖。

## 6. 卸载

删除：安装目录（`RMDir /r` 整个目录，含 `Uninstall.exe`）、桌面与开始菜单快捷方式、卸载注册表项。

**保留**：`%LOCALAPPDATA%\BiliViewer`（登录 Cookie / 窗口设置），卸载完成页会提示
「登录数据与窗口设置已保留在 %LOCALAPPDATA%\BiliViewer」。
per-user 的安装目录是 `%LOCALAPPDATA%\Programs\BiliViewer`，与数据目录 `%LOCALAPPDATA%\BiliViewer`
是两个不同目录，回退时也不会误删数据。

## 7. 出货包实测记录（用 `dist\BiliViewer-Setup-1.0.0.exe` 本体，全程无 UAC）

`PASS=57 FAIL=0`：

1. 清单 `asInvoker`、当前 token 无管理员组 → 说明「装得成」这件事本身就证明免提权
2. `/S` 装到 `D:\Program Files\BiliViewer`：**253 个文件**（252 产物 + `Uninstall.exe`）、
   146.7 MB、`coreclr.dll`（自包含运行时）/ `Assets\AppIcon.ico` / `runtimes\win-x64\native\WebView2Loader.dll` 都在
3. `HKCU\…\Uninstall\BiliViewer` 10 个值齐全，`EstimatedSize`=150206 KB（实测 150207 KB），HKLM 未被写
4. 开始菜单 `哔哩哔哩\BiliViewer.lnk` + `卸载 BiliViewer.lnk`、桌面 `哔哩哔哩 BiliViewer.lnk`，
   三个 lnk 的 `TargetPath` 都指向安装目录里的 exe / Uninstall.exe
5. 启动 `BiliViewer.exe`，6 秒后进程存活，窗口标题「哔哩哔哩 (゜-゜)つロ 干杯~-bilibili」
6. 应用运行中再 `/S` → **退出码 2**，exe 时间戳未变（没有覆盖）
7. `Uninstall.exe /S` → 安装目录、HKCU 项、开始菜单目录、桌面 lnk 全清；`%LOCALAPPDATA%\BiliViewer` 保留（695 → 707 项）
8. `/S /D=D:\Program Files\BiliViewerCustom` 再走一遍：安装/`InstallLocation` 跟随/卸载清理全部正常，
   且默认目录没有被顺带创建
9. 残留检查：`D:\Program Files` 仍是原来的 49 项、无 HKCU/HKLM 项、无 `%TEMP%\bvinst`、无残留进程

GUI 也逐页截图确认过（`docs\installer-*.png`）：标题「哔哩哔哩 BiliViewer 安装程序」；
向导页序是 **组件页 → 目录页 → 安装中 → 完成页**；组件页顶部写明是 per-user 免 UAC；
目录页默认值就是 `D:\Program Files\BiliViewer`、所需空间 146.7 MB、可用空间 391.9 GB；
完成页「立即运行 BiliViewer」默认勾选。

**没有欢迎页**：MUI 欢迎页的正文控件对 `%LOCALAPPDATA%\Programs\BiliViewer` 这类长英文串不折行，
正文会溢出重叠糊成一坨，所以 `MUI_PAGE_WELCOME` 已删掉，欢迎信息挪到组件页顶部
（`MUI_COMPONENTSPAGE_TEXT_TOP`）。

**banner 图**（`installer\assets\`，已入库、编译时由 makensis 直接引用，不需要重新生成）：

| 文件 | 尺寸 | 用途 |
| --- | --- | --- |
| `header.bmp` | 150x57，24 位 BMP | 组件页 / 目录页 / 安装中页右上角的 header 图（`MUI_HEADERIMAGE_BITMAP`，`MUI_HEADERIMAGE_RIGHT`） |
| `welcome-finish.bmp` | 164x314，24 位 BMP | 完成页左侧大图（`MUI_WELCOMEFINISHPAGE_BITMAP`） |

两张图都是 App 图标同款 `#FB7299 → #23ADE5` 对角渐变 + 白色电视机标志。

## 8. 已知注意点

- `Assets\AppIcon.ico` 被 `.gitignore` 忽略，新克隆先跑 `build.ps1`（或 `-SkipIcon` 配合已有 ico）。
- 安装包图标用的是 `Assets\AppIcon.ico`，它是 **PNG 压缩**的多尺寸 ico；NSIS 3.11 能正常接受
  （3.0 早期版本可能报 invalid icon，遇到就换 3.11+）。
- `SetCompressor /SOLID lzma`，150 MB 的绿色目录压到约 45 MB（31%）。
- per-machine 版在本机**装不了**（没有管理员令牌），只能编译、看清单，安装验证必须去有管理员的机器。
- 装到 `D:\Program Files\BiliViewer` 需要该目录对当前账户可写；如果哪天 D: 盘权限收紧，
  per-user 包会自动落到 `%LOCALAPPDATA%\Programs\BiliViewer`，功能不受影响。
