# Build, verify and sign an ARM sideload update with all framework dependencies.
param([string]$CertificateThumbprint = '183E74E21F0C7977BC9ACE4E8AFA2503D2637515')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $repo 'NovReader'
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.15063.0\x64\signtool.exe'

# A child process keeps the ARM C++ toolchain environment out of managed MSBuild.
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-jsbridge.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed' }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-netbridge.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Native image transport build failed' }
& $msbuild (Join-Path $appDir 'NovReader.csproj') /p:Configuration=Release /p:Platform=ARM /p:GenerateAppxPackageOnBuild=true /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false /p:AppxGeneratePrisForPortableLibrariesEnabled=false /t:Build /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'ARM package build failed' }

[xml]$manifest = Get-Content -LiteralPath (Join-Path $appDir 'Package.appxmanifest') -Raw
$version = $manifest.Package.Identity.Version
$packageDir = Join-Path $appDir "AppPackages\NovReader_${version}_ARM_Test"
$package = Join-Path $packageDir "NovReader_${version}_ARM.appx"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Dependencies\arm\Microsoft.VCLibs.ARM.14.00.appx') -Destination (Join-Path $packageDir 'Dependencies\arm\Microsoft.VCLibs.ARM.14.00.appx') -Force
Copy-Item -LiteralPath (Join-Path $appDir 'NovReader.cer') -Destination $packageDir -Force
& python (Join-Path $repo '_devtest\verify_arm_package.py') $package
if ($LASTEXITCODE -ne 0) { throw 'Native dependency verification failed' }
& $signtool sign /fd SHA256 /sha1 $CertificateThumbprint /s My $package
if ($LASTEXITCODE -ne 0) { throw 'Signing failed' }
Get-FileHash -LiteralPath $package -Algorithm SHA256 | Format-List
Write-Output "Install the main package with the three packages in $packageDir\Dependencies\arm"
