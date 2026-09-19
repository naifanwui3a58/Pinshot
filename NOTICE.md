# NOTICE — 致谢与第三方声明

## 设计参考

- **[Setuna2](https://github.com/tylearymf/Setuna2)**（MIT，作者 @tylearymf）
  本项目的贴图交互设计（截图置顶、滚轮缩放、双击收纳为小图、隐藏/显示全部贴图、
  托盘激活全部贴图等）参考并致敬 Setuna2。本项目为独立实现的 WPF 代码，
  未逐字复制 Setuna2 源码；若后续直接复用其代码片段，请保留其版权与 MIT 许可文本。

## 第三方依赖（通过 NuGet 引入，按各自许可证分发）

| 包 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | MIT | 系统托盘图标 |
| Magick.NET-Q8-AnyCPU | 14.17.1 | Apache-2.0 | WebP/PSD/TGA/SVG 等格式解码 |
| XamlAnimatedGif | 2.3.2 | MIT | GIF 动画贴图播放 |
| PaddleOCRSharp | 4.1.0 | Apache-2.0 | 本地离线 OCR（PaddleOCR 封装） |

全局热键与截图引擎为本项目自研实现（`Core/HotkeyService.cs`、`Views/CaptureOverlayWindow.cs`），不依赖第三方库。

## 系统能力

- 文字识别：主力为 **PaddleOCRSharp**（PaddleOCR 本地推理，模型与 native 依赖内嵌进 exe，启动时自动释放到本地）；
  不可用时回退 Windows 自带的 `Windows.Media.Ocr`（OCR 语言包随系统分发，应用启动时可通过
  `Add-WindowsCapability` 一次性批量安装中/英/日/韩/俄语言包，单次 UAC）。
- 翻译默认使用免费公开接口（有道演示接口 / MyMemory / Google 网页接口，自动回退），
  亦可由用户配置 OpenAI 兼容接口 / DeepL / 百度翻译 / 自定义接口；本项目不内置任何密钥。
