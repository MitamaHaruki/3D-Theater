"""MediaPipe の Pose Landmarker のモデル（.task）を取り寄せ、2 つの TFLite を ONNX に変えて、元と同じ答えになるかを確かめる（設計判断記録 100 章）。

使い方（Python 3.10。仮の環境で行う。アプリを使うだけなら要らない。手のモデルの変換と同じ環境でよい）:
    python -m venv venv
    venv\\Scripts\\pip install "tensorflow==2.15.1" "numpy<2" "tf2onnx==1.17.0" "onnx==1.16.2" "onnxruntime==1.19.2"
    venv\\Scripts\\python tools\\convert_pose_models.py models\\pose_landmarker [lite|full]

できるもの（opset 17）:
    pose_detector.onnx（体を探す。224 × 224。色は -1〜1）
    pose_landmarks_detector.onnx（体の点。256 × 256。色は 0〜1。出力は 39 点の画素の値・体らしさ・39 点のメートルの値の 3 つ）
アプリが使うのは軽い版（lite）。標準の版（full）は、点の揺れが同じほどで、時間が 2 倍近くかかった（設計判断記録 100 章）。
"""
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

import numpy as np

VARIANTS = {
    "lite": ("https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task",
             "59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a"),
    "full": ("https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/1/pose_landmarker_full.task",
             "5134a3aad27a58b93da0088d431f366da362b44e3ccfbe3462b3827a839011b1"),
}
NAMES = ("pose_detector", "pose_landmarks_detector")

# 体の点のモデルの出力のうち、使うもの（画素の点・体らしさ・メートルの点）。人の形の切り抜き（Identity_2。256 × 256）と点の濃さの図
# （Identity_3。64 × 64 × 39）は使わないので外す（それを作る所が要らなくなり、時間が半分ほどになる）
KEEP_OUTPUTS = ["Identity", "Identity_1", "Identity_4"]

# tf2onnx は、テンソルの形を確かめるためだけに tf.lite.Interpreter の get_tensor_details を呼ぶ。体を探すモデルでは、それが疎な形の
# テンソルで落ちる（TensorFlow 2.15.1）。読めなければモデルに書かれた形を使う作りなので、読めないことにする
CONVERT = """
import sys
import tensorflow as tf
class _NoInterpreter:
    def __init__(self, *args, **kwargs):
        raise RuntimeError("get_tensor_details が落ちるので使わない")
tf.lite.Interpreter = _NoInterpreter
from tf2onnx import convert
sys.argv = ["tf2onnx.convert"] + sys.argv[1:]
convert.main()
"""


def main(out: str, variant: str) -> None:
    url, sha256 = VARIANTS[variant]
    os.makedirs(out, exist_ok=True)
    work = tempfile.mkdtemp()
    task = os.path.join(work, os.path.basename(url))
    urllib.request.urlretrieve(url, task)
    digest = hashlib.sha256(open(task, "rb").read()).hexdigest()
    if digest != sha256:
        raise SystemExit(f"取り寄せたファイルが違います（SHA-256 {digest}）")
    with zipfile.ZipFile(task) as z:
        z.extractall(work)
    densify(os.path.join(work, "pose_detector.tflite"))
    for name in NAMES:
        subprocess.check_call([sys.executable, "-c", CONVERT, "--tflite", os.path.join(work, name + ".tflite"),
                               "--output", os.path.join(out, name + ".onnx"), "--opset", "17"])
    prune(os.path.join(out, "pose_landmarks_detector.onnx"))
    check(work, out)


def densify(path: str) -> None:
    """
    体を探すモデルは、重みの一部を疎な形（0 を省いた形。TFLite の sparsity）で持ち、使う前に DENSIFY で戻す。tf2onnx はこの形を
    読めないので、重みをふつうの形に戻し、DENSIFY を除いて書き直す（書き直す前のものは別に残し、check で答えを比べる）。
    """
    from tensorflow.lite.python import schema_py_generated as fb
    from tensorflow.lite.tools import flatbuffer_utils as fu
    shutil.copyfile(path, original(path))
    model = fu.read_model(path)
    graph = model.subgraphs[0]
    count = 0
    for tensor in graph.tensors:
        s = tensor.sparsity
        if s is None:
            continue
        dims = list(s.dimMetadata)
        # この形だけ扱う: 並べ替えなし・ブロックなし・最後の次元だけ CSR（行の区切りと列の番号）
        if list(s.traversalOrder) != list(range(len(tensor.shape))) or s.blockMap is not None \
                or any(d.format != fb.DimensionType.DENSE for d in dims[:-1]) or dims[-1].format != fb.DimensionType.SPARSE_CSR:
            raise SystemExit(f"扱えない疎な形です: {tensor.name}")
        dtype = {fb.TensorType.FLOAT16: np.float16, fb.TensorType.FLOAT32: np.float32}[tensor.type]
        values = np.frombuffer(bytes(model.buffers[tensor.buffer].data), dtype=dtype)
        segments = np.asarray(dims[-1].arraySegments.values, dtype=np.int64)
        indices = np.asarray(dims[-1].arrayIndices.values, dtype=np.int64)
        rows = int(np.prod(tensor.shape[:-1]))
        dense = np.zeros((rows, tensor.shape[-1]), dtype=dtype)
        for r in range(rows):
            dense[r, indices[segments[r]:segments[r + 1]]] = values[segments[r]:segments[r + 1]]
        model.buffers[tensor.buffer].data = np.frombuffer(dense.tobytes(), dtype=np.uint8)
        tensor.sparsity = None
        count += 1
    codes = [max(c.builtinCode, c.deprecatedBuiltinCode) for c in model.operatorCodes]
    kept = []
    for op in graph.operators:
        if codes[op.opcodeIndex] != fb.BuiltinOperator.DENSIFY:
            kept.append(op)
            continue
        source, target = op.inputs[0], op.outputs[0]
        for other in graph.operators:
            other.inputs = [source if i == target else i for i in other.inputs]
        graph.outputs = [source if i == target else i for i in graph.outputs]
    graph.operators = kept
    fu.write_model(model, path)
    print(f"体を探すモデル: 疎な形の重み {count} 個をふつうの形に戻しました")


def prune(path: str) -> None:
    """体の点のモデルから、使わない出力と、それだけを作る所を外す。"""
    import onnx
    from onnx import utils
    model = onnx.load(path)
    initializers = {i.name for i in model.graph.initializer}
    inputs = [i.name for i in model.graph.input if i.name not in initializers]
    pruned = utils.Extractor(model).extract_model(inputs, KEEP_OUTPUTS)
    onnx.save(pruned, path)
    print(f"体の点のモデル: 出力を {KEEP_OUTPUTS} だけにしました（処理 {len(model.graph.node)} → {len(pruned.graph.node)}）")


def original(path: str) -> str:
    return path[:-len(".tflite")] + "_original.tflite"


def check(work: str, out: str) -> None:
    import onnxruntime as ort
    import tensorflow as tf
    rng = np.random.default_rng(1)
    for name in NAMES:
        path = os.path.join(work, name + ".tflite")
        tfl = tf.lite.Interpreter(model_path=original(path) if os.path.exists(original(path)) else path)  # 書き直す前と比べる
        tfl.allocate_tensors()
        session = ort.InferenceSession(os.path.join(out, name + ".onnx"), providers=["CPUExecutionProvider"])
        info = tfl.get_input_details()[0]
        x = rng.random(info["shape"], dtype=np.float32)
        tfl.set_tensor(info["index"], x)
        tfl.invoke()
        expected = {t["name"]: tfl.get_tensor(t["index"]) for t in tfl.get_output_details()}
        names = [o.name for o in session.get_outputs()]
        actual = dict(zip(names, session.run(None, {session.get_inputs()[0].name: x})))
        print(f"{name}: 入力 {session.get_inputs()[0].name} {session.get_inputs()[0].shape}")
        for key, a in actual.items():
            e = expected[key]
            print(f"  出力 {key} {list(e.shape)} の元との差の最大 {float(np.max(np.abs(e.reshape(-1) - a.reshape(-1)))):.2e}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join("models", "pose_landmarker"), sys.argv[2] if len(sys.argv) > 2 else "lite")
