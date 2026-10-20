# Offline primary translation model

Model: Qwen2.5-1.5B-Instruct, Q4_K_M GGUF.
Upstream revision: `91cad51170dc346986eccefdc2dd33a9da36ead9`.
Source: https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF
Model file SHA-256: `6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e`
License listed by the upstream repository: Apache License 2.0. The full upstream license is retained at `licenses/qwen/QWEN-LICENSE.txt`.

The model weights are unchanged; the Q4_K_M quantized upstream file is stored at `offline/qwen/model/` and served by the CPU llama.cpp runtime. Qwen is primary. The Argos English→Chinese model remains packaged locally as a secondary offline provider only for responses that contain no Chinese text. No text is sent over the Internet, and no model is automatically downloaded.

llama-cpp-python 0.3.19 and GGML CPU files are included under their MIT terms; original notices are retained in licenses/qwen/.

No endorsement by Qwen or llama.cpp authors is implied. Machine translation is not certified translation; dates, amounts, negation and specialized terms should still be checked against the English source.
