# OpenClaw Debugger

OpenClaw Debugger 是一个面向 Windows 的桌面管理工具，用于查看和维护 OpenClaw 服务器上的记忆、表情包目录与模型配置，并创建服务器文件快照。

项目采用 WPF/C# 宿主 + WebView2 HTML/CSS/JavaScript 界面。远程操作通过 Windows OpenSSH 连接服务器，凭据和本机私有数据不会写入仓库。

## 功能

- **连接与概览**
  - 保存 SSH 主机、端口、用户名和 OpenClaw 工作区路径。
  - 打开应用后自动读取仓库外的本机设置；连接状态、重连和断开操作有明确反馈。
  - 远程操作统一经过桥接协议，带参数校验、超时和取消支持。

- **记忆**
  - 浏览服务器工作区中的记忆文件。
  - 编辑、保存和刷新文件内容。
  - 保存前进行内容校验，支持冲突检测和本地回滚快照。

- **表情包**
  - 支持 PNG、JPG、JPEG、GIF、WEBP、BMP 等图片格式。
  - 拖拽或选择本地文件上传，分块传输，支持暂停、取消、失败重试和断点续传。
  - GIF 在预览中播放；图片目录显示固定尺寸缩略图，缩略图会持久化缓存。
  - 支持重命名、标签编辑、权重调整和目录自动登记。
  - 目录文件与说明文件事务写入，出现错误时回滚。

- **模型**
  - 显示当前模型、备选模型和上次延迟测试结果。
  - 测试模型延迟、添加新模型。
  - 拖拽调整备选模型顺序，顺序变化会自动写入服务器配置，不需要额外的保存按钮。

- **主题**
  - 切换界面主题，调整背景泛白、模糊和界面颜色。
  - 支持 Atri、洛茜等背景主题以及 GIF/图片预览效果。

- **服务器备份**
  - 在服务器端生成根文件系统的 tar.gz 快照，默认排除 `/proc`、`/sys`、`/dev` 和 `/run`。
  - 显示总大小、已传输大小、速度、预计剩余时间、重试次数和当前阶段。
  - 支持暂停、继续、取消、网络中断后从临时文件继续传输。
  - 完成后校验 gzip、计算 SHA-256，并通过临时目录和原子移动提交快照。
  - 支持保留数量设置和启动时清理残留临时目录。

## 技术栈

- .NET 10（Windows）
- WPF
- Microsoft WebView2
- HTML、CSS、原生 JavaScript
- Windows OpenSSH Client
- 远程端：Python 3、tar、gzip、stat、tail，以及 OpenClaw CLI（模型功能需要）

## 快速开始

### 1. 获取代码

```powershell
git clone <repository-url>
cd OpenClaw-Debugger
```

### 2. 构建

```powershell
dotnet build OpenClawDebugger.csproj
```

### 3. 运行

```powershell
dotnet run --project OpenClawDebugger.csproj
```

也可以直接运行构建输出目录中的 `OpenClawDebugger.exe`。

首次启动后，在“设置”中填写：

- SSH 主机名或 IP
- SSH 端口
- SSH 用户名
- OpenClaw 工作区路径
- 表情包目录路径
- 本地备份保留数量

程序使用系统 SSH 客户端。请先确认 Windows 已安装 OpenSSH Client，并确保当前 Windows 用户可以通过 SSH 认证；不要把私钥或凭据复制到仓库。

## 本地数据与隐私

程序会在项目目录之外创建私有数据目录，保存设置、日志、回滚快照、上传临时文件、缩略图缓存和服务器备份。该目录不应加入 Git。

仓库的 `.gitignore` 已覆盖以下类型：

- Codex/本地说明文件
- 私有设置和本地配置
- SSH 私钥、令牌、凭据和环境变量文件
- 日志、转储、临时文件和上传缓存
- tar/zip 等备份归档
- 导出文件、回滚目录和缩略图缓存

提交代码前建议检查：

```powershell
git status --short --ignored
git diff -- .
```

## 项目结构

```text
OpenClaw-Debugger/
├─ WebUi/                         # WebView2 页面、状态模块和控制器
│  ├─ index.html
│  ├─ styles.css
│  ├─ styles-overrides.css
│  ├─ bridge.js                   # 前端桥接协议
│  ├─ bridge-contract.json        # 命令与参数契约
│  ├─ *-state.js                  # 页面状态
│  └─ *-controller.js             # 页面交互
├─ Assets/                        # 应用图标和主题背景
├─ *BridgeHandler.cs              # 按领域注册桥接命令
├─ *Service.cs                    # 记忆、表情包、模型等领域服务
├─ Remote*Client.cs               # 远程客户端接口和实现
├─ RemoteAgentSession.cs          # 复用 SSH 的 JSON 会话
├─ RemoteAgentProgram.cs          # 服务器端轻量代理
├─ LocalServerBackupStore.cs      # 本地快照落盘、校验和保留策略
├─ RemoteSnapshotTransport.cs     # 快照生成与流式断点传输
├─ SettingsRepository.cs          # 仓库外私有设置
└─ OpenClawDebugger.csproj
```

## 备份格式

每个快照目录包含：

- `server-rootfs.tar.gz`：服务器根文件系统的压缩归档
- `snapshot-manifest.json`：创建时间、归档大小、SHA-256、范围和一致性说明

这是在线文件级归档。服务器文件在读取过程中可能发生变化，因此它不是磁盘或卷级的原子快照。恢复操作目前需要用户自行解压或使用其他运维工具完成。

## 开发约定

- 不要提交私钥、服务器地址、用户名、访问令牌或私有设置。
- 新增 WebView 命令时同步更新 `WebUi/bridge-contract.json` 和 C# 契约校验。
- 远程操作应支持取消、超时，并通过操作日志记录阶段、耗时和重试次数。
- 涉及文件写入时使用临时文件、校验和及原子替换，避免半写入状态。
- 变更 UI 后请同时检查 `WebUi/index.html`、对应状态模块和控制器。

## 许可证

当前仓库尚未指定开源许可证。公开发布前请根据项目需要补充 LICENSE 文件。
