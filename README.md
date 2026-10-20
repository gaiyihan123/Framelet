# Framelet

Windows 11 x64 中文截图工具，支持截图标注、置顶贴图、滚动长截图，以及英语到简体中文的离线图片翻译。

## 下载与安装

到 [Releases](https://github.com/gaiyihan123/Framelet/releases/latest) 下载 `Framelet-1.3.0-win-x64-Setup.exe`。安装包包含模型和运行环境，安装后无需另外下载模型或配置 Python。

默认安装到当前用户的程序目录。升级保留快捷键和开机启动设置；已有程序运行时，从系统托盘退出 Framelet，再重新打开以加载新版。

当前发布为 **1.3.0 / 2026-10-20 编辑窗口最大化修复**（标签 `v1.3.0-20261020-editor-maximize`）。截图后的编辑窗口可最大化、还原，主界面继续保持小窗口。安装包约 1.25 GB，主要是随包提供的离线翻译模型。

## 使用

- **截图**：点击选择鼠标下的窗口，拖动框选区域，Enter 全屏，Esc 或右键取消。尚未点击时显示像素放大镜，C 复制当前颜色，Shift 切换 RGB/HEX。
- **编辑**：支持箭头、框线、文字、马赛克、撤销和重做。右上角可最大化、还原编辑窗口，也可双击标题栏切换；主界面保持小窗口。调整选区或标注时自动更新剪贴板；工具条和选框不会进入导出图片。自动复制后可在支持图片的聊天或文档中按 Ctrl+V 粘贴。
- **长截屏**：先框选正文宽高，保持左键按住，继续向下拖动或使用滚轮。首次滚轮会锁定当时的原选框，不自动把下面的输入框圈进来；松手完成并停止继续滚动。衔接不可靠时暂停下滚，可上滚一点找回上一段，或松手保留已确认内容。
- **图片翻译**：截图后在原画面旁显示译文，全文连续排版，可修改或复制文字；长图也可生成原文与译文对照图。
- **贴图与快捷键**：剪贴板图片可置顶，截图、窗口截图、全屏、长截屏、截图翻译和贴图快捷键可在设置中配置。

英语 OCR 使用 Tesseract；翻译使用本机 Qwen2.5-1.5B Q4，Argos 为本地后备。OCR 和翻译不上传图片或文字，不调用在线翻译 API。模型按需加载，真实推理结束后闲置 30 秒自动释放。

## 本次长截图修复

- 单行、双行文字和大片空白能够通过原宽像素验证衔接；固定细竖线不再掩盖正文的实际移动。
- 平滑滚动保留可靠的中间画面，并由下一帧确认，避免一轮滚动结束后已经没有重叠，也避免把往复动画拼成正文。
- 无法衔接时暂停继续下滚；松手取消尚未发送的积压滚轮，只等待当前画面收尾。
- 最后一次长截图可在本机保存少量诊断元数据，不保存截图、聊天正文或窗口标题。

已经完成的验证与适用范围见 [docs/VERIFICATION.md](docs/VERIFICATION.md)。实际用户窗口与物理鼠标的行为仍需现场复测；纯空白或完全重复的内容无法保证推断出可靠位移。

## 源码构建

环境：Windows 11 x64、.NET 10 SDK；制作安装包另需 Inno Setup 6.7.3 或更新版本。

Git 仓库保存源码、依赖锁定文件、模型及运行库完整性清单。模型和运行库较大，另放在同版本 Release 的 `Framelet-1.3.0-runtime-assets-win-x64.zip` 中。

1. 克隆仓库。
2. 从同版本 [运行资源 Release](https://github.com/gaiyihan123/Framelet/releases/tag/v1.3.0-20261020) 下载运行资源 ZIP 和 `SHA256SUMS.txt`，用 `Get-FileHash -Algorithm SHA256` 核对。此次编辑窗口修复没有更改模型或运行库，继续复用该资源 ZIP。
3. 在仓库根目录解压运行资源，恢复 `offline/native`、`offline/embedded`、`offline/model`、`offline/qwen/model` 和 `runtimes/win-x64` 的原目录结构。
4. 构建：

```powershell
Expand-Archive .\Framelet-1.3.0-runtime-assets-win-x64.zip -DestinationPath . -Force
.\build.ps1 -Dotnet dotnet -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

仅构建和验证应用时使用 `-SkipInstaller`。`build.ps1` 会先检查资源清单、哈希和原生运行库签名，再发布自包含应用并运行验收。默认发布到 `D:\QingCapture\build\publish`、安装包到 `D:\QingCapture\dist`；没有 D 盘时使用仓库内 `build` 和 `dist`。也可通过 `-BuildDirectory`、`-OutputDirectory` 指定目录。

专项入口示例：

```powershell
.\QingCapture.exe --ui-self-test --scroll-stability-only --report-dir .\reports\scroll
.\QingCapture.exe --ui-self-test --layout-only --report-dir .\reports\layout
.\QingCapture.exe --ui-self-test --report-dir .\reports\ui
.\QingCapture.exe --self-test --browser-translation-test --report-dir .\reports\full
```

这些命令在已发布目录运行。验收默认不加载个人设置；源码没有捆绑用户聊天截图。重放外部截图只通过显式 `--recorded-chat-scroll --image-path` 参数开启。

## 第三方资源

依赖和模型的许可证、来源及说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、`licenses/` 和 `offline/*NOTICE.md`。模型与运行库下载后仍由清单固定校验。
