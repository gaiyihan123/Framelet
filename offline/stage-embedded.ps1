param(
  [Parameter(Mandatory=$true)][string]$SourceSite,
  [Parameter(Mandatory=$true)][string]$PythonEmbedRoot,
  [string]$CpuTranslationLibrary,
  [string]$CpuTranslationSha256='37973537c82564e7d01feb5fb9cd0e456749bf079db8a5474bfe0aacfd9206ef'
)
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$taskTarget=Join-Path $taskRoot 'embedded'
$taskSite=[IO.Path]::GetFullPath($SourceSite)
$taskPython=[IO.Path]::GetFullPath($PythonEmbedRoot)
if(Test-Path -LiteralPath $taskTarget){throw 'The embedded runtime target already exists; stage into a clean checkout.'}
if(-not(Test-Path -LiteralPath (Join-Path $taskSite 'llama_cpp\llama.py')) -or -not(Test-Path -LiteralPath (Join-Path $taskSite 'ctranslate2\__init__.py'))){throw 'Pinned Python packages are missing.'}
$taskOriginalDll=if($CpuTranslationLibrary){[IO.Path]::GetFullPath($CpuTranslationLibrary)}else{Join-Path $taskSite 'ctranslate2\ctranslate2.dll'}
if(-not(Test-Path -LiteralPath $taskOriginalDll -PathType Leaf) -or (Get-FileHash -LiteralPath $taskOriginalDll).Hash.ToLowerInvariant() -ne $CpuTranslationSha256.ToLowerInvariant()){throw 'Supply the verified CPU-only CTranslate2 DLL and its pinned SHA256; never stage the GPU wheel DLL.'}
$taskEmbeddedDll=Join-Path $taskPython 'python313.dll'
$taskExistingDll=Join-Path $taskRoot 'native\python313.dll'
if(-not(Test-Path -LiteralPath $taskExistingDll -PathType Leaf)){throw 'Run offline/stage-native.ps1 before staging the shared Python packages.'}
if((Get-FileHash -LiteralPath $taskEmbeddedDll).Hash -ne (Get-FileHash -LiteralPath $taskExistingDll).Hash){throw 'Embedded CPython version does not match the bundled runtime.'}
New-Item -ItemType Directory -Path $taskTarget | Out-Null
$taskDestination=Join-Path $taskTarget 'site'
New-Item -ItemType Directory -Path $taskDestination | Out-Null
# Copy only inference resources. Keep this predicate identical to the scoped,
# recoverable trim-runtime.ps1 so future staging cannot restore build/test files.
Get-ChildItem -LiteralPath $taskSite -Recurse -File -Force | ForEach-Object {
  $taskRelative=[IO.Path]::GetRelativePath($taskSite,$_.FullName).Replace('\','/')
  $taskPrunable=($taskRelative -match '^(bin|include|lib|llama_cpp/server)/') -or
    ($taskRelative -match '^(numpy|setuptools)/(.*/)?tests/') -or
    ($taskRelative -match '^llama_cpp/lib/.*\.lib$')
  if(-not $taskPrunable){
    $taskCopy=Join-Path $taskDestination $taskRelative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskCopy)) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $taskCopy
  }
}
Copy-Item -LiteralPath (Join-Path $taskPython 'python313.zip') -Destination (Join-Path $taskTarget 'python313.zip')
Copy-Item -LiteralPath (Join-Path $taskPython 'LICENSE.txt') -Destination (Join-Path $taskTarget 'PYTHON-LICENSE.txt')
# The public CTranslate2 wheel includes GPU/Intel libraries. Keep the verified
# CPU-only native build verified above; no legacy engine tree is required.
foreach($taskName in @('cudnn64_9.dll','libiomp5md.dll')){
  $taskFile=Join-Path $taskDestination ('ctranslate2\'+$taskName)
  if(Test-Path -LiteralPath $taskFile){Remove-Item -LiteralPath $taskFile -Force}
}
Copy-Item -LiteralPath $taskOriginalDll -Destination (Join-Path $taskDestination 'ctranslate2\ctranslate2.dll') -Force
if(@(Get-ChildItem -LiteralPath $taskTarget -Recurse -File | Where-Object {$_.Name -match '^(cudnn|libiomp5|cublas|mkl)'}).Count -ne 0){throw 'GPU/Intel runtime remains in the embedded package.'}
$taskFiles=[ordered]@{}
Get-ChildItem -LiteralPath $taskTarget -Recurse -File | Sort-Object FullName | ForEach-Object {
  $taskRelative=[IO.Path]::GetRelativePath($taskTarget,$_.FullName).Replace('\','/')
  $taskFiles[$taskRelative]=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
@{python_version='3.13.15';files=$taskFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskTarget 'manifest.json') -Encoding utf8
