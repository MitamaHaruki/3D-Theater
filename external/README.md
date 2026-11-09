# 取り込んだ外部のソース

ここにあるファイルは、ほかのプロジェクトのものをそのまま写したもの。**中身は書き換えない**（直したくなったら、写し直すか、使う側で工夫する）。

## bullet3（Bullet Physics 3.25）

- 用途: 物理演算（剛体とジョイント）。`src/3DTheater.Physics.Native` が静的ライブラリとして組み込む。
- 入手元: https://github.com/bulletphysics/bullet3 の タグ `3.25`（`https://codeload.github.com/bulletphysics/bullet3/zip/refs/tags/3.25`、zip の SHA-256 は `B9BC8D1443637A9084E2B585ED582ABF2DA3DDAD7D768ACCCFE4EE17ACA56BF7`）
- 写した範囲: `LICENSE.txt`・`VERSION`・`AUTHORS.txt`、`src/LinearMath`・`src/BulletCollision`・`src/BulletDynamics`、`src/btBulletCollisionCommon.h`・`src/btBulletDynamicsCommon.h`。それ以外（例・テスト・pybullet・Extras など）は使わないので写していない。
- ライセンス: zlib（`bullet3/LICENSE.txt`）。商用を含めて自由に使え、配布もできる。元の作者を偽らないこと、書き換えたら書き換えたと分かるようにすること、ライセンスの表示を消さないことが条件。
- ビルド: `3DTheater.Bullet.vcxproj`（静的ライブラリ）。警告は出さない設定にしている（外部のコードのため）。
- 改行: `.gitattributes` で変換しない設定にし、取り込んだときのバイト列のまま置いている。
