# Pinshot

Setuna 式**截图贴图工具**，附文字提取（OCR）与翻译。贴图即产品本体：
无主窗口，启动后驻留系统托盘，按下热键框选屏幕，截图原位钉在桌面上。

> 贴图交互（置顶、缩放、双击收纳、显隐全部等）设计参考 [Setuna2](https://github.com/tylearymf/Setuna2)（MIT），
> 为独立实现的 WPF 版本；文字提取默认使用 PaddleOCR 本地离线引擎（模型内嵌，无需安装）。

## 功能

- **截图贴图**：框选屏幕任意区域 → 原位生成置顶贴图（默认热键 Alt+Shift+P，可在设置修改）；
  滚轮缩放、Ctrl+滚轮调透明度、双击收纳为小图、方向键微移、拖动时半透明；
  多显示器与不同 DPI 支持，拔掉显示器贴图自动回归工作区，重启后可恢复全部贴图。
- **自研截图引擎**：悬停自动吸附窗口/控件、拖拽选区、全屏十字光标、放大镜、可选保留鼠标指针；
  新建截图时已有贴图自动遮蔽（DWM cloak），不会被截进新图。
- **文字提取**：PaddleOCR 本地离线引擎（中文/数字/英文识别率高；短边 <1000 的截图自动放大
  2~3 倍后再识别，编辑器标签/菜单等密集小字丢字符明显减少），不可用时自动回退 Windows 系统 OCR；
  识别语言支持 **中/英/日/韩/俄** 五种（Windows OCR 语言包由应用启动时自动批量装齐，无需手动安装）。
  OCR 引擎**懒加载**：启动不加载引擎（省内存），首次识别才初始化（慢几秒），空闲 1 分半自动销毁释放内存。
- **翻译（图上原位）**：识别文字块后逐块翻译，译文按原文位置覆盖在图片上；翻译期间显示旋转 loading；
  覆盖块锁定原行框（不叠加）、随贴图缩放同步缩放；失败行自动补译；原文已是目标语言的行原样覆盖。
- **命令/代码逐词对照**：命令行/代码/URL 及混排行中翻不出的片段，自动提取英文单词
  （驼峰命名拆词）查词典，生成「词=译」对照表；命令本身不改写。
- **对照翻译窗口**：与提取文字窗口同款结构，上半部分为提取原文、分隔线下方为译文；
  设置中可勾选 **显示原文 / 显示译文** 自由切换。
- **图片标注 / 修剪**：画笔 / 文本 / 马赛克（粗细、8 种颜色、Ctrl+Z 撤回、Enter 应用、退出放弃）；
  修剪模式拖出选区后 Enter 应用。
- **贴图管理**：从文件 / 网页图片创建贴图（PNG/JPEG/BMP/GIF/ICO/TIFF/WebP/PSD/TGA/SVG）；
  右键菜单复制/剪切/粘贴/另存为、旋转/翻转、收缩、修剪、缩放、透明度、边框；
  隐藏/显示所有贴图、参考图名单与回收站、开机自启动。
- **翻译服务**：默认免费接口链（**有道 → MyMemory → Google** 自动回退，无需任何配置）；
  可切换 **OpenAI 兼容接口**（OpenAI / 智谱 GLM / DeepSeek / Ollama）、**DeepL**、**百度翻译** 或自定义接口。
- **设置窗口**：Setuna 式左侧分组导航：常规设置 / 截图设置 / 截图右键菜单 / 翻译与提取文字 / 系统托盘菜单，
  保存后立即生效（热键重新注册）。

配置保存在 `%APPDATA%\Pinshot\config.json`；崩溃与翻译失败日志在 `%APPDATA%\Pinshot\crash.log`。

## 分发

发给别人只需 **`publish\Pinshot.exe` 一个文件**（自包含：.NET 运行时、PaddleOCR 模型、
native 依赖全部内嵌，首次启动自动释放到本地）。重新生成发布版：

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

发布后若出现 `publish\inference` 文件夹可直接删除（模型已内嵌，运行时会按需重建）。
注意把 exe 放在**有写入权限的目录**（首次启动会在 exe 旁边释放模型，并在 `%APPDATA%\Pinshot` 写配置），
不要直接放 Program Files 这类受保护目录。

## 构建

需要 .NET 10 SDK（Windows）：

```
dotnet build -c Release
```

## 项目结构

```
App.xaml.cs                 启动：单实例、托盘、全局热键、截图入口、OCR 语言包批量安装
Core/Config.cs              配置（JSON 持久化）
Core/PinManager.cs          贴图管理：创建/显隐/激活/关闭、截图避让
Core/Win32.cs               P/Invoke 底座：DWM 遮蔽、物理坐标窗口、DPI、显示器
Core/OcrService.cs          OCR 服务（PaddleOCR 优先，自动回退 Windows OCR；小图自动放大）
Core/PaddleOcrEngine.cs     PaddleOCR 本地引擎封装（模型/native 依赖内嵌释放）
Core/TranslateService.cs    翻译：免费接口链（有道/MyMemory/Google/百度/自定义）+ OpenAI 兼容 / DeepL
Views/PinWindow.*           贴图窗口（Setuna 行为核心、图上原位翻译、loading 遮罩）
Views/ExtractPanelWindow.*  提取文字面板（微信式）
Views/TranslateWindow.*     原文译文对照窗口（提取窗口同款 + 译文区，可配置显示原文/译文）
Views/ResultWindow.*        OCR/翻译结果浮窗
Views/SettingsWindow.*      设置窗口
```

## 许可

MIT（见 `LICENSE`）。第三方依赖与致谢见 `NOTICE.md`。
