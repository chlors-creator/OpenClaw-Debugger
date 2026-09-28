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
- 图片目录提供固定 256px 边界框缩略图；列表只请求并缓存缩略图，点击预览时才请求原图。缩略图会同时持久化到私密目录 `ThumbnailCache`，缓存键包含远端路径、文件大小、修改时间和可用哈希，远端文件变化后自动生成新版本；原图和缩略图仍带容量上限的 LRU 缓存，切换回来不会重复读取。
- 可从文件选择器或拖放上传；单张上限 16 MiB。上传后自动更新远端目录登记。
- 支持重命名图片，并同步维护目录及引用；拒绝同名覆盖。
- 每张表情包有默认值为 `1` 的选择权重，写入目录数据。应用端按表情包适配的模型清单格式保留/更新相应权重字段。
- 标签编辑会先预览变更，再以服务器端锁、SHA-256 前置校验、临时文件和目录 fsync 事务写入 `catalog.json` 与 `MANIFEST.md`（依据服务器现存清单格式处理）；第二个文件写入失败时会尝试回滚第一个文件。

### 主题

- 当前主题：`Atri`、`洛茜`、`浅色`。Atri 与洛茜使用项目内置背景图。
- 主题页提供背景模糊程度、泛白程度、背景图可见度和 16 项界面颜色调整；颜色按主题保存在本机浏览器的 localStorage，主题名称由本机设置保存。
- Atri 和洛茜背景资源位于 `Assets/Backgrounds/`，构建时复制到 Web UI 资源目录。

### 服务器归档备份

- 点击备份后，应用先在服务器 `/tmp` 生成唯一的 `tar.gz` 快照文件，再读取其准确大小；之后通过 SSH `tail` 按本机临时文件已有长度读取远端归档。网络中断时远端归档和本机已传输前缀都会保留，重试会从断点继续，不会重新传输已经完成的字节。
- 导航栏右侧的独立进度模块展示准确总量、已传输大小、当前传输速度、预计剩余时间和图形化进度条；完成后生成归档和 `snapshot-manifest.json`，并将临时目录改名为带时间戳的备份目录。
- SSH 配置了保活检测。快照生成或传输中遇到可恢复的网络中断时最多自动重试 8 次，按 2、4、8、16、30 秒逐步等待；传输重试会显示保留的字节数、继续位置和当前速度。常规清单、记忆和表情包读写复用同一条 SSH Python 会话，异常时自动重建连接。
- 备份开始后顶部按钮变为“暂停备份”。暂停会保留远端快照和本机临时归档，继续时从当前传输位置恢复；按钮随之变为“继续备份”。快照生成阶段的暂停请求会在当前生成命令结束后等待，传输阶段可以直接暂停读取。
- 备份进行时右侧显示“取消备份”按钮；取消会终止当前 SSH 传输、清理本轮临时归档，并恢复“备份服务器”按钮。
- 归档文件为 `server-rootfs.tar.gz`，清单记录主机、账号、时间、字节数、SHA-256、范围和一致性说明。失败时会尽力清理未完成的临时目录。
- 归档覆盖 `/`，包含已挂载文件系统，但排除 `/proc`、`/sys`、`/dev`、`/run`。这是在线文件级 tar 归档，不是原子磁盘/卷快照；运行中变化的文件可能出现时间点不一致。
- 服务器端快照生成完成后会在任务结束时删除 `/tmp/.openclaw-debugger-*.tar.gz`；取消、失败和窗口关闭也会尽力清理。快照是任务开始时在线生成的文件级归档，生成期间文件可能变化，但传输重试始终读取同一份远端归档。
- 当前实现提供创建与查看备份目录，不提供整机归档恢复流程。

## 目录结构

```text
OpenClaw-Debugger/
├── Assets/
│   ├── Backgrounds/       # Atri、洛茜主题背景
│   └── OpenClawDebugger.ico
├── WebUi/
│   ├── index.html         # 页面结构和资源加载顺序
│   ├── styles.css         # 基础布局、组件和动效
│   ├── styles-overrides.css # 主题、调色板、上传和备份覆盖样式
│   ├── bridge.js          # WebView 消息请求/响应和进度事件
│   ├── theme.js           # 主题、调色板和背景调节控制器
│   ├── sticker-cache.js    # 原图/缩略图缓存控制器
│   └── app.js             # 页面编排、列表渲染、编辑和事件绑定
├── App.xaml(.cs)          # WPF 应用入口
├── MainWindow.xaml(.cs)   # WebView2 生命周期、连接流程和消息编排
├── BridgeDispatcher.cs    # WebView 消息解析、来源校验和命令分发
├── BridgeResponseWriter.cs # WebView 响应和进度事件输出
├── ConnectionSettings.cs  # SSH 和远程目录设置模型
├── RemoteFile.cs          # 远程清单文件模型
├── RemoteFileContent.cs   # 远程文件读取结果模型
├── StickerRow.cs          # 表情包目录行模型
├── UserSettings.cs        # 本机设置模型
├── RemoteClientInterfaces.cs # 文件、表情包和快照客户端边界
├── RemoteOpenClawClient.cs # 复用 SSH 会话和远程代理调用原语
├── RemoteFileClient.cs    # 文件域客户端适配器
├── RemoteStickerClient.cs # 表情包域客户端适配器
├── RemoteAgentProgram.cs  # 独立维护的服务器端 Python 代理
├── SshCommandRunner.cs    # OpenSSH 参数、校验和错误分类
├── RemoteSnapshotClient.cs # 快照域客户端门面
├── RemoteSnapshotTransport.cs # 快照生成、断点传输和清理实现
├── BackupCoordinator.cs   # 备份启动、暂停、继续、取消和进度协调
├── StickerService.cs      # 表情包目录、标签、重命名和上传业务
├── StickerUploadService.cs # 分块上传会话和本地临时文件
├── StickerThumbnailCache.cs # 缩略图生成、持久化和容量清理
├── ParsedStickerCatalog.cs # 解析后的目录编辑模型
├── StickerCatalogEditor.cs # 表情包目录格式识别和 JSON 编辑
├── StickerManifestSynchronizer.cs # MANIFEST.md 表格同步
├── MemoryService.cs       # 记忆文件读取、冲突检测和回滚快照
├── SettingsService.cs     # 设置校验、主题保存和配置持久化
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
├── UploadStaging\            # 分块上传临时文件，提交或取消后删除
├── ThumbnailCache\           # 固定尺寸缩略图持久化缓存
└── OpenClaw-Server-Backup\   # 整机 tar.gz 和快照清单
```

不要把 SSH 私钥、凭据、服务器导出数据或私密配置放入仓库。`.gitignore` 已忽略常见密钥文件及构建输出。连接设置、回滚数据和整机备份均写入上述私密目录。

## 架构与修改约定

- `WebUi/` 是 HTML UI 的唯一界面层；静态资源随构建复制。不要把远程 CDN 作为运行依赖。
- UI 通过映射到固定来源 `https://openclaw.local` 的 WebView2 页面加载，并通过类型化消息调用宿主功能。
- `MainWindow.xaml.cs` 只负责 WebView 生命周期、连接状态和消息编排；`BridgeDispatcher.cs` 负责消息协议，`BridgeResponseWriter.cs` 负责响应和进度事件。
- `MemoryService.cs` 负责记忆文件读取、二次哈希校验、DPAPI 回滚快照和保存；`SettingsService.cs` 负责连接设置校验及主题偏好持久化。
- `StickerService.cs`、`StickerUploadService.cs` 和 `StickerThumbnailCache.cs` 分别负责表情包业务、上传会话和缩略图缓存；表情包操作不要重新放回窗口代码。
- `BackupCoordinator.cs` 负责备份运行状态和按钮控制，`LocalServerBackupStore.cs` 负责本地归档，`RemoteSnapshotClient.cs` 负责快照域接口。
- `RemoteFileClient.cs`、`RemoteStickerClient.cs` 和 `RemoteSnapshotClient.cs` 是三个远程域边界，共用 `RemoteOpenClawClient` 的 SSH 会话或快照传输实现；`RemoteAgentProgram.cs` 单独保存服务器端 Python 协议。新增远程功能时应遵循现有路径限制和哈希冲突检查。
- `WebUi/app.js` 负责编排，`bridge.js`、`theme.js`、`sticker-cache.js` 各自维护桥接、主题和缓存状态；样式覆盖集中在 `styles-overrides.css`。
- 连接默认值和主题偏好存入私密目录设置文件；主题调色板及背景微调值存于本机浏览器 localStorage。
- 桌面应用图标来自 `Assets/OpenClawDebugger.ico`；不要在仓库中加入服务器密钥或备份产物。

## P1 / P2 拆分完成情况

- **P1**：远程访问已经按文件、表情包、快照三个接口拆分；记忆读写和设置校验从 `MainWindow` 移到独立服务；服务器端 Python 代理单独维护。
- **P2**：数据模型、表情包目录解析器、`MANIFEST.md` 同步器分别成文件；Web UI 的桥接、主题、缩略图缓存和 CSS 覆盖层独立成资源；`app.js` 保留页面状态编排。
- 新增模块通过项目默认 SDK 编译项自动纳入，不需要手动修改 `.csproj`；Web UI 资源仍由 `WebUi\**\*` 自动复制。

## 运行与构建

环境要求：

- Windows
- .NET 10 SDK（目标框架为 `net10.0-windows`）
- Microsoft Edge WebView2 Runtime
- Windows OpenSSH Client
- 服务器上的 Python 3（常规远程文件操作）
- 服务器可用的 Bash、GNU tar/gzip、`stat`、`tail`；备份账号还需允许非交互 `sudo -n tar`、`sudo -n stat`、`sudo -n tail` 和 `sudo -n rm`

在项目目录启动开发版本：

```powershell
dotnet run --project .\OpenClawDebugger.csproj
```

普通构建：

```powershell
dotnet build .\OpenClawDebugger.csproj
```

## 当前状态与交接提示

- 最近一次已完成构建：`P2Final4` 配置输出到 `bin\P2Final4\net10.0-windows`，构建成功，0 个警告、0 个错误；本地 JavaScript 语法检查和工作树差异检查已执行。没有执行真实服务器整机备份或恢复测试。
- 桌面快捷方式仍按本机发布目录配置；构建输出切换后请从对应发布目录重新启动。已运行的旧窗口不会热更新；关闭后从桌面快捷方式重新启动即可加载新版。
- 仓库目标目录是 `Openclaw\OpenClaw-Debugger`。此前项目从 Napcat 工作区迁移到 Openclaw；修改前应先核对当前实际工作目录，避免改错同名目录。
- 新对话开始时先读本 README、`git status` 和相关源码，再确认用户当前要改的功能。不要假设工作树干净，也不要把私密目录复制进仓库。
