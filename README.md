# OpenClaw Debugger

Windows 本地桌面管理工具。界面使用 HTML/CSS/JavaScript 构建，由 WPF WebView2 承载；SSH、文件范围校验、冲突检查、加密回滚和整机归档继续由 C# 服务处理。

## 当前功能

- 通过本机 Windows OpenSSH 连接服务器，复用当前 Windows 用户的 SSH 配置、known_hosts 和登录身份。
- 只读扫描 OpenClaw 工作区 Markdown、表情包目录及图片。
- 浏览并编辑工作区记忆 Markdown 文件。保存前显示完整内容差异、重新读取远端并校验 SHA-256。
- 预览贴图与原生 GIF 动图；图片目录按需显示缩略图，并缓存最近查看的图片，切换回来时不重复读取服务器。标签编辑支持先预览 catalog.json 和 MANIFEST.md 的差异，再同步写入两份文件。 每张表情包还可设置 0–1,000,000 的选择权重（默认 1，设为 0 即停用）；服务器在模型圈定的语境合适候选中按“单张权重 ÷ 候选总权重”随机抽取，模型漏报候选时按全目录抽取。支持拖放或选择 PNG、JPG、GIF、WEBP、BMP 图片上传；上传后自动在两份目录文件中登记空标签条目，之后可直接编辑标签。支持重命名并同步更新图片文件名和目录引用；同名文件拒绝覆盖，单张最大 16 MiB。
- 可选择浅色、Atri、洛茜主题；Atri 与洛茜使用内置插画背景。背景模糊、泛白和图片可见度可在主题页即时调整并保存在本机浏览器配置中。颜色模块提供 16 项界面调色变量，并按主题分别保存。
- 可创建服务器根文件系统 tar.gz 在线归档快照，写入 OpenClaw-Debugger-Private\OpenClaw-Server-Backup 并生成含 SHA-256 的清单。
- 服务器文件写入前会将原版本以 Windows DPAPI 加密存入 OpenClaw-Debugger-Private\Rollback。

## 架构与安全边界

- `WebUi/` 包含界面结构、样式和交互逻辑。图片背景作为静态资源复制到运行目录。
- `MainWindow.xaml` 只保留桌面窗口与 WebView2。
- `MainWindow.xaml.cs` 处理来自固定本地来源 `https://openclaw.local` 的类型化消息。它只允许预先定义的连接、读取、写入、预览、设置、打开目录和备份操作，不接受任意 shell 命令。
- WebView2 禁用开发者工具、默认上下文菜单和宿主对象；阻止离开本地 UI 的导航。UI 通过虚拟主机映射加载，不启用远程 CDN。
- 远端路径仍由 `RemoteOpenClawClient` 校验；写入带哈希冲突检查。服务器 tar 使用应用内固定命令，归档数据流直接写入本机备份目录。
- 本机连接设置、私密回滚副本和备份保存在仓库外的 `OpenClaw-Debugger-Private`，不会被加入 Git。

## 启动

需要 Windows、.NET 10 Desktop Runtime、Microsoft Edge WebView2 Runtime、Windows OpenSSH Client，以及服务器上的 Python 3。整机快照还要求服务器账号可以免密执行 `sudo tar`。

开发启动：

```powershell
dotnet run --project .\OpenClawDebugger.csproj
```

桌面快捷方式 OpenClaw-Debugger.lnk 当前指向 bin\WeightBuild\net10.0-windows\OpenClawDebugger.exe；新启动的窗口会加载最新界面。若终端中的 ssh admin@106.14.173.90 在同一 Windows 用户下可用，应用沿用当前 OpenSSH 身份，不复制或要求输入 SSH 私钥。

整机归档覆盖服务器 `/` 的文件，并排除运行时虚拟目录 `/proc`、`/sys`、`/dev`、`/run`。它是遍历文件生成的在线归档，不是原子磁盘快照；备份期间仍在变化的文件可能前后不一致。
