param(
  [string]$Dotnet = 'dotnet',
  [string]$Iscc = 'iscc',
  [string]$RedistPath, # Retained for older build commands; application-local CRT files are now bundled.
  [string]$BuildDirectory,
  [string]$OutputDirectory,
  [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskDefaultRoot = if (Test-Path -LiteralPath 'D:\' -PathType Container) { 'D:\QingCapture' } else { $taskRoot }
$taskBuild = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { Join-Path $taskDefaultRoot 'build' }
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskDefaultRoot 'dist' }
$taskPublish = Join-Path $taskBuild 'publish'
New-Item -ItemType Directory -Force $taskPublish | Out-Null
$taskOffline=Join-Path $taskRoot 'offline'
$taskQwen=Join-Path $taskOffline 'qwen'
$taskNative=Join-Path $taskOffline 'native'
if(-not(Test-Path -LiteralPath (Join-Path $taskNative 'manifest.json'))){throw 'Stage the shared offline/native runtime first with offline/stage-native.ps1.'}
$taskNativeManifest=Get-Content -LiteralPath (Join-Path $taskNative 'manifest.json') -Raw | ConvertFrom-Json
$taskWorkersManifest=Get-Content -LiteralPath (Join-Path $taskOffline 'workers-manifest.json') -Raw | ConvertFrom-Json
$taskEmbedded=Join-Path $taskOffline 'embedded'
$taskEmbeddedManifest=Get-Content -LiteralPath (Join-Path $taskEmbedded 'manifest.json') -Raw | ConvertFrom-Json
if($taskEmbeddedManifest.python_version -ne '3.13.15'){throw 'Unexpected embedded Python version'}
function Get-CheckedManifestFile([string]$Directory,[string]$Relative){
  $taskDirectoryFull=[IO.Path]::GetFullPath($Directory).TrimEnd('\')
  if([IO.Path]::IsPathRooted($Relative)){throw "Absolute runtime manifest path is forbidden: $Relative"}
  $taskFull=[IO.Path]::GetFullPath((Join-Path $taskDirectoryFull $Relative))
  if(-not $taskFull.StartsWith($taskDirectoryFull+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Runtime manifest path escapes its package: $Relative"}
  if(-not(Test-Path -LiteralPath $taskFull -PathType Leaf)){throw "Runtime manifest resource is missing: $Relative"}
  return $taskFull
}
function Assert-ManifestFiles([string]$Directory,$Files,[string]$Label){
  if((Get-Item -LiteralPath $Directory).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "$Label root is a reparse point"}
  if(@(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -ne 0){throw "$Label contains a reparse point"}
  $taskEntries=@($Files.PSObject.Properties)
  $taskActual=@(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $Directory 'manifest.json')})
  if($taskActual.Count -ne $taskEntries.Count){throw "$Label manifest does not cover every packaged file"}
  foreach($taskEntry in $taskEntries){
    $taskFile=Get-CheckedManifestFile $Directory $taskEntry.Name
    if((Get-FileHash -Algorithm SHA256 -LiteralPath $taskFile).Hash.ToLowerInvariant() -ne $taskEntry.Value.ToLowerInvariant()){throw "$Label hash mismatch: $($taskEntry.Name)"}
  }
}
if($taskNativeManifest.schema -ne 1 -or $taskNativeManifest.python_version -ne '3.13.15' -or $taskNativeManifest.architecture -ne 'x64' -or $taskNativeManifest.python_dll_sha256 -ne 'e820bf024efd2b56bb2b82791e6b6ddc7303f070f8e72cba7637482a8a906238'){throw 'Unexpected shared CPython ABI or native manifest'}
if($taskNativeManifest.files.PSObject.Properties['python313.dll'].Value -ne $taskNativeManifest.python_dll_sha256){throw 'Shared CPython file hash does not match its pinned ABI record'}
foreach($taskRequired in @('python313.dll','_ctypes.pyd','_hashlib.pyd','_sqlite3.pyd','sqlite3.dll','unicodedata.pyd','libcrypto-3.dll','libffi-8.dll','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')){
  if($null -eq $taskNativeManifest.files.PSObject.Properties[$taskRequired] -or -not(Test-Path -LiteralPath (Join-Path $taskNative $taskRequired) -PathType Leaf)){throw "Shared native dependency is missing: $taskRequired"}
}
Assert-ManifestFiles $taskNative $taskNativeManifest.files 'Shared native runtime'
if($taskWorkersManifest.schema -ne 1 -or $taskWorkersManifest.python_version -ne '3.13.15' -or @($taskWorkersManifest.files.PSObject.Properties).Count -ne 2){throw 'Unexpected translation worker manifest'}
foreach($taskWorker in @('worker.py','qwen/worker.py')){
  $taskEntry=$taskWorkersManifest.files.PSObject.Properties[$taskWorker]
  if($null -eq $taskEntry -or (Get-FileHash -LiteralPath (Get-CheckedManifestFile $taskOffline $taskWorker)).Hash.ToLowerInvariant() -ne $taskEntry.Value){throw "Translation worker source hash mismatch: $taskWorker"}
}
# Model manifests remain authoritative and separate from worker/native records.
# Never reinterpret historical frozen-engine manifests as the current package.
$taskArgosModel=Join-Path $taskOffline 'model'
$taskArgosModelManifest=Get-Content -LiteralPath (Join-Path $taskArgosModel 'manifest.json') -Raw | ConvertFrom-Json
if($taskArgosModelManifest.from -ne 'en' -or $taskArgosModelManifest.to -ne 'zh' -or $taskArgosModelManifest.package -ne '1.9'){throw 'Unexpected Argos model metadata'}
Assert-ManifestFiles $taskArgosModel $taskArgosModelManifest.files 'Argos model'
$taskQwenModel=Join-Path $taskQwen 'model'
$taskQwenModelManifest=Get-Content -LiteralPath (Join-Path $taskQwenModel 'manifest.json') -Raw | ConvertFrom-Json
if($taskQwenModelManifest.format -ne 'gguf' -or $taskQwenModelManifest.from -ne 'en' -or $taskQwenModelManifest.to -ne 'zh-CN' -or $taskQwenModelManifest.license -ne 'apache-2.0' -or [IO.Path]::GetFileName($taskQwenModelManifest.file) -ne $taskQwenModelManifest.file){throw 'Unexpected Qwen model metadata'}
$taskQwenModelFiles=[pscustomobject]@{$taskQwenModelManifest.file=$taskQwenModelManifest.sha256}
Assert-ManifestFiles $taskQwenModel $taskQwenModelFiles 'Qwen model'
# File counts are not a completeness signal after excluding package tests and
# compiler artifacts. Require the actual CPU inference entry points instead,
# while still checking every declared file's full SHA256 below.
$taskEmbeddedRequired=@(
  'PYTHON-LICENSE.txt','python313.zip',
  'site/llama_cpp/llama.py','site/llama_cpp/lib/llama.dll',
  'site/llama_cpp/lib/ggml.dll','site/llama_cpp/lib/ggml-base.dll','site/llama_cpp/lib/ggml-cpu.dll',
  'site/numpy/__init__.py','site/numpy/_core/_multiarray_umath.cp313-win_amd64.pyd',
  'site/ctranslate2/__init__.py','site/ctranslate2/_ext.cp313-win_amd64.pyd','site/ctranslate2/ctranslate2.dll',
  'site/sentencepiece/__init__.py','site/sentencepiece/_sentencepiece.cp313-win_amd64.pyd'
)
foreach($taskRequired in $taskEmbeddedRequired){
  if($null -eq $taskEmbeddedManifest.files.PSObject.Properties[$taskRequired] -or -not(Test-Path -LiteralPath (Join-Path $taskEmbedded $taskRequired) -PathType Leaf)){throw "Embedded runtime requirement is missing: $taskRequired"}
}
Assert-ManifestFiles $taskEmbedded $taskEmbeddedManifest.files 'Embedded runtime'
if(@(Get-ChildItem -LiteralPath $taskEmbedded,$taskNative -Recurse -File | Where-Object {$_.Name -match '^(cudnn|libiomp5|cublas|mkl)'}).Count -gt 0){throw 'Shared CPU distribution contains unexpected GPU/Intel inference libraries'}
foreach($taskLegacyRelative in @('engine','qwen\engine','runtime-manifest.json','qwen\runtime-manifest.json')){
  if(Test-Path -LiteralPath (Join-Path $taskPublish ('offline\'+$taskLegacyRelative))){throw "Publish has an obsolete frozen-engine resource. Archive it outside the fixed publish directory before building: $taskLegacyRelative"}
}
$taskRuntime = Join-Path $taskRoot 'runtimes\win-x64'
$taskRuntimeManifest = Get-Content -LiteralPath (Join-Path $taskRuntime 'vc-runtime-manifest.json') -Raw | ConvertFrom-Json
$taskExpectedRuntime = @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')
if ($taskRuntimeManifest.architecture -ne 'x64' -or @($taskRuntimeManifest.files).Count -ne 3) { throw 'Unexpected native runtime manifest' }
foreach ($taskRuntimeName in $taskExpectedRuntime) {
  $taskEntry = @($taskRuntimeManifest.files | Where-Object { $_.name -eq $taskRuntimeName })
  if ($taskEntry.Count -ne 1) { throw "Missing or duplicate runtime entry: $taskRuntimeName" }
  $taskRuntimeFile = Join-Path $taskRuntime $taskRuntimeName
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath $taskRuntimeFile).Hash -ne $taskEntry[0].sha256) { throw "Runtime hash verification failed: $taskRuntimeName" }
  $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskRuntimeFile
  if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "Microsoft runtime signature verification failed: $taskRuntimeName" }
}
if ($RedistPath) { Write-Host 'RedistPath is no longer required; using verified application-local Microsoft runtime DLLs.' }
& $Dotnet restore "$taskRoot\src\QingCapture.csproj" -r win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& $Dotnet publish "$taskRoot\src\QingCapture.csproj" -c Release -r win-x64 --self-contained true --no-restore -p:OfflineCopyToOutput=Never -o $taskPublish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
foreach ($taskEntry in $taskRuntimeManifest.files) {
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $taskPublish $taskEntry.name)).Hash -ne $taskEntry.sha256) { throw "Published runtime hash verification failed: $($taskEntry.name)" }
}
$taskTest = Start-Process -FilePath "$taskPublish\QingCapture.exe" -ArgumentList @('--self-test','--browser-translation-test','--report-dir',('"'+$taskBuild+'\test-results"')) -WindowStyle Hidden -Wait -PassThru
if ($taskTest.ExitCode -ne 0) { throw 'Tests failed; inspect build/test-results/tests.txt' }
$taskAppChecks=@(Select-String -LiteralPath (Join-Path $taskBuild 'test-results\tests.txt') -Pattern '^PASS:').Count
if($taskAppChecks -lt 196){throw "Missing application regression checks: $taskAppChecks (expected at least 196)"}
$taskAuditPath=Join-Path $taskBuild 'test-results\model-quality-audit-1.3.0.json'
$taskAudit = Start-Process -FilePath "$taskPublish\QingCapture.exe" -ArgumentList @('--quality-audit','--quality-input',('"'+$taskPublish+'\offline\testdata\model-quality-audit.json"'),'--quality-report',('"'+$taskAuditPath+'"')) -WindowStyle Hidden -Wait -PassThru
if ($taskAudit.ExitCode -ne 0) { throw 'Post-fix model semantic audit failed' }
$taskAuditData=Get-Content -LiteralPath $taskAuditPath -Raw -Encoding utf8|ConvertFrom-Json
$taskAuditInput=Get-Content -LiteralPath (Join-Path $taskPublish 'offline\testdata\model-quality-audit.json') -Raw -Encoding utf8|ConvertFrom-Json
if($taskAuditData.cases -ne 32 -or $taskAuditData.cases -ne $taskAuditInput.results.Count -or $taskAuditData.network_calls -ne 0){throw 'Post-fix semantic corpus count or offline privacy check failed'}
$taskRuleCases=@($taskAuditData.results|Where-Object {$null -ne $_.expected_output}).Count
if($taskRuleCases -ne 17){throw "Unexpected exact-rule semantic case count: $taskRuleCases"}
$taskRuleMisses=@($taskAuditData.results|Where-Object {$null -ne $_.expected_output -and $_.after_1_3_0 -cne $_.expected_output})
if($taskRuleMisses.Count -gt 0){throw ('Post-fix high-risk phrase mismatch: '+(($taskRuleMisses|ForEach-Object {$_.id}) -join ', '))}
$taskTrade=$taskAuditData.results|Where-Object id -eq 27
if($taskAuditData.version -ne '1.3.0'){throw 'Semantic audit version is stale'}
if($taskTrade.after_1_3_0 -notmatch '公司' -or $taskTrade.after_1_3_0 -notmatch '日本' -or $taskTrade.after_1_3_0 -notmatch '出口'){throw 'Trade-context fallback did not preserve the sentence meaning'}
Copy-Item "$taskRoot\THIRD-PARTY-NOTICES.md" $taskPublish
Copy-Item "$taskRoot\README.md" $taskPublish
Copy-Item "$taskRoot\licenses" $taskPublish -Recurse -Force
Assert-ManifestFiles (Join-Path $taskPublish 'offline\native') $taskNativeManifest.files 'Published shared native runtime'
Assert-ManifestFiles (Join-Path $taskPublish 'offline\embedded') $taskEmbeddedManifest.files 'Published embedded runtime'
Assert-ManifestFiles (Join-Path $taskPublish 'offline\model') $taskArgosModelManifest.files 'Published Argos model'
Assert-ManifestFiles (Join-Path $taskPublish 'offline\qwen\model') $taskQwenModelFiles 'Published Qwen model'
foreach($taskEntry in $taskWorkersManifest.files.PSObject.Properties){
  if((Get-FileHash -LiteralPath (Get-CheckedManifestFile (Join-Path $taskPublish 'offline') $taskEntry.Name)).Hash.ToLowerInvariant() -ne $taskEntry.Value){throw "Published translation worker mismatch: $($taskEntry.Name)"}
}
foreach($taskManifestRelative in @('native\manifest.json','embedded\manifest.json','workers-manifest.json','model\manifest.json','qwen\model\manifest.json')){
  if((Get-FileHash -LiteralPath (Join-Path $taskPublish ('offline\'+$taskManifestRelative))).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskOffline $taskManifestRelative)).Hash){throw "Published manifest mismatch: $taskManifestRelative"}
}
foreach($taskLegacyRelative in @('engine','qwen\engine','runtime-manifest.json','qwen\runtime-manifest.json')){
  if(Test-Path -LiteralPath (Join-Path $taskPublish ('offline\'+$taskLegacyRelative))){throw "Published application still contains an obsolete frozen-engine resource: $taskLegacyRelative"}
}
if(@(Get-ChildItem -LiteralPath (Join-Path $taskPublish 'offline') -Recurse -File | Where-Object {$_.Name -match '^(cudnn|libiomp5|cublas|mkl)'}).Count -gt 0){throw 'Publish directory contains stale GPU/Intel runtime'}
if (-not $SkipInstaller) {
  & $Iscc ('/DPublishDir='+$taskPublish) ('/DOutputDir='+$taskOutput) "$taskRoot\installer\QingCapture.iss"
  if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
}
