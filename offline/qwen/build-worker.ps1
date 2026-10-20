param([string]$Python='python',[Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
$taskWork=[IO.Path]::GetFullPath($WorkDirectory);New-Item -ItemType Directory -Force $taskWork | Out-Null
& $Python -c 'import sys; assert sys.version_info[:2] == (3,13), "Use Python 3.13 x64"'
if($LASTEXITCODE -ne 0){throw 'Python prerequisite failed'}
$taskVenv=Join-Path $taskWork 'qwen-venv';if(-not(Test-Path -LiteralPath $taskVenv)){& $Python -m venv $taskVenv;if($LASTEXITCODE -ne 0){throw 'Qwen venv failed'}}
$taskPy=Join-Path $taskVenv 'Scripts\python.exe'
$taskWheel=Join-Path $taskWork 'llama_cpp_python-0.3.19-cp313-cp313-win_amd64.whl'
if(-not(Test-Path -LiteralPath $taskWheel)){$taskWheel=Join-Path $taskWork 'downloads\llama_cpp_python-0.3.19-cp313-cp313-win_amd64.whl'}
if(-not(Test-Path -LiteralPath $taskWheel)){Invoke-WebRequest 'https://github.com/abetlen/llama-cpp-python/releases/download/v0.3.19/llama_cpp_python-0.3.19-cp313-cp313-win_amd64.whl' -OutFile $taskWheel}
if((Get-FileHash $taskWheel).Hash.ToLowerInvariant() -ne 'a7ff33fd7f76f4d747f13e032c66747c3c8b05b4ea185609c6b133032ec7249a'){throw 'llama-cpp-python wheel hash mismatch'}
& $taskPy -m pip install --disable-pip-version-check --no-deps $taskWheel;if($LASTEXITCODE -ne 0){throw 'llama-cpp-python install failed'}
& $taskPy -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'build-requirements.txt');if($LASTEXITCODE -ne 0){throw 'Pinned Qwen dependency install failed'}
$taskModel=Join-Path $taskWork 'qwen2.5-1.5b-instruct-q4_k_m.gguf'
if(-not(Test-Path -LiteralPath $taskModel)){$taskModel=Join-Path $taskWork 'downloads\qwen2.5-1.5b-instruct-q4_k_m.gguf'}
if(-not(Test-Path -LiteralPath $taskModel)){Invoke-WebRequest 'https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/91cad51170dc346986eccefdc2dd33a9da36ead9/qwen2.5-1.5b-instruct-q4_k_m.gguf?download=true' -OutFile $taskModel}
if((Get-FileHash $taskModel).Hash.ToLowerInvariant() -ne '6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e'){throw 'Qwen GGUF hash mismatch'}
$taskModelNotice=Join-Path $taskWork 'QWEN-LICENSE.txt';if(-not(Test-Path -LiteralPath $taskModelNotice)){Invoke-WebRequest 'https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/91cad51170dc346986eccefdc2dd33a9da36ead9/LICENSE' -OutFile $taskModelNotice}
$taskLlamaNotice=Join-Path $taskWork 'LLAMA-LICENSE.md';if(-not(Test-Path -LiteralPath $taskLlamaNotice)){Invoke-WebRequest 'https://raw.githubusercontent.com/abetlen/llama-cpp-python/v0.3.19/LICENSE.md' -OutFile $taskLlamaNotice}
& $taskPy -m PyInstaller --noconfirm --onedir --console --name QingTranslate --collect-all llama_cpp --copy-metadata llama-cpp-python --copy-metadata diskcache --copy-metadata jinja2 --copy-metadata numpy --copy-metadata typing_extensions --distpath (Join-Path $taskWork 'qwen-dist') --workpath (Join-Path $taskWork 'qwen-pyi') --specpath $taskWork (Join-Path $PSScriptRoot 'worker.py')
if($LASTEXITCODE -ne 0){throw 'Qwen worker freeze failed'}
& $taskPy -X utf8 (Join-Path $PSScriptRoot 'prepare.py') --model $taskModel --engine (Join-Path $taskWork 'qwen-dist\QingTranslate') --venv $taskVenv --license $taskModelNotice --wheel $taskWheel --llama-license $taskLlamaNotice
if($LASTEXITCODE -ne 0){throw 'Qwen staging failed'}
# Do not reintroduce the old frozen-helper distribution into the application.
$taskNativeArchive=Join-Path $taskWork ('qwen-native-staging-before-'+[Guid]::NewGuid().ToString('N'))
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'stage-native.ps1') -ArchiveLegacyEngines -ArchiveDirectory $taskNativeArchive
