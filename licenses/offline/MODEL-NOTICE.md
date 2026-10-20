# Offline translation model attribution

Model: Argos Translate English → Chinese, package 1.9.
Official package index: https://github.com/argosopentech/argospm-index
Package URL: https://argos-net.com/v1/translate-en_zh-1_9.argosmodel
Package SHA-256: 433e7c4f034d87fbe2353161e05f18646d7999452f801a4e1f0378522b9850ab

The original README in the package states that the original OPUS model from
which this packaged model is derived is licensed under CC-BY 4.0.
Authors: Jörg Tiedemann and Santhosh Thottingal.
Citation: “OPUS-MT — Building open translation services for the World”,
Proceedings of the 22nd Annual Conference of the European Association for
Machine Translation (EAMT), Lisbon, Portugal, 2020.
License: https://creativecommons.org/licenses/by/4.0/
Full license: licenses/CC-BY-4.0.txt in the app installation.

Changes in QingCapture: no changes to the model weights or SentencePiece file.
The optional Stanza segmentation files are not distributed or used. A local,
bounded sentence/token splitting layer is used instead. Model files are loaded
by a CPU-only CTranslate2 4.6.0 build (Ruy, no CUDA/cuDNN/MKL/Intel OpenMP)
in int8 mode with SentencePiece 0.2.1. The embedded
model configuration selects cmn_Hans. Results are normalized to simplified
Chinese by the Windows locale API. This is a direct use of an Argos model and
its backend, not a bundled installation of the full Argos Python application.

Argos project reference: https://github.com/argosopentech/argos-translate
CTranslate2: https://github.com/OpenNMT/CTranslate2 (MIT).
CPU backend source commit: 617405f4b050e994e829d527da6caa0e0030afe7.
Ruy and cpu_features: Apache-2.0; cpuinfo: BSD-style; spdlog/fmt: MIT.
BS thread pool: https://github.com/bshoshany/thread-pool (MIT).
Bundled math headers and backend dependency notices are retained in licenses/offline.
SentencePiece: https://github.com/google/sentencepiece (Apache-2.0).
Python: PSF license. NumPy: BSD-3-Clause plus bundled dependency notices.
PyYAML and setuptools: MIT. PyInstaller bootloader: GPL with distribution
exception; its bundled license and exception are retained with runtime notices.

No endorsement by these authors/projects is implied. This is machine
translation, not a certified translation service. Dates, numbers, negation,
names and specialized terminology must be checked against the source text.
