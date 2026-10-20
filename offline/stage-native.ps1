param(
  [string[]]$NativeSourceDirectories,
  [switch]$ArchiveLegacyEngines,
  [string]$ArchiveDirectory
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$taskTarget=Join-Path $taskRoot 'native'
$taskManifestPath=Join-Path $taskTarget 'manifest.json'
$taskPinnedPythonHash='e820bf024efd2b56bb2b82791e6b6ddc7303f070f8e72cba7637482a8a906238'
function Assert-OrdinaryDirectory([string]$Path){
  if(-not [IO.Path]::IsPathRooted($Path)){throw "An absolute runtime directory is required: $Path"}
  $taskFull=[IO.Path]::GetFullPath($Path).TrimEnd('\')
  if(-not(Test-Path -LiteralPath $taskFull -PathType Container)){throw "Runtime directory is missing: $taskFull"}
  if((Get-Item -LiteralPath $taskFull).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Runtime directory is a reparse point: $taskFull"}
  if(@(Get-ChildItem -LiteralPath $taskFull -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -ne 0){throw "Runtime contains a reparse point: $taskFull"}
  return $taskFull
}
Assert-OrdinaryDirectory $taskRoot | Out-Null
$taskLegacy=@((Join-Path $taskRoot 'engine'),(Join-Path $taskRoot 'qwen\engine'))
if(-not $NativeSourceDirectories){
  $NativeSourceDirectories=@($taskLegacy | ForEach-Object {Join-Path $_ '_internal'} | Where-Object {Test-Path -LiteralPath $_ -PathType Container})
}
$taskSources=@($NativeSourceDirectories | ForEach-Object {Assert-OrdinaryDirectory $_})
if(Test-Path -LiteralPath $taskTarget){$taskSources=@((Assert-OrdinaryDirectory $taskTarget))+$taskSources}
if($taskSources.Count -eq 0){throw 'No pinned native runtime source is available.'}
# PyInstaller package trees are not used by embedded CPython. Keep just one
# conservative set of top-level native stdlib/CRT DLLs and Python extensions;
# inference packages and their DLLs already live in embedded/site.
$taskSelected=@{}
foreach($taskSource in $taskSources){
  foreach($taskFile in @(Get-ChildItem -LiteralPath $taskSource -File | Where-Object {$_.Extension -in '.dll','.pyd'})){
    $taskKey=$taskFile.Name.ToLowerInvariant()
    $taskHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $taskFile.FullName).Hash.ToLowerInvariant()
    if($taskSelected.ContainsKey($taskKey)){
      if($taskSelected[$taskKey].sha256 -ne $taskHash){throw "Conflicting native ABI bytes: $($taskFile.Name)"}
    }else{$taskSelected[$taskKey]=[pscustomobject]@{name=$taskFile.Name;source=$taskFile.FullName;bytes=$taskFile.Length;sha256=$taskHash}}
  }
}
if(-not $taskSelected.ContainsKey('python313.dll') -or $taskSelected['python313.dll'].sha256 -ne $taskPinnedPythonHash){throw 'Pinned CPython 3.13.15 x64 DLL integrity mismatch.'}
# Individual build-worker scripts may stage Argos before Qwen. SQLite is added
# by the Qwen staging pass; build.ps1 requires it before a complete app publish.
foreach($taskRequired in @('_ctypes.pyd','_hashlib.pyd','unicodedata.pyd','libcrypto-3.dll','libffi-8.dll','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')){
  if(-not $taskSelected.ContainsKey($taskRequired)){throw "Required shared native dependency is missing: $taskRequired"}
}
if(@($taskSelected.Keys | Where-Object {$_ -match '^(cudnn|libiomp5|cublas|mkl)'}).Count -ne 0){throw 'GPU/Intel inference resources cannot enter the shared CPU runtime.'}
# Validate the architecture without loading any native library or AI model.
foreach($taskEntry in $taskSelected.Values){
  $taskStream=[IO.File]::OpenRead($taskEntry.source)
  try{
    $taskReader=[IO.BinaryReader]::new($taskStream)
    if($taskReader.ReadUInt16() -ne 0x5a4d){throw "Invalid native executable header: $($taskEntry.name)"}
    $taskStream.Position=0x3c;$taskPeOffset=$taskReader.ReadInt32();$taskStream.Position=$taskPeOffset
    if($taskReader.ReadUInt32() -ne 0x4550 -or $taskReader.ReadUInt16() -ne 0x8664){throw "Native runtime is not PE x64: $($taskEntry.name)"}
  }finally{$taskStream.Dispose()}
}
$taskMoves=@()
if($ArchiveLegacyEngines){
  if(-not $ArchiveDirectory -or -not [IO.Path]::IsPathRooted($ArchiveDirectory)){throw 'Archiving requires an absolute dedicated ArchiveDirectory.'}
  $taskArchive=[IO.Path]::GetFullPath($ArchiveDirectory).TrimEnd('\')
  if($taskArchive -eq [IO.Path]::GetPathRoot($taskArchive).TrimEnd('\') -or $taskArchive -eq $taskRoot -or $taskArchive.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $taskRoot.StartsWith($taskArchive+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'ArchiveDirectory must be a dedicated directory outside the source offline tree.'}
  foreach($taskLegacyDirectory in $taskLegacy){
    if(-not(Test-Path -LiteralPath $taskLegacyDirectory -PathType Container)){continue}
    $taskChecked=Assert-OrdinaryDirectory $taskLegacyDirectory
    if(-not $taskChecked.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $taskChecked -notin $taskLegacy){throw 'Legacy runtime target escaped its explicit offline directory.'}
    $taskRelative=[IO.Path]::GetRelativePath($taskRoot,$taskChecked)
    $taskDestination=Join-Path $taskArchive $taskRelative
    if(Test-Path -LiteralPath $taskDestination){throw "Archived runtime target already exists: $taskDestination"}
    $taskMoveFiles=@(Get-ChildItem -LiteralPath $taskChecked -Recurse -File | Sort-Object FullName | ForEach-Object {[pscustomobject]@{relative=[IO.Path]::GetRelativePath($taskChecked,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
    $taskMoves+=[pscustomobject]@{source=$taskChecked;backup=$taskDestination;files=$taskMoveFiles}
  }
  if($taskMoves.Count -gt 0 -and (Test-Path -LiteralPath (Join-Path $taskArchive 'restore-map.json'))){throw 'The archive already has a restore map; choose a fresh dedicated archive.'}
}
New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
foreach($taskEntry in $taskSelected.Values){
  $taskDestination=Join-Path $taskTarget $taskEntry.name
  if(-not(Test-Path -LiteralPath $taskDestination)){Copy-Item -LiteralPath $taskEntry.source -Destination $taskDestination}
  if((Get-FileHash -Algorithm SHA256 -LiteralPath $taskDestination).Hash.ToLowerInvariant() -ne $taskEntry.sha256){throw "Staged native hash mismatch: $($taskEntry.name)"}
}
$taskFiles=[ordered]@{}
Get-ChildItem -LiteralPath $taskTarget -File | Where-Object {$_.FullName -ne $taskManifestPath} | Sort-Object Name | ForEach-Object {
  if(-not $taskSelected.ContainsKey($_.Name.ToLowerInvariant())){throw "Unexpected shared runtime resource: $($_.Name)"}
  $taskFiles[$_.Name]=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
$taskManifest=[ordered]@{schema=1;python_version='3.13.15';architecture='x64';python_dll_sha256=$taskPinnedPythonHash;files=$taskFiles}
$taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
# Workers are imported directly from these source files, not from the old
# frozen helpers. Model integrity belongs only to each model's own manifest.
$taskWorkers=[ordered]@{}
foreach($taskWorker in @('worker.py','qwen/worker.py')){
  $taskWorkerPath=Join-Path $taskRoot $taskWorker
  if(-not(Test-Path -LiteralPath $taskWorkerPath -PathType Leaf)){throw "Translation worker is missing: $taskWorker"}
  $taskWorkers[$taskWorker]=(Get-FileHash -Algorithm SHA256 -LiteralPath $taskWorkerPath).Hash.ToLowerInvariant()
}
[ordered]@{schema=1;python_version='3.13.15';files=$taskWorkers} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskRoot 'workers-manifest.json') -Encoding utf8
if($taskMoves.Count -gt 0){
  New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
  $taskMapPath=Join-Path $taskArchive 'restore-map.json'
  $taskMap=[ordered]@{schema=1;status='planned';root=$taskRoot;archive=$taskArchive;moves=$taskMoves}
  $taskMap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskMapPath -Encoding utf8
  foreach($taskMove in $taskMoves){
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskMove.backup)) -Force | Out-Null
    Move-Item -LiteralPath $taskMove.source -Destination $taskMove.backup
    foreach($taskFile in $taskMove.files){if((Get-FileHash -LiteralPath (Join-Path $taskMove.backup $taskFile.relative)).Hash.ToLowerInvariant() -ne $taskFile.sha256){throw "Archived runtime integrity mismatch: $($taskFile.relative)"}}
  }
  $taskMap.status='complete';$taskMap | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskMapPath -Encoding utf8
}
[ordered]@{native_directory=$taskTarget;files=$taskFiles.Count;bytes=[long](($taskSelected.Values | Measure-Object bytes -Sum).Sum);python_dll_sha256=$taskPinnedPythonHash;archived_engines=$taskMoves.Count;restore_map=if($taskMoves.Count -gt 0){$taskMapPath}else{$null}} | ConvertTo-Json -Depth 4
