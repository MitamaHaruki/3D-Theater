<#
.SYNOPSIS
    3D Theater をビルドし、テストを実行する。

.EXAMPLE
    .\build.ps1                          # Debug でビルドしてテスト
    .\build.ps1 -Configuration Release   # Release でビルドしてテスト
    .\build.ps1 -SkipTests               # テストを飛ばす
    .\build.ps1 -Run                     # ビルド後にアプリを起動する
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'Visual Studio が見つかりません（vswhere.exe がありません）。README の「必要なもの」を確認してください。'
    }
    $path = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Workload.ManagedDesktop `
        -property installationPath
    if (-not $path) {
        throw '「C++ によるデスクトップ開発」と「.NET デスクトップ開発」を入れた Visual Studio が見つかりません。'
    }
    return $path
}

# C++ のプロジェクトを含むため、dotnet build ではなく Visual Studio の MSBuild を使う
$vs = Find-VisualStudio
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'

Write-Host "==> ビルド ($Configuration|x64)" -ForegroundColor Cyan
& $msbuild (Join-Path $root '3DTheater.slnx') -restore -m -nologo -v:minimal "-p:Configuration=$Configuration" '-p:Platform=x64'
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
                         'tests\3DTheater.Physics.Tests\3DTheater.Physics.Tests.csproj') {
        Write-Host "==> C# のテスト（$([IO.Path]::GetFileNameWithoutExtension($project))）" -ForegroundColor Cyan
        dotnet test (Join-Path $root $project) --no-build -c $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "C# のテストに失敗しました: $project" }
    }
}

$exe = Join-Path $root "src\3DTheater.App\bin\x64\$Configuration\net10.0-windows\3DTheater.exe"
Write-Host "==> 完了: $exe" -ForegroundColor Green
if ($Run) {
    Start-Process -FilePath $exe
}
