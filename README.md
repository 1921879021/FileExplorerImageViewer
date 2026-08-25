# FileExplorerImageViewer (ImagePeek)

> 点击文件资源管理器中的图片，右侧即刻预览

在文件资源管理器中选中图片，右侧预览窗格**立即显示**——包括系统原生无法预览的格式（16 位 PNG、WebP、HEIC、AVIF、JXL、SVG、PSD 等）。单个便携 exe，发给别人双击即可启用，无需安装。

## ✨ 特性

- **原生集成**：以 COM 预览处理器（Preview Handler）形式嵌入资源管理器预览窗格，点击即显
- **29 种格式**：GDI+（jpg/png/gif/bmp/tiff/ico）+ Magick.NET 兜底（webp/avif/heic/heif/jxl/jp2/svg/psd/exr/hdr/tga/pnm…）
- **相邻预加载**：进程内 LRU 缓存 + 后台预解码，连续点击零等待
- **安全隔离**：处理器运行于系统代理进程 prevhost.exe，崩溃不影响资源管理器
- **单文件分发**：解码组件 gzip 内嵌于 exe，复制到任何位置都能运行
- **快速查看器**：`ImagePeek.exe <图片>` 打开，←/→ 切换、滚轮缩放、双击 100%

## 📦 使用

1. 下载/构建 `ImagePeek.exe`
2. 双击运行 → 点击「一键启用预览」→ UAC 确认（仅一次）
3. 资源管理器按 `Alt+P` 打开预览窗格 → 点击任意图片

命令行：

| 命令 | 作用 |
|---|---|
| `ImagePeek.exe --enable` | 启用预览集成 |
| `ImagePeek.exe --disable` | 卸载，恢复系统默认 |
| `ImagePeek.exe --status` | 查看注册状态 |

## 🔨 构建

环境：Windows 10 1903+ / Win11，.NET SDK 8+（构建用，运行时仅依赖系统自带的 .NET Framework 4.8）

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# 产物：src/ImagePeek/bin/Release/net48/ImagePeek.exe
```

## 🧱 实现原理

三层结构：**主程序（注册工具/查看器）+ 解码核心（GDI+ → Magick.NET 两级降级）+ COM 预览处理器**。

处理器以 .NET Framework 托管 COM 的「版本化 InprocServer32 + mscoree 垫片」协议注册进
HKLM，由 prevhost.exe 代理进程加载；渲染控件运行在专用 STA 线程（规避宿主 MTA 线程
无法绘制 WinForms 的问题）；解码组件按版本目录释放到 `%LocalAppData%\ImagePeek`。

详细设计、关键代码与踩坑记录见 [实现文档.md](实现文档.md)。

## 📁 目录结构

```
src/
├── ImagePeek.Core/       解码核心（GDI+ / Magick.NET）+ 注册逻辑
├── ImagePeek.Preview/    COM 预览处理器（IPreviewHandler）
├── ImagePeek/            主程序（WPF：设置界面 + 快速查看器）
└── ImagePeek.TestHost/   自动化验证（COM 激活 / 代理渲染 / 像素检查）
```

## ⚠️ 已知限制

- 启用需一次 UAC 管理员确认（托管 COM 版本化注册只认 HKLM，系统限制）
- 仅支持 64 位系统
- 动图（GIF/WebP 动画）显示第一帧；RAW 相机格式为后续计划

## 📄 许可

本项目仅供学习交流使用。依赖的第三方库：Magick.NET（Apache-2.0）、NetVips（LGPL，未使用）。
