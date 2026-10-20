"""Stage the Qwen GGUF, frozen CPU engine and redistribution notices."""
from pathlib import Path
import argparse,hashlib,json,shutil,sys
p=argparse.ArgumentParser()
for name in ['model','engine','venv','license','wheel','llama-license']:p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();root=Path(__file__).resolve().parent
def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
expected='6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e'
expected_wheel='a7ff33fd7f76f4d747f13e032c66747c3c8b05b4ea185609c6b133032ec7249a'
assert sha(a.model)==expected,'Qwen model SHA-256 mismatch'
assert sha(a.wheel)==expected_wheel,'llama-cpp-python CPU wheel SHA-256 mismatch'
engine=root/'engine';assert (a.engine/'QingTranslate.exe').is_file();shutil.copytree(a.engine,engine,dirs_exist_ok=True)
for name in ['msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll']:
    shutil.copy2(root.parent.parent/'runtimes/win-x64'/name,engine/'_internal'/name)
model=root/'model';model.mkdir(parents=True,exist_ok=True)
filename='qwen2.5-1.5b-instruct-q4_k_m.gguf';shutil.copy2(a.model,model/filename)
metadata={'format':'gguf','from':'en','to':'zh-CN','file':filename,'sha256':expected,'model':'Qwen2.5-1.5B-Instruct Q4_K_M','revision':'91cad51170dc346986eccefdc2dd33a9da36ead9','license':'apache-2.0'}
(model/'manifest.json').write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf-8')
notices=root.parent.parent/'licenses/qwen';notices.mkdir(parents=True,exist_ok=True)
assert 'Apache License' in a.license.read_text(encoding='utf-8',errors='replace')
shutil.copy2(a.license,notices/'QWEN-LICENSE.txt');shutil.copy2(a.llama_license,notices/'LLAMA-CPP-PYTHON-LICENSE.md')
shutil.copy2(a.llama_license,notices/'GGML-CPU-LICENSE.txt');shutil.copy2(Path(sys.base_prefix)/'LICENSE.txt',notices/'Python-LICENSE.txt')
site=a.venv/'Lib/site-packages'
for pattern in ['llama_cpp_python-*.dist-info','diskcache-*.dist-info','jinja2-*.dist-info','markupsafe-*.dist-info','numpy-*.dist-info','typing_extensions-*.dist-info','pyinstaller-*.dist-info']:
    for distribution in site.glob(pattern):shutil.copytree(distribution,notices/distribution.name,dirs_exist_ok=True)
runtime_files=[f for directory in [engine,model] for f in directory.rglob('*') if f.is_file()]
runtime={'files':{f.relative_to(root).as_posix():sha(f) for f in sorted(runtime_files)},'python':sys.version,'backend':'llama.cpp 0.3.19 CPU, 8 threads; no CUDA/Metal/Vulkan','model':metadata,'worker_source_sha256':sha(root/'worker.py')}
(root/'runtime-manifest.json').write_text(json.dumps(runtime,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'runtime_files':len(runtime_files),'model_bytes':(model/filename).stat().st_size,'sha256':expected}))
