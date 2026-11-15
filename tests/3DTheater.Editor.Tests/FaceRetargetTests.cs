using System.Numerics;

namespace Theater3D.Editor.Tests;

/// <summary>顔のトラッキングを、キャラクターのボーンとモーフに当てはめる計算。</summary>
[TestClass]
public sealed class FaceRetargetTests
{
    private static readonly Vector3 Forward = new(0, 0, -1); // モデルの前（カメラの方）
    private static readonly Vector3 Up = Vector3.UnitY;

    private static FaceSignal Face(float yaw = 0, float pitch = 0, float roll = 0, float x = 360, float size = 300, params (string Name, float Value)[] blend)
    {
        var values = new Dictionary<string, float> { ["eyeBlinkLeft"] = 0.1f, ["eyeBlinkRight"] = 0.1f };
        foreach (var (name, value) in blend)
        {
            values[name] = value;
        }
        return new FaceSignal(yaw, pitch, roll, new Vector2(x, 500), size, values);
    }

    // 同じ顔を 3 秒続けて渡し、落ち着いた結果を返す
    private static FaceOutput Settle(FaceRetarget retarget, FaceSignal? face, double start = 0)
    {
        FaceOutput output = FaceOutput.Neutral;
        for (int i = 0; i <= 90; i++)
        {
            output = retarget.Compute(face, start + (i / 30.0));
        }
        return output;
    }

    private static FaceRetarget Calibrated(Action<FaceRetargetSettings>? configure = null)
    {
        var settings = new FaceRetargetSettings();
        configure?.Invoke(settings);
        var retarget = new FaceRetarget(settings);
        retarget.Calibrate(Face());
        return retarget;
    }

    private static Quaternion Total(FaceOutput o) => o.UpperBody * o.Neck * o.Head;

    [TestMethod]
    public void ScreenDirectionsBecomeModelRotations()
    {
        Assert.IsGreaterThan(0.3f, Vector3.Transform(Forward, FaceRetarget.ModelRotation(20, 0, 0)).X, "画面の右（モデルの +X）を向く");
        Assert.IsGreaterThan(0.15f, Vector3.Transform(Forward, FaceRetarget.ModelRotation(0, 10, 0)).Y, "上を向く");
        Assert.IsGreaterThan(0.2f, Vector3.Transform(Up, FaceRetarget.ModelRotation(0, 0, 15)).X, "画面の上で時計回り（頭の上が画面の右へ）");
    }

    [TestMethod]
    public void TurnsLikeTheVideoOrLikeAMirror()
    {
        Assert.IsFalse(new FaceRetargetSettings().Mirror, "既定は映像と同じ向き");

        // 本人が自分の右を向く → カメラの絵では左を向く（左右の値は負）
        FaceSignal turnedRight = Face(yaw: -25);
        Vector3 video = Vector3.Transform(Forward, Total(Settle(Calibrated(), turnedRight)));
        Assert.IsLessThan(-0.3f, video.X, "映像と同じ向き: キャラクターの右（画面の左）へ向く");
        Vector3 mirrored = Vector3.Transform(Forward, Total(Settle(Calibrated(s => s.Mirror = true), turnedRight)));
        Assert.IsGreaterThan(0.3f, mirrored.X, "鏡のように: 画面の右へ向く");

        // 本人が右へかしげる → 絵の上で反時計回り（傾きは負）→ 映像と同じ向き: 画面の上でも反時計回り。鏡のように: 時計回り
        Vector3 tilted = Vector3.Transform(Up, Total(Settle(Calibrated(s => s.BodySway = false), Face(roll: -20))));
        Assert.IsLessThan(-0.25f, tilted.X);
        Vector3 tiltedMirror = Vector3.Transform(Up, Total(Settle(Calibrated(s => { s.BodySway = false; s.Mirror = true; }), Face(roll: -20))));
        Assert.IsGreaterThan(0.25f, tiltedMirror.X);
    }

    [TestMethod]
    public void TheTurnIsSharedByUpperBodyNeckAndHead()
    {
        FaceOutput output = Settle(Calibrated(s => s.BodySway = false), Face(yaw: -30));
        float Angle(Quaternion q) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(q.W), -1f, 1f)) * 180 / MathF.PI;
        Assert.AreEqual(30f * FaceRetarget.UpperBodyShare, Angle(output.UpperBody), 0.5f);
        Assert.AreEqual(30f * FaceRetarget.NeckShare, Angle(output.Neck), 0.5f);
        Assert.AreEqual(30f * (1 - FaceRetarget.UpperBodyShare - FaceRetarget.NeckShare), Angle(output.Head), 0.5f);
        Assert.AreEqual(30f, Angle(Total(output)), 0.5f, "合わせると本人の向き");

        FaceOutput noBody = Settle(Calibrated(s => { s.UpperBody = false; s.BodySway = false; }), Face(yaw: -30));
        Assert.AreEqual(Quaternion.Identity, noBody.UpperBody, "上半身を使わない");
    }

    [TestMethod]
    public void CalibrationRemovesTheNeutralPose()
    {
        var retarget = new FaceRetarget(new FaceRetargetSettings());
        FaceSignal neutral = FaceRetarget.Average([Face(roll: -4, pitch: 3), Face(roll: -5, pitch: 3), Face(roll: -3, pitch: 3)]);
        Assert.AreEqual(-4f, neutral.Roll, 1e-5f, "何枚かの平均");
        retarget.Calibrate(neutral);
        Assert.IsTrue(retarget.IsCalibrated);
        FaceOutput output = Settle(retarget, Face(roll: -4, pitch: 3));
        Assert.AreEqual(1f, Total(output).W, 1e-4f, "正面のときの傾きは使わない");
    }

    [TestMethod]
    public void BlinksAndWinksFollowTheMirror()
    {
        // 本人の右目を閉じる（MediaPipe は閉じても 0.6〜0.8）
        FaceSignal rightClosed = Face(blend: ("eyeBlinkRight", 0.7f));
        FaceOutput video = Settle(Calibrated(), rightClosed);
        Assert.AreEqual(1f, video.Morphs[FaceRetarget.WinkRight], 1e-3f, "映像と同じ向き: キャラクターの右目");
        Assert.AreEqual(0f, video.Morphs[FaceRetarget.WinkLeft], 1e-3f);
        Assert.AreEqual(0f, video.Morphs[FaceRetarget.Blink], 1e-3f);
        FaceOutput mirrored = Settle(Calibrated(s => s.Mirror = true), rightClosed);
        Assert.AreEqual(1f, mirrored.Morphs[FaceRetarget.WinkLeft], 1e-3f, "鏡のように: キャラクターの左目");
        Assert.AreEqual(0f, mirrored.Morphs[FaceRetarget.WinkRight], 1e-3f);

        FaceOutput both = Settle(Calibrated(), Face(blend: [("eyeBlinkLeft", 0.65f), ("eyeBlinkRight", 0.7f)]));
        Assert.AreEqual(1f, both.Morphs[FaceRetarget.Blink], 1e-3f, "両目ならまばたき");
        Assert.AreEqual(0f, both.Morphs[FaceRetarget.WinkLeft] + both.Morphs[FaceRetarget.WinkRight], 1e-3f);

        Assert.AreEqual(0f, FaceRetarget.BlinkAmount(0.14f, 0.1f), "正面とほぼ同じなら開いたまま");
        Assert.AreEqual(0.5f, FaceRetarget.BlinkAmount(0.325f, 0.1f), 1e-3f);
        Assert.AreEqual(0.5f, FaceRetarget.BlinkAmount(0.45f, 0.1f, FaceRetarget.PhoneBlinkRange), 1e-3f, "スマホ（ARKit）は広く");

        // スマホの値は、半分閉じても閉じきりにならない
        FaceRetarget phone = Calibrated();
        phone.Calibrate(Face() with { BlinkRange = FaceRetarget.PhoneBlinkRange });
        FaceOutput half = Settle(phone, Face(blend: [("eyeBlinkLeft", 0.45f), ("eyeBlinkRight", 0.45f)]) with { BlinkRange = FaceRetarget.PhoneBlinkRange });
        Assert.AreEqual(0.5f, half.Morphs[FaceRetarget.Blink], 0.02f);
    }

    [TestMethod]
    public void SmallEyeDifferencesAndTurnedHeadsDoNotWink()
    {
        // 正面で片目だけ閉じたら、そのままウィンク
        Assert.AreEqual((1f, 0f), FaceRetarget.LinkEyes(1f, 0f, 0f));
        // 片目だけ少し細めた（差が小さい）なら、両目をそろえる
        (float left, float right) = FaceRetarget.LinkEyes(0.4f, 0.2f, 0f);
        Assert.AreEqual(0.3f, left, 1e-4f);
        Assert.AreEqual(0.3f, right, 1e-4f);
        // 差のうち、そろえる幅を越えた分だけ残す
        (left, right) = FaceRetarget.LinkEyes(0.75f, 0f, 0f);
        Assert.AreEqual(0.375f + (0.5f / 0.75f * 0.5f), left, 1e-4f);
        Assert.AreEqual(0.375f - (0.5f / 0.75f * 0.5f), right, 1e-4f);
        // 頭を横へ向けると、片目を閉じていても両目をそろえていく（30° でそろう、20° で半分）
        Assert.AreEqual((0.5f, 0.5f), FaceRetarget.LinkEyes(1f, 0f, -30f));
        (left, right) = FaceRetarget.LinkEyes(1f, 0f, 20f);
        Assert.AreEqual(0.75f, left, 1e-4f);
        Assert.AreEqual(0.25f, right, 1e-4f);

        // 横や上を向いて、片目だけ閉じ気味に読めても、キャラクターはウィンクしない
        FaceOutput turned = Settle(Calibrated(), Face(yaw: 32f, blend: [("eyeBlinkLeft", 0.3f), ("eyeBlinkRight", 0.08f)]));
        Assert.AreEqual(0f, turned.Morphs[FaceRetarget.WinkLeft] + turned.Morphs[FaceRetarget.WinkRight], 1e-3f);
        FaceOutput up = Settle(Calibrated(), Face(pitch: 36f, blend: [("eyeBlinkLeft", 0.39f), ("eyeBlinkRight", 0.22f)]));
        Assert.AreEqual(0f, up.Morphs[FaceRetarget.WinkLeft] + up.Morphs[FaceRetarget.WinkRight], 1e-3f);
        Assert.IsGreaterThan(0.2f, up.Morphs[FaceRetarget.Blink], "そろえた分は、両目のまばたきになる");
    }

    [TestMethod]
    public void MouthShapesApproximateTheVowels()
    {
        FaceOutput a = Settle(Calibrated(), Face(blend: ("jawOpen", 0.8f)));
        Assert.IsGreaterThan(0.9f, a.Morphs[FaceRetarget.A], "口を大きく開けると「あ」");
        Assert.AreEqual(0f, a.Morphs[FaceRetarget.U], 1e-3f);

        FaceOutput u = Settle(Calibrated(), Face(blend: [("mouthPucker", 0.9f), ("jawOpen", 0.1f)]));
        Assert.IsGreaterThan(0.9f, u.Morphs[FaceRetarget.U], "すぼめると「う」");
        Assert.AreEqual(0f, u.Morphs[FaceRetarget.A], 1e-3f);

        FaceOutput o = Settle(Calibrated(), Face(blend: [("mouthFunnel", 0.6f), ("jawOpen", 0.3f)]));
        Assert.IsGreaterThan(0.8f, o.Morphs[FaceRetarget.O], "とがらせると「お」");

        FaceOutput i = Settle(Calibrated(), Face(blend: [("mouthStretchLeft", 0.6f), ("mouthStretchRight", 0.6f)]));
        Assert.IsGreaterThan(0.9f, i.Morphs[FaceRetarget.I], "横に広げると「い」");

        // 大きく開けると、すぼめる・横に広げるも一緒に大きくなる（スマホ）。口の形は重ねず、合計を 1 までにする
        FaceOutput wide = Settle(Calibrated(), Face(blend: [("jawOpen", 0.95f), ("mouthFunnel", 0.3f), ("mouthStretchLeft", 0.4f), ("mouthStretchRight", 0.4f)]));
        float sum = new[] { FaceRetarget.A, FaceRetarget.I, FaceRetarget.U, FaceRetarget.E, FaceRetarget.O }.Sum(k => wide.Morphs[k]);
        Assert.AreEqual(1f, wide.Morphs[FaceRetarget.A], 1e-3f, "いちばん強い「あ」はそのまま");
        Assert.IsLessThan(1.001f, sum, "あいうえおの合計は 1 まで（重ねると歯や舌が顎を突き抜ける）");

        var some = new Dictionary<string, float> { [FaceRetarget.A] = 0.6f, [FaceRetarget.E] = 0.6f, [FaceRetarget.O] = 0.2f, [FaceRetarget.Smile] = 0.9f };
        FaceRetarget.LimitVowels(some);
        Assert.AreEqual(0.6f, some[FaceRetarget.A], 1e-5f, "同じ強さなら先のものを残す");
        Assert.AreEqual(0.3f, some[FaceRetarget.E], 1e-5f, "残り 0.4 を、0.6 と 0.2 の割合で分ける");
        Assert.AreEqual(0.1f, some[FaceRetarget.O], 1e-5f);
        Assert.AreEqual(0.9f, some[FaceRetarget.Smile], "口角は口の形ではないので変えない");

        FaceOutput smile = Settle(Calibrated(), Face(blend: [("mouthSmileLeft", 0.8f), ("mouthSmileRight", 0.8f)]));
        Assert.IsGreaterThan(0.9f, smile.Morphs[FaceRetarget.Smile]);
    }

    [TestMethod]
    public void BrowsUseTheDifferenceFromTheNeutralFace()
    {
        var retarget = new FaceRetarget(new FaceRetargetSettings());
        retarget.Calibrate(Face(blend: ("browDownLeft", 0.5f)));
        FaceOutput same = Settle(retarget, Face(blend: ("browDownLeft", 0.5f)));
        Assert.AreEqual(0f, same.Morphs[FaceRetarget.BrowAngry], 1e-3f, "正面のときの眉の寄せ方は使わない");
        FaceOutput up = Settle(Calibrated(), Face(blend: ("browInnerUp", 0.9f)));
        Assert.IsGreaterThan(0.9f, up.Morphs[FaceRetarget.BrowUp]);
    }

    [TestMethod]
    public void BodySwaysWithTheFacePosition()
    {
        // 本人が右へ動く → 絵では左へ（x が減る）→ 映像と同じ向き: 画面の左へ傾く。鏡のように: 画面の右へ傾く
        FaceOutput moved = Settle(Calibrated(s => s.UpperBody = false), Face(x: 300));
        Assert.AreEqual(-MathF.Sin(5f * MathF.PI / 180), Vector3.Transform(Up, moved.UpperBody).X, 1e-3f,
            "上半身が画面の左へ傾く（顔の大きさの 0.2 倍動いたら 5°）");
        FaceOutput movedMirror = Settle(Calibrated(s => { s.UpperBody = false; s.Mirror = true; }), Face(x: 300));
        Assert.AreEqual(MathF.Sin(5f * MathF.PI / 180), Vector3.Transform(Up, movedMirror.UpperBody).X, 1e-3f);
        FaceOutput closer = Settle(Calibrated(s => s.UpperBody = false), Face(size: 360));
        Assert.IsLessThan(-0.05f, Vector3.Transform(Up, closer.UpperBody).Z, "近づくと前（カメラの方）へ傾く");
        FaceOutput off = Settle(Calibrated(s => { s.UpperBody = false; s.BodySway = false; }), Face(x: 300, size: 360));
        Assert.AreEqual(Quaternion.Identity, off.UpperBody);
    }

    [TestMethod]
    public void GazeFollowsTheEyes()
    {
        // 本人が自分の右を見る（右目は外、左目は内）
        FaceSignal lookRight = Face(blend: [("eyeLookOutRight", 0.8f), ("eyeLookInLeft", 0.8f)]);
        FaceOutput video = Settle(Calibrated(), lookRight);
        Assert.IsLessThan(-0.2f, Vector3.Transform(Forward, video.Eyes).X, "映像と同じ向き: キャラクターの右（画面の左）を見る");
        FaceOutput mirrored = Settle(Calibrated(s => s.Mirror = true), lookRight);
        Assert.IsGreaterThan(0.2f, Vector3.Transform(Forward, mirrored.Eyes).X, "鏡のように: 画面の右を見る");
        FaceOutput off = Settle(Calibrated(s => s.Gaze = false), lookRight);
        Assert.AreEqual(Quaternion.Identity, off.Eyes);
    }

    [TestMethod]
    public void LosingTheFaceEasesBackToNeutral()
    {
        FaceRetarget retarget = Calibrated();
        Settle(retarget, Face(yaw: -30, blend: ("jawOpen", 0.8f)));
        FaceOutput back = Settle(retarget, null, start: 10);
        Assert.AreEqual(1f, Total(back).W, 1e-3f, "見失ったら正面へ戻る");
        Assert.AreEqual(0f, back.Morphs[FaceRetarget.A], 1e-3f);
    }

    [TestMethod]
    public void OneEuroFilterSmoothsJitterButFollowsMoves()
    {
        var filter = new OneEuroFilter { MinCutoff = 1f };
        var rng = new Random(1);
        float sum = 0;
        for (int i = 0; i < 300; i++)
        {
            float v = filter.Filter(10 + (float)(rng.NextDouble() - 0.5), i / 30.0);
            if (i > 60)
            {
                sum += MathF.Abs(v - 10);
            }
        }
        Assert.IsLessThan(0.12f, sum / 239, "ぶれ（±0.5）を小さくする");
        for (int i = 300; i < 330; i++)
        {
            filter.Filter(20, i / 30.0);
        }
        Assert.AreEqual(20f, filter.Filter(20, 11), 0.5f, "動いたら 1 秒ほどで追いつく");
    }

    [TestMethod]
    public void RigNamesIgnoreWidthAndCase()
    {
        Assert.AreEqual(FaceRigNames.Normalize("ウィンク2右"), FaceRigNames.Normalize("ｳｨﾝｸ２右"));
        string[] morphs = ["真面目", "ｳｨﾝｸ右", "口角上げ", "にっこり"];
        Assert.AreEqual(1, FaceRigNames.Find(morphs, ["ウィンク右"]));
        Assert.AreEqual(2, FaceRigNames.Find(morphs, FaceRigNames.Morphs[FaceRetarget.Smile]), "候補の先のものを使う");
        Assert.AreEqual(-1, FaceRigNames.Find(morphs, ["あ"]));
    }

    // パーフェクトシンクの値を求めて、落ち着かせる
    private static FaceOutput SettlePerfectSync(FaceRetarget retarget, FaceSignal face)
    {
        FaceOutput output = FaceOutput.Neutral;
        for (int i = 0; i <= 90; i++)
        {
            output = retarget.Compute(face, i / 30.0, perfectSync: true);
        }
        return output;
    }

    [TestMethod]
    public void PerfectSyncPassesTheValuesFromTheNeutralFace()
    {
        Assert.HasCount(52, FaceRetarget.PerfectSyncNames);
        Assert.HasCount(52, FaceRetarget.PerfectSyncNames.Distinct());
        FaceSignal face = Face(blend: [("jawOpen", 0.5f), ("mouthSmileLeft", 0.8f), ("browInnerUp", 0.3f), ("eyeLookOutLeft", 0.4f),
            ("eyeBlinkLeft", 0.9f), ("tongueOut", 0.6f)]);

        // 映像と同じ向き: 本人の左 → キャラクターの左。値は正面のときからの違い
        FaceOutput o = SettlePerfectSync(Calibrated(), face);
        Assert.HasCount(52, o.PerfectSync);
        Assert.AreEqual(0.5f, o.PerfectSync["jawOpen"], 0.01f);
        Assert.AreEqual(0.8f, o.PerfectSync["mouthSmileLeft"], 0.01f);
        Assert.AreEqual(0f, o.PerfectSync["mouthSmileRight"], 0.01f);
        Assert.AreEqual(0.4f, o.PerfectSync["eyeLookOutLeft"], 0.01f);
        Assert.AreEqual(0.6f, o.PerfectSync["tongueOut"], 0.01f, "スマホは舌も出す");
        // 目を閉じるのは、まばたきと同じく広げて左右をそろえる（片目だけ大きく閉じればウィンク）
        Assert.AreEqual(1f, o.PerfectSync["eyeBlinkLeft"], 0.01f);
        Assert.AreEqual(0f, o.PerfectSync["eyeBlinkRight"], 0.01f);
        Assert.AreEqual(o.Morphs[FaceRetarget.WinkLeft], o.PerfectSync["eyeBlinkLeft"] - o.Morphs[FaceRetarget.Blink], 0.01f);

        // 鏡のように: 左右を入れ替える
        FaceOutput mirrored = SettlePerfectSync(Calibrated(s => s.Mirror = true), face);
        Assert.AreEqual(0.8f, mirrored.PerfectSync["mouthSmileRight"], 0.01f);
        Assert.AreEqual(0f, mirrored.PerfectSync["mouthSmileLeft"], 0.01f);
        Assert.AreEqual(0.4f, mirrored.PerfectSync["eyeLookOutRight"], 0.01f);
        Assert.AreEqual(1f, mirrored.PerfectSync["eyeBlinkRight"], 0.01f);
        Assert.AreEqual("jawOpen", FaceRetarget.MirrorSide("jawOpen"));

        // 目線を動かさないときは、目線の表情の元も 0
        Assert.AreEqual(0f, SettlePerfectSync(Calibrated(s => s.Gaze = false), face).PerfectSync["eyeLookOutLeft"]);

        // 正面のときに少し口が開いていれば、その分を引く
        var retarget = new FaceRetarget(new FaceRetargetSettings());
        retarget.Calibrate(Face(blend: [("jawOpen", 0.2f)]));
        Assert.AreEqual(0.5f, SettlePerfectSync(retarget, Face(blend: [("jawOpen", 0.6f)])).PerfectSync["jawOpen"], 0.01f);

        // 頼まなければ求めない
        Assert.IsEmpty(Calibrated().Compute(face, 0).PerfectSync);
    }

    [TestMethod]
    public void PerfectSyncNamesAreFoundInModels()
    {
        string?[] names = ["まばたき", "eyeBlinkLeft", "Face.EyeBlink_R", "blendShape1.jawOpen", "mouthSmile_L", "MOUTH_SMILE_RIGHT", "ｔｏｎｇｕｅＯｕｔ",
            "eyeBlinkLeft", null, "jawOpen_2", "Fcl_MTH_A"];
        Dictionary<string, int> found = FaceRigNames.FindPerfectSync(names);
        Assert.HasCount(6, found);
        Assert.AreEqual(1, found["eyeBlinkLeft"], "同じ名前は先のもの");
        Assert.AreEqual(2, found["eyeBlinkRight"], "「.」より前と、_R");
        Assert.AreEqual(3, found["jawOpen"]);
        Assert.AreEqual(4, found["mouthSmileLeft"]);
        Assert.AreEqual(5, found["mouthSmileRight"], "大文字・記号は問わない");
        Assert.AreEqual(6, found["tongueOut"], "全角も");
        Assert.IsGreaterThan(26, FaceRigNames.PerfectSyncMinimum, "少ししか無いモデルは、ふつうの当てはめ方");
    }

    [TestMethod]
    public void PerfectSyncIsOnByDefaultAndSaved()
    {
        var settings = new FaceRetargetSettings();
        Assert.IsTrue(settings.PerfectSync, "持っているモデルなら使う");
        settings.PerfectSync = false;
        var project = new ProjectFile { Live = LiveEntry.From(new LiveOutputSettings(), settings) };
        var report = new Theater3D.Formats.LoadReport();
        ProjectFile read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project), report)!;
        Assert.IsFalse(read.Live!.Tracking!.PerfectSync, "保存して読み戻しても残る");
        var restored = new FaceRetargetSettings();
        read.Live.Tracking.ApplyTo(restored, _ => null);
        Assert.IsFalse(restored.PerfectSync);
        ProjectFile old = ProjectSerializer.Deserialize(
            """{"format":"3DTheater.Project","version":1,"mode":"live","live":{"tracking":{"mirror":true}}}""", report)!;
        Assert.IsTrue(old.Live!.Tracking!.PerfectSync, "前の版のファイルは使う");
        settings.Reset();
        Assert.IsTrue(settings.PerfectSync);
    }

    [TestMethod]
    public void SettingsClampAndNotify()
    {
        var settings = new FaceRetargetSettings();
        int changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.Smoothing = 2f;
        Assert.AreEqual(1f, settings.Smoothing);
        settings.Smoothing = float.NaN;
        Assert.AreEqual(0.5f, settings.Smoothing);
        settings.Mirror = false;
        Assert.AreEqual(2, changes, "変わらなければ知らせない");
    }

    [TestMethod]
    public void GazeTurnsIntoLookExpressions()
    {
        // 両目のボーンが無いモデル（目線を表情で動かす VRM）: 目の回転を、モデルから見た向きの表情の強さにする
        Assert.AreEqual((0f, 0f, 0f, 0f), FaceRetarget.LookWeights(Quaternion.Identity));
        // 画面の右（モデルの +X ＝ モデルの左）を、いちばん大きく見る
        var left = FaceRetarget.LookWeights(FaceRetarget.ModelRotation(FaceRetarget.GazeYawDegrees, 0, 0));
        Assert.AreEqual(1f, left.Left, 1e-4f);
        Assert.AreEqual(0f, left.Right);
        var right = FaceRetarget.LookWeights(FaceRetarget.ModelRotation(-FaceRetarget.GazeYawDegrees / 2, 0, 0));
        Assert.AreEqual(0.5f, right.Right, 1e-3f);
        Assert.AreEqual(0f, right.Left);
        var up = FaceRetarget.LookWeights(FaceRetarget.ModelRotation(0, FaceRetarget.GazePitchDegrees, 0));
        Assert.AreEqual(1f, up.Up, 1e-4f);
        Assert.AreEqual(0f, up.Down);
        var down = FaceRetarget.LookWeights(FaceRetarget.ModelRotation(0, -FaceRetarget.GazePitchDegrees / 2, 0));
        Assert.AreEqual(0.5f, down.Down, 1e-3f);
        var far = FaceRetarget.LookWeights(FaceRetarget.ModelRotation(FaceRetarget.GazeYawDegrees * 3, 0, 0));
        Assert.AreEqual(1f, far.Left, "1 より大きくしない");
        CollectionAssert.AreEqual(new[] { "視線上" }, FaceRigNames.LookUp.ToArray());
        CollectionAssert.AreEqual(new[] { "視線左" }, FaceRigNames.LookLeft.ToArray());
    }
}
