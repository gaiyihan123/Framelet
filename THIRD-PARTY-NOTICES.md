# 第三方组件

轻截图的桌面界面、截图、拼接和对照排版代码为本项目实现，没有复制 Snipaste 或 Yoink 的代码、图标及品牌资源。

- Tesseract .NET wrapper 5.2.0：Apache-2.0，https://github.com/charlesw/tesseract 。安装目录附许可证。
- Tesseract OCR 5.2.0：Apache-2.0，https://github.com/tesseract-ocr/tesseract 。通过上述 NuGet 包提供 Windows 本机库。
- Leptonica 1.82.0：BSD 2-Clause，https://github.com/DanBloomberg/leptonica 。安装目录附许可证。
- tessdata_fast 英语模型：Apache-2.0，https://github.com/tesseract-ocr/tessdata_fast 。模型 SHA-256：7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2。
- .NET 10 / WPF：Microsoft .NET 的许可证与 THIRD-PARTY-NOTICES 随自包含发布文件分发，https://github.com/dotnet/runtime 。
- Microsoft Visual C++ x64 Runtime 14.51.36247.0：随程序分发微软签名的 msvcp140.dll、vcruntime140.dll、vcruntime140_1.dll；仅放在程序目录，不运行系统安装器。哈希见 `vc-runtime-manifest.json`，原始来源与许可说明见 `licenses/Microsoft-VC-Runtime-NOTICE.txt` 和 `Microsoft-Visual-Studio-Redist.txt`。https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution 。
- Inno Setup：安装器由 Inno Setup 6.7.3 构建，https://jrsoftware.org/ 。
- Inno Setup 简体中文语言文件：Zhenghan Yang (Kira)，保留文件头归属，https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation 。

1.3.0 不调用 MyMemory 或其他在线翻译服务。获取本安装包可能需要联网；安装后的翻译不依赖网络。

## 离线翻译组件

- Qwen 是离线主译；检测到整段仍为英文或长段照抄原文时，Argos 会在本机重试。因此随包保留 Argos、CPU CTranslate2、Ruy、SentencePiece、Python、NumPy 和其原有许可条款。
- Qwen2.5-1.5B-Instruct Q4_K_M GGUF revision `91cad51170dc346986eccefdc2dd33a9da36ead9`：上游标注 Apache-2.0；未修改权重。模型许可与 SHA-256 见 `offline/qwen/MODEL-NOTICE.md` 和 `offline/qwen/model/manifest.json`。
- llama-cpp-python CPU wheel 0.3.19：MIT。打包 Python 扩展和 GGML CPU DLL，完整元数据与上游许可文件位于 `licenses/qwen/`。未包含 CUDA、cuDNN 或 GPU 推理库。
- Qwen 出现整句仍为英语时，程序可用下面保留的 Argos CPU 模型作为本机后备。后备仅用于防止“未翻译内容”静默显示为译文。
- Argos en→zh 1.9 模型：仅作整段英语或长段照抄原文时的本机后备。包内原始 README 注明上游 OPUS 模型为 CC-BY 4.0；作者 Jörg Tiedemann、Santhosh Thottingal。详见 `offline/MODEL-NOTICE.md`、`licenses/CC-BY-4.0.txt`。
- CTranslate2 4.6.0：MIT，https://github.com/OpenNMT/CTranslate2 。从提交 617405f4b050e994e829d527da6caa0e0030afe7 自建 CPU-only Ruy 后端，不分发官方 wheel 的 cuDNN、CUDA、MKL 或 Intel OpenMP 库。Ruy/cpu_features 为 Apache-2.0，cpuinfo 为 BSD-style，spdlog/fmt、BS thread pool 为 MIT；完整原始许可及 math header 归属保留在 `licenses/offline/`。
- SentencePiece 0.2.1：Apache-2.0，https://github.com/google/sentencepiece 。
- CPython 3.13.15：PSF，内嵌于轻截图主进程，不使用最终用户的 Python 环境，也不启动翻译辅助程序。`offline/embedded/python313.zip` 来自官方同版本 embeddable distribution，许可随包保留。
- NumPy 2.5.3：BSD-3-Clause，另含其原包列出的 OpenBLAS 等第三方许可。
- PyYAML 6.0.3、setuptools 80.9.0：MIT，保留原包及其捆绑组件许可。
- PyInstaller 6.16.0：GPL 加 Bootloader Exception，许可明确允许将未修改的 bootloader 与程序结合分发；运行时 hooks 另受原文所列许可约束。

上述包的完整原始 distribution metadata、嵌套 licenses、Python 许可及 PyInstaller COPYING/例外条款位于 `licenses/offline/`。本应用使用 Argos 模型和 CTranslate2 后端，未分发完整 Argos Python 应用、LibreTranslate 服务或 Stanza 模型。无上游背书含义。许可资料核对不等同于正式法律意见。
