# WinCodexBar Windows 工程

这里是 WinCodexBar 的 Windows 客户端源码，使用 C#、Windows Forms 和 .NET 8 构建。

## 开发环境

- Windows 10/11
- .NET 8 SDK

## 常用命令

```powershell
dotnet restore windows\CodexBarWin\CodexBarWin.csproj
dotnet build windows\CodexBarWin\CodexBarWin.csproj -c Release
dotnet publish windows\CodexBarWin\CodexBarWin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 工程结构

- `Models`：账号、配置和用量模型。
- `Services`：账号导入导出、OAuth、用量刷新、网关、唤醒策略和本地会话扫描。
- `Tray`：托盘入口、菜单和后台协调逻辑。
- `UI`：工作台、设置页、托盘弹窗和 Fluent 风格控件。
- `Assets`：应用图标和界面图标。

## 注意

- 不要在日志或提交内容中输出 access token、refresh token、id token。
- 本地导出的账号文件属于敏感数据。
- 发布包应从 `Release` 配置生成。
