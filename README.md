# WinCodexBar

[English](./README.en.md) · [下载最新版](https://github.com/windycn/WinCodexBar/releases/latest) · [更新记录](./CHANGELOG.md)

Windows 上的 Codex 托盘工作台：管理多账号、查看额度、切换模型、分析本地用量。

## 0.2.0 有什么变化

- **屏幕适配**：默认跟随当前显示器 DPI；也可选择 100%–300% 手动缩放。窗口限制在屏幕工作区内，窄屏设置卡片上下排列，超出内容可滚动。
- **字体与图标**：随包提供中文无衬线字体与 Microsoft Fluent UI System Icons，减少系统字体缺失引起的回退和字形差异。窄屏导航自动收起为分类下拉框，设置页无横向滚动。
- **托盘样式**：额度圆环、剩余百分比、经典应用图标。单击立即打开快捷面板，双击打开工作台；Esc 收起面板。
- **交互修复**：删除确认不再被外部点击检测误关；滚动后的隐藏账号区域不再响应点击；打开设置、查看统计与关闭面板按顺序执行。
- **更新更方便**：启动后和每 6 小时自动检查正式版，也可手动检查。用户确认后下载对应架构包、校验 SHA256、备份数据、覆盖程序并重启。覆盖失败尝试回滚，保留日志与旧程序备份。
- **模型**：内置 GPT-6 Astra / Sol / Luna，并合并本机 Codex 模型缓存中的模型、推理强度与服务档位。支持手填模型 ID，升级保留已有选择。
- **重置窗口**：读取 CodexRadar 公开摘要中的当前窗口状态，旧预测不会盖过已开启的窗口。显示来源，点击可查看网站；这不是个人账号已重置的证明。

## 下载与运行

在 [Releases](https://github.com/windycn/WinCodexBar/releases/latest) 下载与你的系统匹配的 ZIP：

| 安装包 | 适用设备 |
| --- | --- |
| `WinCodexBar-0.2.0-win-x64.zip` | Intel / AMD 64 位 Windows |
| `WinCodexBar-0.2.0-win-arm64.zip` | Windows on ARM |
| `WinCodexBar-0.2.0-win-x86.zip` | 32 位 Windows |

解压到独立、可写的文件夹，运行 `WinCodexBar.exe`。发布包自带 .NET 运行时。不要直接在 ZIP 内运行；自动更新需要对程序目录有写入权限。

首次从 0.1.x 升级：先退出旧程序，再解压覆盖或运行新目录中的程序。旧版没有自动更新入口，升级到 0.2.0 后即可使用。程序只允许一个实例运行。

## 日常使用

- **托盘**：查看当前账号的 5 小时 / 7 天额度、重置时间，刷新或切换账号。
- **工作台**：添加、导入、导出、切换、删除账号；查看 Token 活动和本地会话分析。
- **设置 → 外观与缩放**：自动 / 手动缩放、托盘样式，保存后生效。
- **设置 → 唤醒策略**：保持唤醒、高级防休眠、开机启动、自动检查与手动检查更新。
- **设置 → 模型参数**：默认模型、Review 模型、推理强度和服务等级。模型实际可用性由账号与 Codex 版本决定。
- **设置 → 账号设置**：手动模式 / 聚合模式。手动切换同步登录配置；已运行的 Codex 通常需要重启或新开实例。聚合模式把后续请求交给本地账号网关，正在运行的响应流不被中途切换。

关闭工作台会收回托盘；彻底退出请使用托盘菜单的“退出”。高级防休眠会模拟轻微鼠标移动，可单独关闭。

## 更新、数据与备份

账号与设置保存在 `%USERPROFILE%\.codexbar`，不在程序目录；设置 `CODEXBAR_HOME` 时使用该目录下的 `.codexbar`。升级保留旧模型、价格和外观设置，补充缺失的新字段。

- 每个新版本首次启动，在 `.codexbar\backups\before-版本-时间` 备份现有账号与设置。
- 自动覆盖更新前，另行备份到 `.codexbar\backups\before-update-时间`。
- 更新包及程序回滚备份保存在 `%LOCALAPPDATA%\WinCodexBar\updates\随机目录`，内有 `result.txt` 和 `previous-program`。
- 覆盖失败会尝试恢复被修改文件；若目录权限或文件占用阻止恢复，可退出应用后从 `previous-program` 手动还原。自动回滚针对文件替换失败，不等同于检测新版本的全部运行问题。
- 恢复账号或设置时，先退出程序，再将备份中的 JSON 放回 `.codexbar`。备份和导出文件含登录凭证，不要公开分享。
- 更新与删除账号不会删除 `.codex\sessions` 或 `.codex\archived_sessions`。删除账号仅从 WinCodexBar 账号池移除，不代表撤销远端登录或清除其他客户端的登录状态。

自动检查只读取本仓库公开 Release 元数据，不上传账号或会话。发现新版本会通知，**不会未经确认自动安装**。网络不可用时继续使用当前版本。

## 统计与信息来源

Token 活动来自本地 Codex 会话文件；金额是按所选价格预设计算的估值，不是账单，也不能反推订阅额度。GPT-6 内置价格采用标准短上下文价格，不包含 Fast、长上下文、缓存写入等附加规则。

模型资料：[OpenAI 模型目录](https://developers.openai.com/api/docs/models)。重置窗口数据来自 [Codex 雷达 codexradar.com](https://codexradar.com/) 的公开摘要，三分钟缓存；完整 API 需站方授权，本程序不请求受保护的完整接口。

## 构建与验证

需要 .NET 8 SDK。Windows 可运行完整窗口回归；其他系统可交叉编译 Windows 程序并运行核心回归。

```powershell
dotnet build windows/CodexBarWin/CodexBarWin.csproj -c Release
dotnet run --project tests/WinCodexBar.CoreTests -c Release
dotnet run --project tests/WinCodexBar.WindowsTests -c Release
dotnet publish windows/CodexBarWin/CodexBarWin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Windows 回归覆盖设置卡片、手动缩放、窗口开关、删除确认和真实 PowerShell 更新脚本。测试使用临时数据目录与虚构账号，不连接真实账号。多台实体显示器之间的热插拔、不同显卡与 Windows 主题仍建议在实际设备上验收。

## License

[MIT](./LICENSE) · [第三方组件声明](./THIRD_PARTY_NOTICES.md)
