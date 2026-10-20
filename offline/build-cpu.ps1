param([Parameter(Mandatory=$true)][string]$WorkDirectory,[Parameter(Mandatory=$true)][string]$CMake,[string]$Generator='Visual Studio 18 2026')
$ErrorActionPreference='Stop'
$taskWork=[IO.Path]::GetFullPath($WorkDirectory)
$taskSource=Join-Path $taskWork 'ct2-cpu'
$taskBuild=Join-Path $taskWork 'ct2-cpu-build'
$taskInstall=Join-Path $taskWork 'ct2-cpu-install'
if(-not(Test-Path -LiteralPath $taskSource)){
  git clone --branch v4.6.0 --depth 1 https://github.com/OpenNMT/CTranslate2.git $taskSource
  if($LASTEXITCODE -ne 0){throw 'CTranslate2 source download failed'}
}
if((& git -C $taskSource rev-parse HEAD) -ne '617405f4b050e994e829d527da6caa0e0030afe7'){throw 'Unexpected upstream commit'}
git -C $taskSource submodule update --init --depth 1 third_party/spdlog third_party/cpu_features third_party/ruy
if($LASTEXITCODE -ne 0){throw 'Submodules failed'}
git -C (Join-Path $taskSource 'third_party\ruy') submodule update --init --depth 1 third_party/cpuinfo
if($LASTEXITCODE -ne 0){throw 'CPUinfo source failed'}
& $CMake -S $taskSource -B $taskBuild -G $Generator -A x64 '-DWITH_MKL=OFF' '-DWITH_DNNL=OFF' '-DWITH_CUDA=OFF' '-DWITH_CUDNN=OFF' '-DWITH_RUY=ON' '-DOPENMP_RUNTIME=NONE' '-DBUILD_CLI=OFF' '-DBUILD_TESTS=OFF' '-DCMAKE_POLICY_VERSION_MINIMUM=3.5' ('-DCMAKE_INSTALL_PREFIX='+$taskInstall)
if($LASTEXITCODE -ne 0){throw 'CPU configuration failed'}
& $CMake --build $taskBuild --config Release --target install --parallel 6
if($LASTEXITCODE -ne 0){throw 'CPU build failed'}
# Select only the CPU DLL in the local venv used to build the frozen helper.
# Preserve the wheel's original DLLs in this build directory, never globally.
$taskPackage=Join-Path $taskWork 'venv\Lib\site-packages\ctranslate2'
$taskBackup=Join-Path $taskWork 'original-wheel-dlls'
New-Item -ItemType Directory -Force $taskBackup | Out-Null
foreach($taskName in @('ctranslate2.dll','cudnn64_9.dll','libiomp5md.dll')){
  $taskFile=Join-Path $taskPackage $taskName
  if(Test-Path -LiteralPath $taskFile){
    if(-not(Test-Path -LiteralPath (Join-Path $taskBackup $taskName))){Move-Item -LiteralPath $taskFile -Destination (Join-Path $taskBackup $taskName)}
    elseif($taskName -ne 'ctranslate2.dll'){throw 'Unexpected GPU library remained in CPU build environment; use a fresh work directory'}
  }
}
Copy-Item -LiteralPath (Join-Path $taskInstall 'bin\ctranslate2.dll') -Destination (Join-Path $taskPackage 'ctranslate2.dll') -Force
& (Join-Path $taskWork 'venv\Scripts\python.exe') -c 'import ctranslate2; assert "int8" in ctranslate2.get_supported_compute_types("cpu"); print("CPU runtime ready")'
if($LASTEXITCODE -ne 0){throw 'CPU Python binding failed'}
