"""Stage only verified model files, the built helper and redistribution notices."""
from pathlib import Path
from zipfile import ZipFile
import argparse, hashlib, json, shutil, sys

p=argparse.ArgumentParser()
p.add_argument('--archive',type=Path,required=True)
p.add_argument('--engine',type=Path,required=True)
p.add_argument('--venv',type=Path,required=True)
p.add_argument('--cc-license',type=Path,required=True)
p.add_argument('--cpu-source',type=Path,required=True)
a=p.parse_args()
root=Path(__file__).resolve().parent
sha=lambda f:hashlib.sha256(f.read_bytes()).hexdigest()
expected='433e7c4f034d87fbe2353161e05f18646d7999452f801a4e1f0378522b9850ab'
assert sha(a.archive)==expected, 'Unexpected model package hash'
model=root/'model';model.mkdir(exist_ok=True)
files=['metadata.json','sentencepiece.model','README.md','model/shared_vocabulary.json','model/model.bin','model/config.json']
with ZipFile(a.archive) as z:
    for name in files:
        output=model/name;output.parent.mkdir(parents=True,exist_ok=True)
        output.write_bytes(z.read('translate-en_zh-1_9/'+name))
manifest={'from':'en','to':'zh','package':'1.9','source_sha256':expected,'files':{name:sha(model/name) for name in files}}
(model/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
assert (a.engine/'QingTranslate.exe').is_file()
assert not list(a.engine.rglob('cudnn*.dll')) and not list(a.engine.rglob('libiomp5*.dll')), 'GPU/Intel runtime must not ship'
assert not list((root/'engine').rglob('cudnn*.dll')), 'Use a clean CPU staging directory; preserve old staging outside the source tree'
shutil.copytree(a.engine,root/'engine',dirs_exist_ok=True)
# Use the same verified Microsoft CRT distribution as the rest of the app,
# instead of DLLs PyInstaller happened to find on the developer PATH.
for name in ['msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll']:
    shutil.copy2(root.parent/'runtimes/win-x64'/name,root/'engine/_internal'/name)
notices=root.parent/'licenses/offline';notices.mkdir(parents=True,exist_ok=True)
site=a.venv/'Lib/site-packages'
for metadata in site.glob('*.dist-info'):
    # Keep full distribution metadata, including nested bundled license texts.
    shutil.copytree(metadata,notices/metadata.name,dirs_exist_ok=True)
python_license=Path(sys.base_prefix)/'LICENSE.txt'
assert python_license.exists(),'Missing Python license'
shutil.copy2(python_license,notices/'Python-LICENSE.txt')
assert 'Attribution 4.0' in a.cc_license.read_text(encoding='utf-8')
shutil.copy2(a.cc_license,root.parent/'licenses/CC-BY-4.0.txt')
shutil.copy2(root/'MODEL-NOTICE.md',notices/'MODEL-NOTICE.md')
shutil.copy2(a.cpu_source/'LICENSE',notices/'CTranslate2-LICENSE.txt')
for folder in [a.cpu_source/'third_party/spdlog',a.cpu_source/'third_party/cpu_features',a.cpu_source/'third_party/ruy']:
    for file in folder.rglob('*'):
        if file.is_file() and (file.name.upper().startswith(('LICENSE','COPYING','NOTICE'))) and '.git' not in file.parts:
            dest=notices/'cpu-backend'/file.relative_to(a.cpu_source)
            dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(file,dest)
for name in ['avx_mathfun.h','avx512_mathfun.h','neon_mathfun.h','BS_thread_pool_light.hpp']:
    shutil.copy2(a.cpu_source/'third_party'/name,notices/name)
from urllib.request import urlopen
for name,url in {
    'SentencePiece-LICENSE.txt':'https://raw.githubusercontent.com/google/sentencepiece/v0.2.1/LICENSE',
    'BS-thread-pool-LICENSE.txt':'https://raw.githubusercontent.com/bshoshany/thread-pool/v3.5.0/LICENSE.txt',
    'OpenSSL-LICENSE.txt':'https://raw.githubusercontent.com/openssl/openssl/openssl-3.0.0/LICENSE.txt'
}.items():
    with urlopen(url,timeout=30) as response: (notices/name).write_bytes(response.read())
runtime_files=[f for folder in [root/'engine',root/'model'] for f in folder.rglob('*') if f.is_file()]
runtime_manifest={'files':{f.relative_to(root).as_posix():sha(f) for f in sorted(runtime_files)},'python':sys.version,'source':'offline/worker.py','worker_source_sha256':sha(root/'worker.py'),'backend':'CTranslate2 4.6.0 CPU-only Ruy; no CUDA/cuDNN/MKL/Intel OpenMP','upstream_commit':'617405f4b050e994e829d527da6caa0e0030afe7'}
(root/'runtime-manifest.json').write_text(json.dumps(runtime_manifest,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'runtime_files':len(runtime_files),'runtime_bytes':sum(f.stat().st_size for f in runtime_files),'model_sha256':expected}))
