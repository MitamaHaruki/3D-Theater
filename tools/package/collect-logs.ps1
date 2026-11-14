# 3D Theater の確かめの結果を集める（確かめ用の zip に入れる道具）。
# ログ（速さの行だけを抜き出した speed.txt も）と、パソコンの様子（Windows の版・CPU・メモリ・GPU・カメラ・画面・カメラの使用の許可）と、アプリが落ちたときの Windows の記録を、
# 1 つの zip にしてデスクトップに置く。顔の映像や写真は入れない。
# Windows PowerShell 5.1 でも動くように書く（このファイルは BOM 付きの UTF-8 にする。BOM が無いと 5.1 は日本語を読み違える）。
param(
    [string]$OutDir = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$work = Join-Path $env:TEMP "3DTheater-check-$stamp"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $work 'logs') | Out-Null
Write-Host '3D Theater の確かめの結果を集めています…'

# ログ（この 14 日の分）
$logDir = Join-Path $env:LOCALAPPDATA '3DTheater\Logs'
$logs = @()
if (Test-Path $logDir) {
    $logs = @(Get-ChildItem $logDir -Filter '*.log' | Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-14) })
    foreach ($log in $logs) { Copy-Item $log.FullName (Join-Path $work 'logs') }
}

# 速さのログ（ライブモードの 1 分ごとの速さと重さ・まとめ。設計判断記録 77 章）と、起動・終了・カメラ・エラー・警告の行を、
# 読みやすいよう別のファイルにも抜き出す（ログは BOM の無い UTF-8 なので、読み方を指定する）
$speed = New-Object System.Collections.Generic.List[string]
foreach ($log in $logs) {
    foreach ($line in (Get-Content $log.FullName -Encoding UTF8)) {
        if ($line -match '\[(速さ|重さ|まとめ)\]|\] CPU: |カメラ|起動します|終了します|\[(ERROR|WARN )\]') { $speed.Add($line) }
    }
}
[System.IO.File]::WriteAllLines((Join-Path $work 'speed.txt'), $speed, (New-Object System.Text.UTF8Encoding $true))

# パソコンの様子
$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$text) { $lines.Add($text) }
Add-Line "集めた日時: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
try {
    $os = Get-CimInstance Win32_OperatingSystem
    Add-Line "Windows: $($os.Caption)（版 $($os.Version)・ビルド $($os.BuildNumber)・$($os.OSArchitecture)）"
    Add-Line ("メモリ: {0:F1} GB" -f ($os.TotalVisibleMemorySize / 1MB))
} catch { Add-Line "Windows: 読めませんでした（$($_.Exception.Message)）" }
try {
    foreach ($cpu in Get-CimInstance Win32_Processor) { Add-Line "CPU: $($cpu.Name.Trim())（コア $($cpu.NumberOfCores)・スレッド $($cpu.NumberOfLogicalProcessors)）" }
} catch { Add-Line "CPU: 読めませんでした" }
try {
    foreach ($gpu in Get-CimInstance Win32_VideoController) {
        Add-Line ("GPU: {0}（ドライバー {1}・{2} × {3}・{4} Hz）" -f $gpu.Name, $gpu.DriverVersion, $gpu.CurrentHorizontalResolution, $gpu.CurrentVerticalResolution, $gpu.CurrentRefreshRate)
    }
} catch { Add-Line "GPU: 読めませんでした" }
try {
    $cameras = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.Class -in 'Camera', 'Image' })
    if ($cameras.Count -eq 0) { Add-Line 'カメラ: 見つかりません' }
    # Image の仲間には、スキャナーも入る
    foreach ($camera in $cameras) { Add-Line "カメラ・スキャナー: $($camera.FriendlyName)（$($camera.Class)・状態 $($camera.Status)）" }
} catch { Add-Line "カメラ: 読めませんでした（$($_.Exception.Message)）" }
try {
    Add-Type -AssemblyName System.Windows.Forms
    foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
        Add-Line ("画面: {0} × {1}{2}" -f $screen.Bounds.Width, $screen.Bounds.Height, $(if ($screen.Primary) { '（主）' } else { '' }))
    }
} catch { Add-Line "画面: 読めませんでした" }
$consent = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam'
$allow = (Get-ItemProperty $consent -ErrorAction SilentlyContinue).Value
$desktop = (Get-ItemProperty (Join-Path $consent 'NonPackaged') -ErrorAction SilentlyContinue).Value
Add-Line "カメラの使用の許可: カメラへのアクセス = $allow・デスクトップ アプリ = $desktop（Allow なら許可）"
$exe = Join-Path $PSScriptRoot '3DTheater.exe'
if (Test-Path $exe) { Add-Line "3D Theater: 版 $((Get-Item $exe).VersionInfo.ProductVersion)（$PSScriptRoot）" }
Add-Line "ログ: $($logs.Count) 個"
[System.IO.File]::WriteAllLines((Join-Path $work 'system-info.txt'), $lines, (New-Object System.Text.UTF8Encoding $true))

# アプリが落ちたときの Windows の記録（この 7 日。3D Theater のものだけ）
try {
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-7) } -ErrorAction Stop |
        Where-Object { $_.ProviderName -in 'Application Error', '.NET Runtime', 'Windows Error Reporting', 'Application Hang' -and $_.Message -match '3DTheater' } |
        Select-Object -First 20)
    $text = if ($events.Count -eq 0) { '（3D Theater が落ちた記録はありません）' } else {
        ($events | ForEach-Object { "==== $($_.TimeCreated)　$($_.ProviderName)`r`n$($_.Message)" }) -join "`r`n`r`n"
    }
    [System.IO.File]::WriteAllText((Join-Path $work 'windows-events.txt'), $text, (New-Object System.Text.UTF8Encoding $true))
} catch {
    [System.IO.File]::WriteAllText((Join-Path $work 'windows-events.txt'), "読めませんでした: $($_.Exception.Message)", (New-Object System.Text.UTF8Encoding $true))
}

# 1 つの zip にする
$zip = Join-Path $OutDir "3DTheater_確かめの結果_$stamp.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip
Remove-Item $work -Recurse -Force
Write-Host ''
Write-Host "できました: $zip"
Write-Host 'メモと一緒に持ち帰ってください。'
