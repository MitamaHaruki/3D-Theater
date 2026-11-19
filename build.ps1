<#
.SYNOPSIS
    3D Theater をビルドし、テストを実行する。

.EXAMPLE
    .\build.ps1                          # Debug でビルドしてテスト
    .\build.ps1 -Configuration Release   # Release でビルドしてテスト
    .\build.ps1 -SkipTests               # テストを飛ばす
    .\build.ps1 -Run                     # ビルド後にアプリを起動する
    .\build.ps1 -Platform ARM64          # ARM64 の PC 向けに作る（テストは x64 だけなので流さない）
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',
    [switch]$SkipTests,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# テストは x64 の版だけで流す（ARM64 の版は、この PC では動かせないため。設計判断記録 94 章）
if ($Platform -ne 'x64' -and -not $SkipTests) {
    Write-Host "==> $Platform の版ではテストを流しません（テストは x64 の版で流してください）" -ForegroundColor Yellow
    $SkipTests = $true
}

function Find-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'Visual Studio が見つかりません（vswhere.exe がありません）。README の「必要なもの」を確認してください。'
    }
    $components = @('Microsoft.VisualStudio.Component.VC.Tools.x86.x64', 'Microsoft.VisualStudio.Workload.ManagedDesktop')
    if ($Platform -eq 'ARM64') {
        # ARM64 の版には、ARM64 と ARM64EC（仮想カメラの部品を ARM64X にする）の C++ の道具が要る
        $components += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64', 'Microsoft.VisualStudio.Component.VC.Tools.ARM64EC'
    }
    $path = & $vswhere -latest -products * -requires $components -property installationPath
    if (-not $path) {
        if ($Platform -eq 'ARM64') {
            throw '「C++ によるデスクトップ開発」と「.NET デスクトップ開発」に加えて、「MSVC ARM64/ARM64EC ビルド ツール」を入れた Visual Studio が見つかりません。'
        }
        throw '「C++ によるデスクトップ開発」と「.NET デスクトップ開発」を入れた Visual Studio が見つかりません。'
    }
    return $path
}

# C++ のプロジェクトを含むため、dotnet build ではなく Visual Studio の MSBuild を使う
$vs = Find-VisualStudio
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'

Write-Host "==> ビルド ($Configuration|$Platform)" -ForegroundColor Cyan
& $msbuild (Join-Path $root '3DTheater.slnx') -restore -m -nologo -v:minimal "-p:Configuration=$Configuration" "-p:Platform=$Platform"
if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました（終了コード $LASTEXITCODE）" }

if (-not $SkipTests) {
    Write-Host '==> C++ のテスト' -ForegroundColor Cyan
    $vstest = Join-Path $vs 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
    & $vstest (Join-Path $root "artifacts\native\x64\$Configuration\3DTheater.Engine.Native.Tests.dll") /Platform:x64
    if ($LASTEXITCODE -ne 0) { throw 'C++ のテストに失敗しました' }

    foreach ($project in 'tests\3DTheater.Engine.Interop.Tests\3DTheater.Engine.Interop.Tests.csproj',
                         'tests\3DTheater.Editor.Tests\3DTheater.Editor.Tests.csproj',
                         'tests\3DTheater.Formats.Tests\3DTheater.Formats.Tests.csproj',
                         'tests\3DTheater.Animation.Tests\3DTheater.Animation.Tests.csproj',
                         'tests\3DTheater.Physics.Tests\3DTheater.Physics.Tests.csproj',
                         'tests\3DTheater.Media.Tests\3DTheater.Media.Tests.csproj',
                         'tests\3DTheater.Tracking.Tests\3DTheater.Tracking.Tests.csproj',
                         'tests\Plugin.Core.Tests\Plugin.Core.Tests.csproj') {
        Write-Host "==> C# のテスト（$([IO.Path]::GetFileNameWithoutExtension($project))）" -ForegroundColor Cyan
        dotnet test (Join-Path $root $project) --no-build -c $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "C# のテストに失敗しました: $project" }
    }
}

$exe = Join-Path $root "src\3DTheater.App\bin\$Platform\$Configuration\net10.0-windows\3DTheater.exe"
Write-Host "==> 完了: $exe" -ForegroundColor Green
if ($Run) {
    Start-Process -FilePath $exe
}
