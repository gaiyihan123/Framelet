# Framelet · F capture frame

2026-10-08 更新。F 的上横、竖笔和中横配合右下取景角，表达 Framelet 和截图；删除原 QingCapture 的 Q 图形。应用图标沿用雾蓝紫 `#626FA6` 底色及柔白 `#FAFBFF` 图形，没有渐变、额外描边或阴影。

- `icon.svg`：512 × 512 大尺寸应用图标，较宽留白及2.8单位主笔画。
- `icon-small.svg`：20–40px光学校正版本，稍扩底板、3单位笔画和开放间隙。
- `icon-tiny.svg`：16px独立像素网格，2px实体笔画；避免小尺寸F和右下角糊在一起。
- `mark.svg`：无底板的单色F取景标记，与首页矢量标识同源。
- `icon-1024.png`、`png/`及`../../src/icon.ico`：从SVG生成的透明PNG和10尺寸ICO（16/20/24/32/40/48/64/96/128/256）。

首页左上角使用20DIP的无底板矢量F标记，与14DIP Segoe UI SemiBold的Framelet字标相隔9DIP；颜色为雾蓝紫和`#414960`。不再把完整方形应用图标缩成标题装饰。WPF矢量在各显示缩放下重新渲染；窗口、任务栏、托盘、EXE与安装器共用同一多帧ICO。

不含第三方品牌、字体文件或图片。SVG和WPF路径直接实现，不使用位图图像生成服务，也不增加软件运行依赖。

重新生成：`node assets/brand/generate-icons.cjs`。可传`--sharp-module <已有sharp目录>`，使用现有Node/sharp；构建软件只需生成并保存的ICO，不需要安装设计工具。
