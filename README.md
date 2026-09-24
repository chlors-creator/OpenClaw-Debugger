# OpenClaw Debugger

Windows 桌面管理工具，阶段一和阶段二实现范围：

- 通过本机 Windows OpenSSH 连接服务器，复用当前用户的 SSH 配置、known_hosts 和登录身份。
- 只读扫描 OpenClaw workspace Markdown、表情包目录及图片。
- 浏览和编辑 MEMORY.md、USER.md、AGENTS.md、SOUL.md、DREAMS.md 和 memory 子目录中的 Markdown 文件。
- 预览贴图，并在识别现有 catalog 结构后编辑标签；保存同步更新 catalog.json 与 MANIFEST.md。
- 标签格式暂不兼容时，可通过“高级编辑原始标签文件”同时编辑两份文件。
- 写入前显示两侧内容，检查远程 SHA-256 冲突，并将原版本以 Windows DPAPI 加密存入仓库旁的 OpenClaw-Debugger-Private\Rollback。

## 启动

需要 Windows、.NET 10 Desktop Runtime、Windows OpenSSH Client，以及服务器上的 Python 3。

运行命令：dotnet run --project .\OpenClawDebugger.csproj

应用默认使用项目背景中记录的服务器、工作区和 stickers 路径。连接在当前 Windows 用户上下文中发起；应用不保存、生成、复制或要求输入 SSH 私钥。严格校验 SSH 主机指纹。如果终端里的 ssh admin@106.14.173.90 在同一 Windows 用户下可用，应用沿用相同的 OpenSSH 身份。

所有远程路径都经过固定范围校验。文件通过 SSH 标准输入交给固定 Python helper 处理，不接受界面输入的远程 shell 命令。图片只在选择预览时读取。远程文本不写入应用仓库。

## 私密数据

私密目录为 OpenClaw-Debugger 的相邻目录 OpenClaw-Debugger-Private。settings.json 保存连接路径等非凭证配置；Rollback 中的内容快照由 Windows DPAPI 加密。SSH 密钥继续由 Windows OpenSSH 原有配置管理，不复制到该目录或 Git 仓库。