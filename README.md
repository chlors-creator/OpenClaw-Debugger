# OpenClaw Debugger

Windows 桌面管理工具，阶段一和阶段二实现范围：

- 通过本机 Windows OpenSSH 连接服务器，复用当前用户的 SSH 配置、known_hosts 和登录身份。
- 只读扫描 OpenClaw workspace Markdown、表情包目录及图片。
- 浏览和编辑 MEMORY.md、USER.md、AGENTS.md、SOUL.md、DREAMS.md 和 memory 子目录中的 Markdown 文件。
- 预览贴图，并在识别现有 catalog 结构后编辑标签；保存同步更新 catalog.json 与 MANIFEST.md。
- 标签格式暂不兼容时，可通过“高级编辑原始标签文件”同时编辑两份文件。
- 可选浅色、Atri、洛茜主题；Atri 与洛茜使用项目内置插画背景，并带柔焦与轻微泛白效果。主导航使用斜边分组标签，选中项切换为上圆角梯形。
- 连接成功后可点击“备份服务器”，通过 SSH 在服务器上运行 sudo tar，将根文件系统 / 生成压缩快照，存入 OpenClaw-Debugger-Private\OpenClaw-Server-Backup 下的时间戳目录；同时保存快照清单和 SHA-256。快照包含其他挂载目录，排除运行时虚拟目录 /proc、/sys、/dev、/run。这是遍历文件生成的在线归档，不是原子磁盘快照；备份时仍在变化的文件可能前后不一致。服务器账号需能免密 sudo 执行 tar。
- 写入前显示两侧内容，检查远程 SHA-256 冲突，并将原版本以 Windows DPAPI 加密存入仓库旁的 OpenClaw-Debugger-Private\Rollback。

## 启动

需要 Windows、.NET 10 Desktop Runtime、Windows OpenSSH Client，以及服务器上的 Python 3。整机快照还要求服务器账号可免密 sudo 执行 tar。

运行命令：dotnet run --project .\OpenClawDebugger.csproj

应用默认使用项目背景中记录的服务器、工作区和 stickers 路径。连接在当前 Windows 用户上下文中发起；应用不保存、生成、复制或要求输入 SSH 私钥。严格校验 SSH 主机指纹。如果终端里的 ssh admin@106.14.173.90 在同一 Windows 用户下可用，应用沿用相同的 OpenSSH 身份。

受管文件路径都经过固定范围校验；文本与图片操作通过 SSH 标准输入交给固定 Python helper 处理，界面不接受任意远程 shell 命令。整机快照使用固定的 sudo tar 命令，其二进制归档通过 SSH 标准输出流传回本机。图片只在选择预览时读取。远程内容不写入应用仓库。

## 私密数据

私密目录为 OpenClaw-Debugger 的相邻目录 OpenClaw-Debugger-Private。settings.json 保存连接路径等非凭证配置；Rollback 中的内容快照由 Windows DPAPI 加密。SSH 密钥继续由 Windows OpenSSH 原有配置管理，不复制到该目录或 Git 仓库。
