// ModelPS から、レイトレーシングでの影（光線をたどる処理）を外した軽い版（設計判断記録 90 章）。
// レイトレーシングでの影を使わない回の、レンダーパス・床の映り込み・捨てる処理の要る描画に使う。色の計算は ModelPS と同じ
#define MODEL_NO_RAYS 1
#include "ModelPS.hlsl"
