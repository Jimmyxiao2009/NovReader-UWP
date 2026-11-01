# build-jsbridge.ps1 — 把 JsBridge.cpp（JSC 桥）编成 ARM32 UWP 的 JsBridge.dll。
# 复用 Apotheosis 的工具链环境与 build-clang-gpu 的 JSC/WTF/bmalloc 静态库 + ICU。
# 产物：H:\项目\UWP\NovReader\NovReader\JsBridge.dll（随后作为 Content 打进 appx）。
$ErrorActionPreference = 'Stop'
. 'E:\Apotheosis\port\arm32-uwp-env.ps1' *> $null

$here = $PSScriptRoot
$appDir = Join-Path (Split-Path -Parent $here) 'NovReader'
$gpu  = 'E:\Apotheosis\build-clang-gpu'

$clang = 'C:\Program Files\LLVM\bin\clang-cl.exe'
$lld   = 'C:\Program Files\LLVM\bin\lld-link.exe'

# 与 Apotheosis 的 harness-cmd.bat 相同的宏基线（去掉 WebCore 专属的）
$defines = @(
  '-DBUILDING_WEBKIT=1','-DBUILDING_WINUWP__','-DBUILDING_WITH_CMAKE=1',
  '-DHAVE_CONFIG_H=1','-DNOCRYPT','-DNOMINMAX','-DNTDDI_VERSION=0x0A000004',
  '-DPAS_BMALLOC=1','-DSTATICALLY_LINKED_WITH_JavaScriptCore','-DSTATICALLY_LINKED_WITH_WTF','-DSTATICALLY_LINKED_WITH_bmalloc',
  '-DUNICODE','-DWIN32_LEAN_AND_MEAN','-DWINAPI_FAMILY=WINAPI_FAMILY_APP','-DWK_WINUWP=1',
  '-D_CRT_NONSTDC_NO_DEPRECATE','-D_CRT_SECURE_NO_WARNINGS','-D_ENABLE_EXTENDED_ALIGNED_STORAGE',
  '-D_HAS_EXCEPTIONS=0','-D_UNICODE','-D_WIN32_WINNT=0x0A00','-D_WINDOWS','-DWK_WINUWP_JIT=1','-DNDEBUG',
  # 与 configure-gpu.ps1 一致：系统 malloc；C++23（WTF 用了 std::unexpected）
  '-DUSE_SYSTEM_MALLOC=1','-DHAVE_FAST_MALLOC_H=1'
) -join ' '

$includes = @(
  "$gpu",
  "$gpu\JavaScriptCore\Headers",
  "$gpu\JavaScriptCore\PrivateHeaders",
  "$gpu\WTF\Headers",
  "$gpu\bmalloc\Headers",
  "$gpu\PAL\Headers",
  'E:\Apotheosis\WebKit\Source\JavaScriptCore',
  'E:\Apotheosis\WebKit\Source\WTF',
  'C:\icu-arm-uwp\include'
) | ForEach-Object { "-I`"$_`"" }
$includes = $includes -join ' '

Set-Location $gpu

# Apotheosis 规则：bat/编译路径必须 ASCII。源和产物经 ASCII 暂存目录。
$stage = 'E:\Apotheosis\_jsbridge'
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$here\JsBridge.cpp" "$stage\JsBridge.cpp" -Force
Copy-Item "$here\JsBridgeStubs.cpp" "$stage\JsBridgeStubs.cpp" -Force
Copy-Item "$here\JsBridgeDiagnostics.h" "$stage\JsBridgeDiagnostics.h" -Force

# 1) 编译（clang-cl + Apotheosis 的 arm shim：/FI 补齐 clang 在 ARM32 缺失的 MSVC STL 内在函数）
$cc = "`"C:\Program Files\LLVM\bin\clang-cl.exe`" --target=thumbv7-unknown-windows-msvc /nologo -TP $defines $includes " +
      '/FI"E:\Apotheosis\port\clang-cl-arm-shim.h"' +
      " /std:c++20 /clang:-std=c++23 /EHs-c- /GR- /MD /O2 /Fo`"$stage\JsBridge.obj`" -c -- `"$stage\JsBridge.cpp`""
$cc | Out-File "$stage\_cc.bat" -Encoding ASCII
cmd /c "$stage\_cc.bat" *> "$here\cc.log"
if ($LASTEXITCODE -ne 0) { Write-Host "[cc] FAILED"; Get-Content "$here\cc.log" -Tail 30; exit 1 }
Write-Host "[cc] ok"

# 1b) stubs
$cc2 = $cc -replace [regex]::Escape("`"$stage\JsBridge.cpp`""), "`"$stage\JsBridgeStubs.cpp`"" -replace 'JsBridge.obj', 'JsBridgeStubs.obj'
$cc2 | Out-File "$stage\_cc2.bat" -Encoding ASCII
cmd /c "$stage\_cc2.bat" *> "$here\cc2.log"
if ($LASTEXITCODE -ne 0) { Write-Host "[cc2] FAILED"; Get-Content "$here\cc2.log" -Tail 30; exit 1 }
Write-Host "[cc2] ok"
# 2) 链接（JSC + WTF + bmalloc + ICU + UWP CRT/系统库）
$libs = @(
  'JavaScriptCore.lib','WTF.lib','bmalloc.lib','PAL.lib',
  'icuuc.lib','icuin.lib','icudt.lib',
  'WindowsApp.lib'
) -join ' '

$ld = "`"C:\Program Files\LLVM\bin\lld-link.exe`" /DLL /MACHINE:ARM /OUT:`"$stage\JsBridge.dll`" `"$stage\JsBridge.obj`" `"$stage\JsBridgeStubs.obj`" " +
      "/LIBPATH:`"$gpu\lib`" /LIBPATH:`"C:\icu-arm-uwp\lib`" $libs " +
      "/OPT:REF /OPT:NOICF /INCREMENTAL:NO /DEBUG /PDB:`"$stage\JsBridge.pdb`""
$ld | Out-File "$stage\_ld.bat" -Encoding ASCII
cmd /c "$stage\_ld.bat" *> "$here\ld.log"
if ($LASTEXITCODE -ne 0) {
  Write-Host "[ld] FAILED"
  @(Select-String -Path "$here\ld.log" -Pattern 'undefined symbol|duplicate symbol|error') |
    Select -First 40 | % { $_.Line }
  exit 1
}

# 3) 拷到 NovReader 工程（Content 打包）
Copy-Item "$stage\JsBridge.dll" (Join-Path $appDir 'JsBridge.dll') -Force
Copy-Item "$stage\JsBridge.pdb" (Join-Path $here 'JsBridge.pdb') -Force
# ICU 的 .lib 是导入库，不是静态库；必须携带 ICU 本体及其数据 DLL。
foreach ($dependency in 'icuuc78.dll','icuin78.dll','icudt78.dll') {
    Copy-Item -LiteralPath (Join-Path 'C:\icu-arm-uwp\bin' $dependency) -Destination (Join-Path $appDir $dependency) -Force -ErrorAction Stop
}
$size = '{0:N1} MB' -f ((Get-Item (Join-Path $appDir 'JsBridge.dll')).Length / 1MB)
Write-Host "[ld] ok  JsBridge.dll $size"
