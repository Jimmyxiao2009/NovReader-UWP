$ErrorActionPreference = 'Stop'
. 'E:\Apotheosis\port\arm32-uwp-env.ps1' *> $null
$stage = 'E:\Apotheosis\_jsbridge'
$appDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'NovReader'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NetBridge.cpp') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DnsWire.h') -Destination $stage -Force
& 'C:\Program Files\LLVM\bin\clang-cl.exe' --target=thumbv7-unknown-windows-msvc /nologo /TP /O2 /MD /EHs-c- /DNDEBUG /D_HAS_EXCEPTIONS=0 /DWINAPI_FAMILY=WINAPI_FAMILY_APP /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I'C:\vcpkg\installed\arm-uwp\include' /Fo"$stage\NetBridge.obj" /c "$stage\NetBridge.cpp"
if ($LASTEXITCODE -ne 0) { throw 'NetBridge compile failed' }
& 'C:\Program Files\LLVM\bin\lld-link.exe' /DLL /MACHINE:ARM /OUT:"$stage\NetBridge.dll" "$stage\NetBridge.obj" /LIBPATH:'C:\vcpkg\installed\arm-uwp\lib' libcurl.lib WindowsApp.lib /OPT:REF /OPT:NOICF /DEBUG /PDB:"$stage\NetBridge.pdb"
if ($LASTEXITCODE -ne 0) { throw 'NetBridge link failed' }
Copy-Item -LiteralPath "$stage\NetBridge.dll" -Destination $appDir -Force
Copy-Item -LiteralPath "$stage\NetBridge.pdb" -Destination $PSScriptRoot -Force
foreach ($name in 'libcurl.dll','libssl-3-arm.dll','libcrypto-3-arm.dll','z.dll') {
    Copy-Item -LiteralPath (Join-Path 'C:\vcpkg\installed\arm-uwp\bin' $name) -Destination $appDir -Force
}
Copy-Item -LiteralPath 'E:\Apotheosis\harness\Assets\cacert.pem' -Destination (Join-Path $appDir 'Assets\cacert.pem') -Force
Write-Output 'NetBridge built with Apotheosis curl/OpenSSL and CA bundle'
