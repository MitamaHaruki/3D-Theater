"""MediaPipe の Face Landmarker のモデル（.task）を取り寄せ、3 つの TFLite を ONNX に変えて、元と同じ答えになるかを確かめる。

使い方（Python 3.10。仮の環境で行う。アプリを使うだけなら要らない）:
    python -m venv venv
    venv\\Scripts\\pip install "tensorflow==2.15.1" "numpy<2" "tf2onnx==1.17.0" "onnx==1.16.2" "onnxruntime==1.19.2"
    venv\\Scripts\\python tools\\convert_face_models.py models\\face_landmarker

できるもの: face_detector.onnx・face_landmarks_detector.onnx・face_blendshapes.onnx（opset 17）
"""
import hashlib
import os
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

import numpy as np

URL = "https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task"
SHA256 = "64184e229b263107bc2b804c6625db1341ff2bb731874b0bcc2fe6544e0bc9ff"
NAMES = ("face_detector", "face_landmarks_detector", "face_blendshapes")


def main(out: str) -> None:
    os.makedirs(out, exist_ok=True)
    work = tempfile.mkdtemp()
    task = os.path.join(work, "face_landmarker.task")
    urllib.request.urlretrieve(URL, task)
    digest = hashlib.sha256(open(task, "rb").read()).hexdigest()
    if digest != SHA256:
        raise SystemExit(f"取り寄せたファイルが違います（SHA-256 {digest}）")
    with zipfile.ZipFile(task) as z:
        z.extractall(work)
    for name in NAMES:
        subprocess.check_call([sys.executable, "-m", "tf2onnx.convert", "--tflite", os.path.join(work, name + ".tflite"),
                               "--output", os.path.join(out, name + ".onnx"), "--opset", "17"])
    check(work, out)


def check(work: str, out: str) -> None:
    import onnxruntime as ort
    import tensorflow as tf
    rng = np.random.default_rng(1)
    for name in NAMES:
        tfl = tf.lite.Interpreter(model_path=os.path.join(work, name + ".tflite"))
        tfl.allocate_tensors()
        session = ort.InferenceSession(os.path.join(out, name + ".onnx"), providers=["CPUExecutionProvider"])
        info = tfl.get_input_details()[0]
        x = rng.random(info["shape"], dtype=np.float32) * (256 if name == "face_blendshapes" else 1)
        tfl.set_tensor(info["index"], x)
        tfl.invoke()
        expected = [tfl.get_tensor(t["index"]) for t in tfl.get_output_details()]
        actual = session.run(None, {session.get_inputs()[0].name: x})
        for e in expected:
            diff = min(float(np.max(np.abs(e.reshape(-1) - a.reshape(-1)))) for a in actual if a.size == e.size)
            print(f"{name}: 出力 {list(e.shape)} の元との差の最大 {diff:.2e}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join("models", "face_landmarker"))
