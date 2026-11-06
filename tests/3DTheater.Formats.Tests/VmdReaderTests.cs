using System.IO;
using System.Numerics;
using System.Text;
using Theater3D.Formats.Vmd;

namespace Theater3D.Formats.Tests;

/// <summary>テスト用に VMD のバイト列を作る（仕様の並びのまま 1 つずつ書く）。</summary>
internal sealed class VmdTestWriter
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _w;

    public VmdTestWriter(string modelName = "テスト", int version = 2)
    {
        _w = new BinaryWriter(_stream);
        byte[] signature = new byte[30];
        Encoding.ASCII.GetBytes(version == 2 ? "Vocaloid Motion Data 0002" : "Vocaloid Motion Data file").CopyTo(signature, 0);
        _w.Write(signature);
        Name(modelName, version == 2 ? 20 : 10);
    }

    public static byte[] DefaultInterpolation()
    {
        // MMD が新しいキーに入れる直線（20, 20, 107, 107）
        var b = new byte[64];
        for (int ch = 0; ch < 4; ch++)
        {
            b[ch] = 20;
            b[ch + 4] = 20;
            b[ch + 8] = 107;
            b[ch + 12] = 107;
        }
        return b;
    }

    public void Name(string name, int bytes) => Field(VmdText.Encode(name, bytes), bytes);

    public void Field(byte[] value, int bytes)
    {
        var field = new byte[bytes];
        value.AsSpan(0, Math.Min(bytes, value.Length)).CopyTo(field);
        if (value.Length < bytes)
        {
            field.AsSpan(value.Length + 1).Fill(0xFD); // MMD は 0 の後ろを 0xFD で埋めることがある
        }
        _w.Write(field);
    }

    public VmdTestWriter Count(int count)
    {
        _w.Write(count);
        return this;
    }

    public VmdTestWriter Bone(string name, int frame, Vector3 t, Quaternion r, byte[]? interpolation = null)
    {
        Name(name, 15);
        _w.Write(frame);
        V3(t);
        _w.Write(r.X);
        _w.Write(r.Y);
        _w.Write(r.Z);
        _w.Write(r.W);
        _w.Write(interpolation ?? DefaultInterpolation());
        return this;
    }

    public VmdTestWriter Morph(string name, int frame, float weight)
    {
        Name(name, 15);
        _w.Write(frame);
        _w.Write(weight);
        return this;
    }

    public VmdTestWriter Camera(int frame, float distance, Vector3 target, Vector3 rotation, int fov)
    {
        _w.Write(frame);
        _w.Write(distance);
        V3(target);
        V3(rotation);
        _w.Write(new byte[24]);
        _w.Write(fov);
        _w.Write((byte)0);
        return this;
    }

    public VmdTestWriter Raw(byte[] bytes)
    {
        _w.Write(bytes);
        return this;
    }

    public byte[] ToArray()
    {
        _w.Flush();
        return _stream.ToArray();
    }

    private void V3(Vector3 v)
    {
        _w.Write(v.X);
        _w.Write(v.Y);
        _w.Write(v.Z);
    }
}

[TestClass]
public sealed class VmdReaderTests
{
    [TestMethod]
    public void ReadsBoneAndMorphKeysOfOldStyleFile()
    {
        // ボーンとモーフだけで終わる古い書き方（カメラ以降の区切りが無い）
        byte[] data = new VmdTestWriter("初音ミク")
            .Count(2)
            .Bone("センター", 0, new Vector3(0, 1, 2), Quaternion.Identity)
            .Bone("左ひじ", 30, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1))
            .Count(1)
            .Morph("まばたき", 15, 0.75f)
            .ToArray();

        VmdLoadResult result = VmdReader.Read(data);

        Assert.IsTrue(result.Succeeded, string.Join("\n", result.Report.Issues.Select(i => i.Message)));
        VmdDocument d = result.Document!;
        Assert.AreEqual(2, d.Version);
        Assert.AreEqual("初音ミク", d.ModelName.Text);
        Assert.HasCount(2, d.BoneKeys);
        Assert.AreEqual("左ひじ", d.BoneKeys[1].Bone.Text);
        Assert.AreEqual(30, d.BoneKeys[1].Frame);
        Assert.AreEqual(new Vector3(0, 1, 2), d.BoneKeys[0].Translation);
        Assert.AreEqual(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1), d.BoneKeys[1].Rotation);
        Assert.AreEqual(((byte)20, (byte)20, (byte)107, (byte)107), d.BoneKeys[0].Curve(3));
        Assert.HasCount(1, d.MorphKeys);
        Assert.AreEqual(0.75f, d.MorphKeys[0].Weight);
        Assert.IsEmpty(d.CameraKeys);
        Assert.AreEqual(30, d.LastFrame);
        Assert.IsFalse(result.Report.Issues.Any(), "古い書き方でも注意は出さない");
    }

    [TestMethod]
    public void ReadsCameraMotion()
    {
        byte[] data = new VmdTestWriter("カメラ・照明")
            .Count(0)
            .Count(0)
            .Count(1)
            .Camera(100, -45, new Vector3(0, 10, 0), new Vector3(0.1f, 0, 0), 30)
            .Count(0) // 照明
            .Count(0) // セルフ影
            .Count(0) // 表示・IK
            .ToArray();

        VmdDocument d = VmdReader.Read(data).Document!;

        Assert.IsTrue(d.IsCameraMotion);
        Assert.AreEqual(-45f, d.CameraKeys[0].Distance);
        Assert.AreEqual(30, d.CameraKeys[0].FieldOfView);
        Assert.AreEqual(100, d.LastFrame);
    }

    [TestMethod]
    public void ReadsVisibilityAndIkKeys()
    {
        var w = new VmdTestWriter().Count(0).Count(0).Count(0).Count(0).Count(0).Count(1);
        w.Raw(BitConverter.GetBytes(10)).Raw([1]).Count(2);
        w.Name("左足ＩＫ", 20);
        w.Raw([0]);
        w.Name("右足ＩＫ", 20);
        w.Raw([1]);

        VmdDocument d = VmdReader.Read(w.ToArray()).Document!;

        VmdVisibilityKey key = d.VisibilityKeys.Single();
        Assert.AreEqual(10, key.Frame);
        Assert.IsTrue(key.Visible);
        Assert.AreEqual("左足ＩＫ", key.IkStates[0].Bone.Text);
        Assert.IsFalse(key.IkStates[0].Enabled);
        Assert.IsTrue(key.IkStates[1].Enabled);
    }

    [TestMethod]
    public void LongNamesAreCutLikeMmdAndStillMatch()
    {
        // 15 バイトを超える名前（全角 8 文字 = 16 バイト）は、MMD と同じく 15 バイトで切れる
        const string longName = "右人差指先先先先";
        byte[] data = new VmdTestWriter().Count(1).Bone(longName, 0, Vector3.Zero, Quaternion.Identity).Count(0).ToArray();

        VmdName name = VmdReader.Read(data).Document!.BoneKeys[0].Bone;

        Assert.AreEqual(15, name.Bytes.Length);
        Assert.AreEqual("右人差指先先先", name.Text, "途中で切れた 2 バイト文字は文字にしない");
        Assert.IsTrue(name.Matches(longName, VmdText.BoneNameBytes), "モデルの長い名前と対応づけられる");
        Assert.IsFalse(name.Matches("右人差指先先先", VmdText.BoneNameBytes));
    }

    [TestMethod]
    public void ReadsVersionOneHeader()
    {
        byte[] data = new VmdTestWriter("古い", version: 1).Count(1).Bone("頭", 5, Vector3.Zero, Quaternion.Identity).ToArray();

        VmdLoadResult result = VmdReader.Read(data);

        Assert.AreEqual(1, result.Document!.Version);
        Assert.AreEqual("古い", result.Document.ModelName.Text);
        Assert.HasCount(1, result.Document.BoneKeys);
    }

    [TestMethod]
    public void TruncatedFileIsReportedWithLocation()
    {
        byte[] full = new VmdTestWriter().Count(2)
            .Bone("センター", 0, Vector3.Zero, Quaternion.Identity)
            .Bone("頭", 10, Vector3.Zero, Quaternion.Identity)
            .ToArray();

        VmdLoadResult result = VmdReader.Read(full[..(full.Length - 30)]);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Report.Issues.Single().Message, "ボーンのキーの数");
    }

    [TestMethod]
    public void BrokenTrailingCountIsAWarningNotAnError()
    {
        byte[] data = new VmdTestWriter().Count(1).Bone("頭", 0, Vector3.Zero, Quaternion.Identity).Count(0).Raw([1, 2]).ToArray();

        VmdLoadResult result = VmdReader.Read(data);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Report.WarningCount);
    }

    [TestMethod]
    public void NonVmdFilesAreRejected()
    {
        Assert.IsFalse(VmdReader.Read(Encoding.ASCII.GetBytes("PMX hello world this is not a motion file")).Succeeded);
        Assert.IsFalse(VmdReader.Read([1, 2, 3]).Succeeded);
        VmdLoadResult pose = VmdReader.Read(Encoding.ASCII.GetBytes("Vocaloid Pose Data file\r\n\r\nmiku.osm;\r\n0;\r\n"));
        StringAssert.Contains(pose.Report.Issues.Single().Message, "VPD");
    }
}

[TestClass]
public sealed class VpdReaderTests
{
    private static byte[] Sjis(string text) => VmdText.ShiftJis.GetBytes(text);

    [TestMethod]
    public void ReadsBonesAndMorphsWithComments()
    {
        string text = "Vocaloid Pose Data file\r\n\r\nmiku.osm;\t\t// 親ファイル名\r\n2;\t\t\t\t// 総ポーズボーン数\r\n\r\n" +
            "Bone0{センター\r\n  0.000000,1.500000,-2.000000;\t\t\t\t// trans x,y,z\r\n  0.000000,0.000000,0.000000,1.000000;\t\t// Quaternion x,y,z,w\r\n}\r\n\r\n" +
            "Bone1{左ひじ\r\n  0,0,0;\r\n  0,0.7071068,0,0.7071068;\r\n}\r\n\r\n" +
            "Morph0{まばたき\r\n  0.500000;\r\n}\r\n";

        VpdLoadResult result = VpdReader.Read(Sjis(text));

        Assert.IsTrue(result.Succeeded);
        VpdDocument d = result.Document!;
        Assert.AreEqual("miku.osm", d.ModelFileName);
        Assert.HasCount(2, d.Bones);
        Assert.AreEqual("センター", d.Bones[0].Name);
        Assert.AreEqual(new Vector3(0, 1.5f, -2), d.Bones[0].Translation);
        Assert.AreEqual(0.7071068f, d.Bones[1].Rotation.Y, 1e-6f);
        Assert.AreEqual("まばたき", d.Morphs.Single().Name);
        Assert.AreEqual(0.5f, d.Morphs[0].Weight);
        Assert.IsFalse(result.Report.Issues.Any());
    }

    [TestMethod]
    public void BadNumbersSkipOnlyThatBone()
    {
        string text = "Vocaloid Pose Data file\nmiku.osm;\n2;\nBone0{頭\n0,0,0;\n0,0,0,1;\n}\nBone1{首\n0,x,0;\n0,0,0,1;\n}\n";

        VpdLoadResult result = VpdReader.Read(Sjis(text));

        Assert.HasCount(1, result.Document!.Bones);
        Assert.AreEqual(1, result.Report.WarningCount);
        StringAssert.Contains(result.Report.Issues[0].Message, "首");
    }

    [TestMethod]
    public void NonVpdTextIsRejected()
    {
        Assert.IsFalse(VpdReader.Read(Sjis("Hello\nBone0{頭\n0,0,0;\n0,0,0,1;\n}")).Succeeded);
    }
}
