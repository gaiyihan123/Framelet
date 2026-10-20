param(
  [switch]$Apply,
  [string]$ArchiveDirectory
)
$ErrorActionPreference='Stop'
# Intentionally scoped to this checkout's embedded runtime, never a supplied
# workspace/drive root. By default this script only reports the exact targets.
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'embedded'))
$taskRootPrefix=$taskRoot.TrimEnd('\')+'\'
if(-not(Test-Path -LiteralPath $taskRoot -PathType Container)){throw 'Embedded runtime is missing.'}
if((Get-Item -LiteralPath $taskRoot).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Embedded runtime must not be a reparse point.'}
if(@(Get-ChildItem -LiteralPath $taskRoot -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -ne 0){throw 'Embedded runtime contains a reparse point; no files were moved.'}
function Get-CheckedRelativePath([string]$Path){
  $taskFull=[IO.Path]::GetFullPath($Path)
  if(-not $taskFull.StartsWith($taskRootPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Target escapes the embedded runtime: $taskFull"}
  return [IO.Path]::GetRelativePath($taskRoot,$taskFull).Replace('\','/')
}
function Test-TranslationBuildArtifact([string]$Relative){
  # Keep this predicate identical to stage-embedded.ps1. No inference DLL,
  # package initializer, tokenizer, model, or dependency is selected here.
  return ($Relative -match '^site/(bin|include|lib|llama_cpp/server)/') -or
    ($Relative -match '^site/(numpy|setuptools)/(.*/)?tests/') -or
    ($Relative -match '^site/llama_cpp/lib/.*\.lib$')
}
$taskManifestPath=Join-Path $taskRoot 'manifest.json'
$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
if($taskManifest.python_version -ne '3.13.15'){throw 'Unexpected embedded Python version.'}
$taskFiles=@(Get-ChildItem -LiteralPath $taskRoot -Recurse -File | Where-Object {$_.FullName -ne $taskManifestPath} | Sort-Object FullName)
$taskEntries=@($taskManifest.files.PSObject.Properties)
if($taskFiles.Count -ne $taskEntries.Count){throw 'Embedded manifest does not cover every file; no files were moved.'}
foreach($taskFile in $taskFiles){
  $taskRelative=Get-CheckedRelativePath $taskFile.FullName
  $taskEntry=$taskManifest.files.PSObject.Properties[$taskRelative]
  if($null -eq $taskEntry -or (Get-FileHash -Algorithm SHA256 -LiteralPath $taskFile.FullName).Hash.ToLowerInvariant() -ne $taskEntry.Value){throw "Embedded runtime integrity mismatch: $taskRelative"}
}
$taskSelected=@($taskFiles | Where-Object {Test-TranslationBuildArtifact (Get-CheckedRelativePath $_.FullName)})
$taskTargets=@($taskSelected | ForEach-Object {
  $taskRelative=Get-CheckedRelativePath $_.FullName
  if($taskRelative -match '^site/(bin|include|lib|llama_cpp/server)/'){Join-Path $taskRoot $Matches[0].TrimEnd('/')}
  elseif($taskRelative -match '^site/(numpy|setuptools)/(.*/)?tests/'){Join-Path $taskRoot $Matches[0].TrimEnd('/')}
  else {$_.FullName}
} | Sort-Object -Unique)
foreach($taskTarget in $taskTargets){Write-Host "TARGET $taskTarget"}
$taskBytes=[long](($taskSelected | Measure-Object Length -Sum).Sum)
$taskSummary=[ordered]@{root=$taskRoot;applied=$false;selected_files=$taskSelected.Count;selected_bytes=$taskBytes;absolute_targets=$taskTargets;manifest_entries_before=$taskEntries.Count;manifest_entries_after=$taskEntries.Count}
if(-not $Apply -or $taskSelected.Count -eq 0){$taskSummary | ConvertTo-Json -Depth 5;return}
if(-not $ArchiveDirectory -or -not [IO.Path]::IsPathRooted($ArchiveDirectory)){throw '-Apply requires an absolute, dedicated ArchiveDirectory.'}
$taskArchive=[IO.Path]::GetFullPath($ArchiveDirectory).TrimEnd('\')
$taskArchivePrefix=$taskArchive+'\'
if($taskArchive -eq [IO.Path]::GetPathRoot($taskArchive).TrimEnd('\') -or $taskArchive.StartsWith($taskRootPrefix,[StringComparison]::OrdinalIgnoreCase) -or $taskRoot.StartsWith($taskArchivePrefix,[StringComparison]::OrdinalIgnoreCase) -or $taskArchive -eq $taskRoot){throw 'ArchiveDirectory must be a dedicated directory outside the embedded runtime.'}
$taskMapPath=Join-Path $taskArchive 'restore-map.json'
$taskBeforePath=Join-Path $taskArchive 'manifest.before.json'
if((Test-Path -LiteralPath $taskMapPath) -or (Test-Path -LiteralPath $taskBeforePath)){throw 'ArchiveDirectory already contains a previous trim record; use a fresh dedicated directory.'}
$taskMoves=@($taskSelected | ForEach-Object {
  $taskRelative=Get-CheckedRelativePath $_.FullName
  $taskDestination=[IO.Path]::GetFullPath((Join-Path $taskArchive $taskRelative))
  if(-not $taskDestination.StartsWith($taskArchivePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Backup target escaped its archive directory.'}
  if(Test-Path -LiteralPath $taskDestination){throw "Backup target already exists: $taskDestination"}
  [pscustomobject]@{relative=$taskRelative;source=$_.FullName;backup=$taskDestination;bytes=$_.Length;sha256=$taskManifest.files.PSObject.Properties[$taskRelative].Value}
})
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
Copy-Item -LiteralPath $taskManifestPath -Destination $taskBeforePath
# The durable map is written before the first cross-drive move so a partial
# operation is recoverable without any model/runtime-tree duplication.
$taskMap=[ordered]@{schema=1;status='planned';root=$taskRoot;archive=$taskArchive;manifest_before=$taskBeforePath;manifest_target=$taskManifestPath;files=$taskMoves}
$taskMap | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskMapPath -Encoding utf8
foreach($taskMove in $taskMoves){
  New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskMove.backup)) -Force | Out-Null
  Move-Item -LiteralPath $taskMove.source -Destination $taskMove.backup
  if((Get-FileHash -Algorithm SHA256 -LiteralPath $taskMove.backup).Hash.ToLowerInvariant() -ne $taskMove.sha256){throw "Archived file integrity mismatch: $($taskMove.relative)"}
}
$taskRemaining=[ordered]@{}
Get-ChildItem -LiteralPath $taskRoot -Recurse -File | Where-Object {$_.FullName -ne $taskManifestPath} | Sort-Object FullName | ForEach-Object {
  $taskRelative=Get-CheckedRelativePath $_.FullName
  $taskRemaining[$taskRelative]=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
$taskNextManifest=[ordered]@{python_version=$taskManifest.python_version;files=$taskRemaining}
$taskNextManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
$taskMap.status='complete'
$taskMap | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskMapPath -Encoding utf8
$taskSummary.applied=$true
$taskSummary.manifest_entries_after=$taskRemaining.Count
$taskSummary.restore_map=$taskMapPath
$taskSummary | ConvertTo-Json -Depth 5
