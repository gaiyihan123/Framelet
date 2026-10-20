param([string]$Python='python',[Parameter(Mandatory=$true)][string]$WorkDirectory,[Parameter(Mandatory=$true)][string]$CMake,[string]$Generator='Visual Studio 18 2026')
$ErrorActionPreference='Stop'
$taskWork=[IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Force $taskWork | Out-Null
& $Python -c 'import sys; assert sys.version_info[:2] == (3,13), "Use Python 3.13 x64"'
if($LASTEXITCODE -ne 0){throw 'Python prerequisite failed'}
& $Python -m venv (Join-Path $taskWork 'venv')
if($LASTEXITCODE -ne 0){throw 'venv failed'}
$taskPython=Join-Path $taskWork 'venv\Scripts\python.exe'
& $taskPython -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements-build.txt')
if($LASTEXITCODE -ne 0){throw 'Pinned dependencies failed'}
& (Join-Path $PSScriptRoot 'build-cpu.ps1') -WorkDirectory $taskWork -CMake $CMake -Generator $Generator
$taskArchive=Join-Path $taskWork 'translate-en_zh-1_9.argosmodel'
if(-not(Test-Path -LiteralPath $taskArchive)){Invoke-WebRequest 'https://argos-net.com/v1/translate-en_zh-1_9.argosmodel' -OutFile $taskArchive}
if((Get-FileHash -LiteralPath $taskArchive).Hash.ToLowerInvariant() -ne '433e7c4f034d87fbe2353161e05f18646d7999452f801a4e1f0378522b9850ab'){throw 'Model hash mismatch'}
$taskLicense=Join-Path $taskWork 'CC-BY-4.0.txt'
Invoke-WebRequest 'https://creativecommons.org/licenses/by/4.0/legalcode.txt' -OutFile $taskLicense
& $taskPython -m PyInstaller --noconfirm --onedir --console --name QingTranslate --collect-all ctranslate2 --collect-all sentencepiece --copy-metadata numpy --copy-metadata setuptools --copy-metadata pyyaml --distpath (Join-Path $taskWork 'dist-cpu') --workpath (Join-Path $taskWork 'pyi-cpu-build') --specpath $taskWork (Join-Path $PSScriptRoot 'worker.py')
if($LASTEXITCODE -ne 0){throw 'Worker build failed'}
& $taskPython -X utf8 (Join-Path $PSScriptRoot 'prepare.py') --archive $taskArchive --engine (Join-Path $taskWork 'dist-cpu\QingTranslate') --venv (Join-Path $taskWork 'venv') --cc-license $taskLicense --cpu-source (Join-Path $taskWork 'ct2-cpu')
if($LASTEXITCODE -ne 0){throw 'Model/runtime staging failed'}
# Frozen helpers are build-time staging only. Publish one shared native Python
# runtime and keep generated helper/package duplicates recoverable outside P.
$taskNativeArchive=Join-Path $taskWork ('native-staging-before-'+[Guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'stage-native.ps1') -ArchiveLegacyEngines -ArchiveDirectory $taskNativeArchive
