<#
.SYNOPSIS
    確かめ用の zip を作る。ほかの PC（カメラ付きのノートパソコンなど）で、展開するだけで動く。

.DESCRIPTION
    Release でビルドし、.NET を中に入れて発行（self-contained）する。C++ の部品が使う Visual C++ の実行時ライブラリも、
    アプリの隣に置く（app-local。Visual Studio の再頒布用のフォルダーから写す）。確かめの手順書・Claude への指示書・
    ログを集める道具・ライセンス一覧を足して、artifacts\package に zip を作る（指示書は zip の隣にも置く）。
    テストは流さない（先に .\build.ps1 で流しておく）。

.EXAMPLE
    .\package.ps1                     # artifacts\package\3DTheater-確かめ用-日付.zip を作る
    .\package.ps1 -OutDir D:\受け渡し  # 置き場所を変える
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'artifacts\package')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'Visual Studio が見つかりません（vswhere.exe がありません）。README の「必要なもの」を確認してください。'
    }
    $path = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $path) {
        throw '「C++ によるデスクトップ開発」を入れた Visual Studio が見つかりません。'
    }
    return $path
}

# 1) Release でビルドする（C++ の DLL と、サンプルのプラグインを作る）
& (Join-Path $root 'build.ps1') -Configuration Release -SkipTests
$built = Join-Path $root 'src\3DTheater.App\bin\x64\Release\net10.0-windows'

# 2) .NET を中に入れて発行する
$vs = Find-VisualStudio
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
$stage = Join-Path $OutDir '3D Theater'
if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}
New-Item -ItemType Directory -Force $stage | Out-Null
Write-Host '==> 発行（.NET を中に入れる）' -ForegroundColor Cyan
& $msbuild (Join-Path $root 'src\3DTheater.App\3DTheater.App.csproj') -restore -t:Publish -nologo -v:minimal -m `
    '-p:Configuration=Release' '-p:Platform=x64' '-p:RuntimeIdentifier=win-x64' '-p:SelfContained=true' "-p:PublishDir=$stage\"
if ($LASTEXITCODE -ne 0) { throw "発行に失敗しました（終了コード $LASTEXITCODE）" }

# 3) 発行には入らないもの: サンプルのプラグイン（ビルドの後に出力へ写している）と、C++ の DLL（無ければ写す）
Copy-Item (Join-Path $built 'Plugins') $stage -Recurse -Force
foreach ($native in '3DTheater.Engine.Native', '3DTheater.Physics.Native', '3DTheater.Video.Native', '3DTheater.VirtualCamera') {
    foreach ($extension in '.dll', '.pdb') {
        $file = Join-Path $built ($native + $extension)
        if ((Test-Path $file) -and -not (Test-Path (Join-Path $stage ($native + $extension)))) {
            Copy-Item $file $stage
        }
    }
}

# 4) Visual C++ の実行時ライブラリ（v145）をアプリの隣に置く（相手の PC に再頒布パッケージを入れなくてよいように）
$crt = Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC\*\x64\Microsoft.VC145.CRT') -Directory | Sort-Object FullName | Select-Object -Last 1
if (-not $crt) { throw 'Visual C++ の再頒布用の実行時ライブラリ（Microsoft.VC145.CRT）が見つかりません' }
Copy-Item (Join-Path $crt.FullName '*.dll') $stage -Force
Write-Host "==> Visual C++ の実行時ライブラリ: $($crt.FullName)" -ForegroundColor Cyan

# 5) 確かめの手順書・Claude への指示書・ログを集める道具・ライセンス一覧
$package = Join-Path $root 'tools\package'
$instructions = Join-Path $package 'Claude への指示書.md'
Copy-Item (Join-Path $package '確かめの手順.txt') $stage
Copy-Item $instructions $stage
Copy-Item (Join-Path $package 'ログを集める.cmd') $stage
Copy-Item (Join-Path $package 'collect-logs.ps1') $stage
Copy-Item (Join-Path $root 'docs\ライセンス一覧.md') $stage

# 6) 要る物がそろっているか確かめる
$required = '3DTheater.exe', '3DTheater.dll', 'coreclr.dll', 'PresentationFramework.dll', 'onnxruntime.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
    'msvcp140.dll', 'msvcp140_1.dll', '3DTheater.Engine.Native.dll', '3DTheater.Physics.Native.dll', '3DTheater.Video.Native.dll',
    '3DTheater.VirtualCamera.dll', 'Models\FaceLandmarker\face_landmarks_detector.onnx', 'Plugins\SceneInfo\plugin.json'
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $stage $_)) })
if ($missing.Count -gt 0) { throw "zip に入れる物が足りません: $($missing -join ', ')" }
$config = Get-Content (Join-Path $stage '3DTheater.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $config.runtimeOptions.includedFrameworks) { throw '.NET が中に入っていません（self-contained になっていません）' }

# 7) zip にする（中の名前は UTF-8）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$version = (Get-Item (Join-Path $stage '3DTheater.exe')).VersionInfo.ProductVersion
$zip = Join-Path $OutDir ('3DTheater-確かめ用-{0}-{1}.zip' -f ($version -replace '\+.*$', ''), (Get-Date -Format 'yyyyMMdd-HHmm'))
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true, [System.Text.Encoding]::UTF8)
$files = Get-ChildItem $stage -Recurse -File
# ほかの PC の Claude が zip を開く前に読めるよう、指示書は zip の隣にも置く
Copy-Item $instructions $OutDir -Force
Write-Host ('==> 完了: {0}（{1:F0} MB。展開すると {2} 個・{3:F0} MB）' -f $zip, ((Get-Item $zip).Length / 1MB), $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)) -ForegroundColor Green
