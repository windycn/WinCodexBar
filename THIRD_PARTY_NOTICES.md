# 第三方组件声明

WinCodexBar 的发布包包含以下组件与资源：

- **Windows App SDK / WinUI 3**：Microsoft，随发布包提供其官方 Windows App Runtime 安装程序；许可见 SDK NuGet 包与运行时安装程序。https://github.com/microsoft/windowsappsdk
- **WinUIEx**：Morten Nielsen 与 contributors，MIT License。https://github.com/dotMorten/WinUIEx
- **WebView2**：Microsoft，供本机模型质量检测结果和 SVG 预览使用；运行时由 Windows 或 Microsoft Edge WebView2 提供。https://developer.microsoft.com/microsoft-edge/webview2/
- **Svg.Skia / SkiaSharp**：Wiesław Šoltés、Microsoft 与 contributors，MIT License。用于把 SVG 绘制成图库缩略图；SVG 正文仍以可编辑的矢量文件保存。https://github.com/wieslawsoltes/Svg.Skia https://github.com/mono/SkiaSharp
- **.NET / Windows Forms / Microsoft.Data.Sqlite / System.Memory**：Microsoft 与 .NET contributors，MIT License。自包含 Windows 包包含 .NET 运行时。https://github.com/dotnet/runtime
- **SQLitePCLRaw**：Eric Sink 与 contributors，Apache-2.0 License。https://github.com/ericsink/SQLitePCL.raw
- **SQLite**：Public Domain。https://sqlite.org/copyright.html
- **Microsoft Fluent UI System Icons**：Copyright (c) Microsoft Corporation，MIT License。图标字体随包嵌入，不依赖系统图标字体。来源：https://github.com/microsoft/fluentui-system-icons 。完整许可见 `windows/CodexBarWin/Assets/Fonts/FluentSystemIcons-LICENSE.txt`（发布包 `licenses/FluentSystemIcons-LICENSE.txt`）。
- **Noto Sans SC**：The Noto Project Authors，SIL Open Font License 1.1。来源：https://github.com/google/fonts/tree/main/ofl/notosanssc 。本项目将字体实例化为固定字重并保留界面使用的字符范围；衍生字体家族更名为 WinCodexBar Sans。完整许可见 `windows/CodexBarWin/Assets/Fonts/NotoSansSC-OFL.txt`（发布包 `licenses/NotoSansSC-OFL.txt`）。

CodexRadar 为外部信息来源，界面保留其来源标注与链接。其服务并非本程序的一部分。

黑屏离开的原生空闲关屏流程参考了 MIT 许可的 [Display Off](https://github.com/itsnateai/displayoff) 项目提出的实现思路。本项目使用 Windows Power API 独立实现，没有打包其 Python 代码。
