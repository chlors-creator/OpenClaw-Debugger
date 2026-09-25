# OpenClaw Debugger

OpenClaw Debugger 是一个运行在 Windows 上的本地桌面管理工具，用于查看和维护 OpenClaw 服务器中的记忆文件、表情包及其标签，并把服务器根文件系统归档到本机。项目目前采用 HTML/CSS/JavaScript 界面 + WPF WebView2 宿主 + C# 本地服务的结构。

## 项目目标与当前范围

- 在桌面应用中连接 OpenClaw 服务器，查看工作区记忆 Markdown 和表情包目录。
- 编辑记忆内容、表情包标签和权重，并安全上传、预览、重命名图片。
- 管理主题和界面颜色，以及背景模糊、泛白和可见度。
- 把服务器 `/` 归档为本机 tar.gz，并在私密目录生成校验清单。
- 对远程路径和可执行操作设定明确边界；本项目不是通用 SSH 终端，也没有整机快照恢复界面。

## 已实现功能

### 连接、记忆与写入保护

- 默认连接 `admin@106.14.173.90:22`，工作区为 `/home/admin/.openclaw/workspace`，表情包目录为 `/home/admin/.openclaw/workspace/stickers`。
- 使用 Windows OpenSSH 客户端，并沿用当前 Windows 用户的 SSH 登录环境。用户确认在目标机器上 `ssh admin@106.14.173.90` 可以直接连接；不在仓库保存或要求输入私钥。
- 读取工作区 Markdown、记忆目录内容、表情包目录清单和图片。远程可读写路径由客户端代码限制，不允许用路径穿越访问范围外文件。
- 编辑记忆时先展示差异，保存前重新读取远端并比较 SHA-256，发现内容已变化则拒绝覆盖。
- 写远端文件前把原内容保存到本机回滚目录，并使用 Windows DPAPI 保护。

### 表情包

- 支持 PNG、JPG/JPEG、GIF、WEBP、BMP；GIF 可播放。
- 图片目录提供缩略图，并缓存近期已查看的图片，返回已查看图片时复用缓存。
- 可从文件选择器或拖放上传；单张上限 16 MiB。上传后自动更新远端目录登记。
- 支持重命名图片，并同步维护目录及引用；拒绝同名覆盖。
- 每张表情包有默认值为 `1` 的选择权重，写入目录数据。应用端按表情包适配的模型清单格式保留/更新相应权重字段。
- 标签编辑会先预览变更，再同步保存 `catalog.json` 和 `MANIFEST.md`（依据服务器现存清单格式处理）。

### 主题

- 当前主题：`Atri`、`洛茜`、`浅色`。Atri 与洛茜使用项目内置背景图。
- 主题页提供背景模糊程度、泛白程度、背景图可见度和 16 项界面颜色调整；颜色按主题保存在本机浏览器的 localStorage，主题名称由本机设置保存。
- Atri 和洛茜背景资源位于 `Assets/Backgrounds/`，构建时复制到 Web UI 资源目录。

### 服务器归档备份

- 点击备份后，应用先远程遍历并压缩计数，估算归档总大小；然后再执行一次 tar/gzip，把压缩流经 SSH 直接写到本机临时目录。
- UI 展示估算总量、已传输大小、平均传输速度和预计剩余时间；完成后生成归档和 `snapshot-manifest.json`，并将临时目录改名为带时间戳的备份目录。
- 归档文件为 `server-rootfs.tar.gz`，清单记录主机、账号、时间、字节数、SHA-256、范围和一致性说明。失败时会尽力清理未完成的临时目录。
- 归档覆盖 `/`，包含已挂载文件系统，但排除 `/proc`、`/sys`、`/dev`、`/run`。这是在线文件级 tar 归档，不是原子磁盘/卷快照；运行中变化的文件可能出现时间点不一致。
- 预估会额外完整读取并压缩一次文件树，增加服务器 CPU、磁盘 I/O 和备份耗时；它不会在服务器留下预生成的归档文件，文件变化也可能导致最终大小与预估值略有偏差。
- 当前实现提供创建与查看备份目录，不提供整机归档恢复流程。

## 目录结构

```text
OpenClaw-Debugger/
├── Assets/
│   ├── Backgrounds/       # Atri、洛茜主题背景
│   └── OpenClawDebugger.ico
├── WebUi/
│   ├── index.html         # 页面结构
│   ├── styles.css         # 布局、主题和动效
│   └── app.js             # 页面交互、主题、上传和 WebView 消息调用
├── App.xaml(.cs)          # WPF 应用入口
├── MainWindow.xaml(.cs)   # WebView2 宿主、消息桥接、操作流程
├── Models.cs             # 设置和数据模型
├── RemoteOpenClawClient.cs # SSH、远程路径校验、文件操作、上传和归档流
├── StickerCatalogEditor.cs # 表情包目录格式读取、编辑和同步
├── LocalSnapshotStore.cs  # DPAPI 加密的远端文件回滚副本
├── LocalServerBackupStore.cs # 整机归档和本机清单写入
├── SettingsRepository.cs  # 私密目录和本机设置
└── OpenClawDebugger.csproj
```

## 本机私密数据

私密目录固定在仓库外：

```text
%USERPROFILE%\Documents\ChatGPT\Openclaw\OpenClaw-Debugger-Private\
├── settings.json
├── Rollback\                 # DPAPI 加密的远端文件旧版本
├── Exports\
├── Secrets\
└── OpenClaw-Server-Backup\   # 整机 tar.gz 和快照清单
```

不要把 SSH 私钥、凭据、服务器导出数据或私密配置放入仓库。`.gitignore` 已忽略常见密钥文件及构建输出。连接设置、回滚数据和整机备份均写入上述私密目录。

## 架构与修改约定

- `WebUi/` 是 HTML UI 的唯一界面层；静态资源随构建复制。不要把远程 CDN 作为运行依赖。
- UI 通过映射到固定来源 `https://openclaw.local` 的 WebView2 页面加载，并通过类型化消息调用宿主功能。
- `MainWindow.xaml.cs` 是宿主桥接入口。只添加具体、有限的操作，不接受来自 UI 的任意 shell 命令。
- `RemoteOpenClawClient.cs` 管理 SSH 调用、允许的远程目录、文件校验、上传和归档传输。新增远程功能时应遵循现有路径限制和哈希冲突检查。
- 连接默认值和主题偏好存入私密目录设置文件；主题调色板及背景微调值存于本机浏览器 localStorage。
- 桌面应用图标来自 `Assets/OpenClawDebugger.ico`；不要在仓库中加入服务器密钥或备份产物。

## 运行与构建

环境要求：

- Windows
- .NET 10 SDK（目标框架为 `net10.0-windows`）
- Microsoft Edge WebView2 Runtime
- Windows OpenSSH Client
- 服务器上的 Python 3（常规远程文件操作）
- 服务器可用的 Bash、GNU tar/gzip、`wc`；备份账号还需允许非交互 `sudo -n tar`

在项目目录启动开发版本：

```powershell
dotnet run --project .\OpenClawDebugger.csproj
```

普通构建：

```powershell
dotnet build .\OpenClawDebugger.csproj
```

## 当前状态与交接提示

- 最近一次已完成构建：`WeightBuild` 配置输出到 `bin\WeightBuild-BackupProgress\net10.0-windows`，构建成功，0 个警告、0 个错误。这个构建状态不代表已在目标服务器上实际执行过完整备份或恢复验证。
- 桌面快捷方式名称为 `OpenClaw-Debugger.lnk`，近期已将其目标指向上述备份进度版本输出。若已有应用窗口仍开着，它不会自动加载新构建；关闭并重新启动应用后再确认界面。
- 仓库目标目录是 `Openclaw\OpenClaw-Debugger`。此前项目从 Napcat 工作区迁移到 Openclaw；修改前应先核对当前实际工作目录，避免改错同名目录。
- 新对话开始时先读本 README、`git status` 和相关源码，再确认用户当前要改的功能。不要假设工作树干净，也不要把私密目录复制进仓库。