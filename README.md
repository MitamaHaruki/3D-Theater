# 3D Theater

MMD 系のモデルを使って 3D アニメーションを作る、Windows 用のソフト。
何を作るかは [開発指示書](3D_Theater_開発資料一式/3D_Theater_開発指示書.md) にまとめてある。

## 今の状態（Phase 0：開発の土台）

できていること:

- ビルド手順（`build.ps1`）と、C# / C++ のテスト
- C++ の描画エンジン（Direct3D 11）と、C# から呼ぶための境界
- モデルなしで起動し、確認用の 3D（床の格子・座標軸・回る立方体）を表示する画面
- WPF への 3D の埋め込み方を 2 通り並べて比べる画面（結果は [設計判断記録](docs/設計判断記録.md) の 3 章）
- ログファイル（`%LOCALAPPDATA%\3DTheater\Logs`）

まだできていないこと（Phase 1 以降）:

- タイトルバーへのメニュー統合、ツールバー、ドッキング画面、ダークテーマ（今の画面は Phase 0 の検証用）
- 視点の操作（回転・平行移動・ズーム）、固定 6 方向ビュー、1/2/4 画面
- モデル・モーションの読み込み、タイムライン、物理演算、書き出し
- GPU が使えなくなったときの自動復旧（今は描画を止めて知らせるだけ）

## 必要なもの

- Windows 11（x64）
- Visual Studio 2026（Community 可）に次の 2 つを入れたもの
  - 「C++ によるデスクトップ開発」
  - 「.NET デスクトップ開発」
- .NET 10 SDK（Visual Studio と一緒に入る）

## ビルドとテスト

PowerShell でこのフォルダーを開いて実行する。

```powershell
.\build.ps1
```

- `-Configuration Release` で Release ビルド
- `-SkipTests` でテストを飛ばす
- `-Run` でビルドのあとアプリを起動する

スクリプトの実行が止められる場合は次のようにする。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Visual Studio で開く場合は `3DTheater.slnx` を開き、`3DTheater.App` をスタートアップにして実行する。
C++ のコードも続けてデバッグできるよう設定してある。

> C++ のプロジェクトを含むため、`dotnet build` ではビルドできない。`build.ps1` か Visual Studio を使う。

## 起動

```text
src\3DTheater.App\bin\x64\Debug\net10.0-windows\3DTheater.exe
```

| オプション | 意味 |
|---|---|
| `--warp` | GPU を使わないソフトウェア描画で起動する（診断用） |
| `--debug-layer` | Release でも Direct3D の検査機能を使う（Debug では常に使う） |

GPU での初期化に失敗したときは、自動でソフトウェア描画に切り替えて起動する。

## フォルダー構成

```text
3DTheater.slnx                     ソリューション
build.ps1                          ビルドとテスト
src/
  3DTheater.App/                   WPF の画面
  3DTheater.Engine.Interop/        C# から C++ を呼ぶ境界
  3DTheater.Engine.Native/         C++ の描画エンジン（公開する関数は include/tt_engine.h）
tests/
  3DTheater.Engine.Interop.Tests/  C# 側から見たエンジンのテスト
  3DTheater.Engine.Native.Tests/   C++ の公開関数のテスト
docs/
  設計判断記録.md                  決めたことと理由、持ち越し事項
  ライセンス一覧.md                使っている部品と利用条件
3D_Theater_開発資料一式/           開発指示書と画面の参考画像
artifacts/                         C++ のビルド結果（Git では管理しない）
```
