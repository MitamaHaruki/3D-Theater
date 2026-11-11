// 環境の画像（全方向の景色を、横 360 度・縦 180 度に広げた画像）の読み方。テクスチャは読み込む側で宣言する。
// 画像の真ん中が +Z（MMD の初期のカメラが見る向き）、右へ 4 分の 1 進むと +X、上の端が真上。

static const float kEnvironmentPi = 3.14159265;

// 景色を Y 軸のまわりに rotation（ラジアン。XMMatrixRotationY と同じ向き）だけ回したとき、ワールドの向き dir に見える、
// 元の画像の向き
float3 EnvironmentDirection(float3 dir, float rotation)
{
    float s = sin(rotation);
    float c = cos(rotation);
    return float3(c * dir.x - s * dir.z, dir.y, s * dir.x + c * dir.z);
}

// 元の画像の向きを、画像の座標（0〜1）にする
float2 EnvironmentUv(float3 dir)
{
    float3 d = normalize(dir);
    return float2(0.5 + atan2(d.x, d.z) * (0.5 / kEnvironmentPi), acos(clamp(d.y, -1.0, 1.0)) / kEnvironmentPi);
}

// 球面調和関数（2 次まで、9 個）の基底。描画エンジンが係数を求めたときと同じ並び
void EnvironmentBasis(float3 d, out float basis[9])
{
    basis[0] = 0.282095;
    basis[1] = 0.488603 * d.y;
    basis[2] = 0.488603 * d.z;
    basis[3] = 0.488603 * d.x;
    basis[4] = 1.092548 * d.x * d.y;
    basis[5] = 1.092548 * d.y * d.z;
    basis[6] = 0.315392 * (3.0 * d.y * d.y - 1.0);
    basis[7] = 1.092548 * d.x * d.z;
    basis[8] = 0.546274 * (d.x * d.x - d.z * d.z);
}
