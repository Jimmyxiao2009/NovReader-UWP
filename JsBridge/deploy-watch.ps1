# deploy-watch.ps1 — 等手机可达后自动：备份 LocalState → 安装 1.0.3.0 → 启动 → 验证。
# 用法: pwsh -File deploy-watch.ps1 [-WaitMinutes 30]
param([int]$WaitMinutes = 30)

$ErrorActionPreference = 'Continue'
$ip = '192.168.3.159'
$pkgDir = 'H:\项目\UWP\NovReader\NovReader\AppPackages\NovReader_1.0.3.0_ARM_Test'
$dir = 'H:\项目\UWP\NovReader\_devtest\phone-backup\v103'
$log = 'H:\项目\UWP\NovReader\JsBridge\deploy-watch.log'

function Log($m) { "$(Get-Date -Format 'HH:mm:ss')  $m" | Tee-Object -FilePath $log -Append }

# 1) 等可达
$deadline = (Get-Date).AddMinutes($WaitMinutes)
while ((Get-Date) -lt $deadline)
{
    $r = curl.exe -s --noproxy '*' -m 4 -o NUL -w "%{http_code}" "http://$ip/api/os/info"
    if ($r -eq '200') { break }
    Start-Sleep 10
}
if ($r -ne '200') { Log "手机 $WaitMinutes 分钟内不可达，放弃"; exit 1 }
Log "手机可达，开始备份"

# 2) 备份当前数据（1.0.2.0）
$pf102 = 'NovReader_1.0.2.0_arm__7xxdx2kd68cm6'
New-Item -ItemType Directory -Force $dir | Out-Null
$items = (curl.exe -s --noproxy '*' -m 30 "http://$ip/api/filesystem/apps/files?knownfolderid=LocalAppData&packagefullname=$pf102&path=%5CLocalState" | ConvertFrom-Json).Items | Where-Object { $_.Type -eq 32 }
foreach ($it in $items)
{
    curl.exe -s --noproxy '*' -m 240 -o "$dir\$($it.Name)" "http://$ip/api/filesystem/apps/file?knownfolderid=LocalAppData&filename=$($it.Name)&packagefullname=$pf102&path=%5CLocalState"
    Log ("备份 {0} ({1} 字节)" -f $it.Name, (Get-Item "$dir\$($it.Name)" -ErrorAction SilentlyContinue).Length)
}

# 3) 安装 1.0.3.0（含 JSC 依赖）
Push-Location $pkgDir
$ok = $false
for ($t = 0; $t -lt 10 -and -not $ok; $t++)
{
    $a = @('-sS','--noproxy','*','-m','600','-w','  HTTP%{http_code}','-X','POST',
        '-F','NovReader_1.0.3.0_ARM.appx=@NovReader_1.0.3.0_ARM.appx;type=application/octet-stream',
        '-F','Microsoft.NET.Native.Framework.1.7.appx=@Dependencies\arm\Microsoft.NET.Native.Framework.1.7.appx;type=application/octet-stream',
        '-F','Microsoft.NET.Native.Runtime.1.7.appx=@Dependencies\arm\Microsoft.NET.Native.Runtime.1.7.appx;type=application/octet-stream',
        "http://$ip/api/app/packagemanager/package?package=NovReader_1.0.3.0_ARM.appx")
    $out = & curl.exe @a 2>&1
    Log "安装尝试 $t : $out"
    if ("$out" -match 'HTTP202|HTTP200')
    {
        for ($i = 0; $i -lt 60; $i++)
        {
            Start-Sleep 5
            $st = curl.exe -s --noproxy '*' -m 10 -w " HTTP%{http_code}" "http://$ip/api/app/packagemanager/state"
            if ($st -notmatch 'HTTP204|HTTP000') { Log "安装状态: $st"; break }
        }
        $ok = "$st" -match 'Success.: true'
    }
    else { Start-Sleep 10 }
}
Pop-Location
if (-not $ok) { Log "安装失败"; exit 1 }

# 4) 启动 + 验证
$pf = 'NovReader_1.0.3.0_arm__7xxdx2kd68cm6'
$b = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('NovReader_7xxdx2kd68cm6!App'))
curl.exe -s --noproxy '*' -m 20 -w "启动 HTTP%{http_code}`n" -X POST -H "Content-Length: 0" "http://$ip/api/taskmanager/app?appid=$b" | Out-Null
Start-Sleep 15
$procs = (curl.exe -s --noproxy '*' -m 15 "http://$ip/api/resourcemanager/processes" | ConvertFrom-Json).Processes | Where-Object { $_.PackageFullName -match 'NovReader' }
Log "进程: " + ($procs | ForEach-Object { "$($_.ImageName) pid=$($_.ProcessId)" })
Log ("崩溃转储: " + ((curl.exe -s --noproxy '*' -m 15 "http://$ip/api/debug/dump/usermode/dumps") -match 'NovReader'))
curl.exe -s --noproxy '*' -m 30 -o "$dir\app-after.log" "http://$ip/api/filesystem/apps/file?knownfolderid=LocalAppData&filename=app.log&packagefullname=$pf&path=%5CLocalState"
Log "--- 启动后 app.log ---"
Get-Content "$dir\app-after.log" -Encoding UTF8 -Tail 15 | ForEach-Object { Log $_ }
Log "完成"
