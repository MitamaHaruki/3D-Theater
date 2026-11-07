using System.Numerics;
using Theater3D.Formats.Vmd;

namespace Theater3D.Formats.Tests;

[TestClass]
public sealed class VmdWriterTests
{
    private static VmdName N(string text, int bytes = VmdText.BoneNameBytes) => VmdName.FromText(text, bytes);

    [TestMethod]
    public void WrittenVmdReadsBackTheSame()
    {
        byte[] ip = VmdWriter.Interpolation((10, 20, 30, 40), (20, 20, 107, 107), (1, 2, 3, 4), (64, 0, 64, 127));
        var document = new VmdDocument
        {
            ModelName = N("初音ミク", VmdText.ModelNameBytes),
            BoneKeys =
            [
                new VmdBoneKey(N("センター"), 0, new Vector3(1, 2, 3), Quaternion.Identity, ip),
                new VmdBoneKey(N("右人差指先先先先"), 15, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), ip),
            ],
            MorphKeys = [new VmdMorphKey(N("まばたき"), 5, 0.75f)],
            CameraKeys = [new VmdCameraKey(0, -45, new Vector3(0, 10, 0), new Vector3(0.1f, 0.2f, 0), new byte[24], 30, false)],
            LightKeys = [new VmdLightKey(0, new Vector3(0.6f), new Vector3(-0.5f, -1, 0.5f))],
            ShadowKeys = [new VmdShadowKey(0, 1, 0.01f)],
            VisibilityKeys = [new VmdVisibilityKey(0, true, [new VmdIkState(N("左足ＩＫ", VmdText.IkNameBytes), false)])],
        };

        VmdLoadResult result = VmdReader.Read(VmdWriter.Write(document));

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Report.Issues.Any());
        VmdDocument d = result.Document!;
        Assert.AreEqual("初音ミク", d.ModelName.Text);
        Assert.HasCount(2, d.BoneKeys);
        Assert.AreEqual(new Vector3(1, 2, 3), d.BoneKeys[0].Translation);
        Assert.AreEqual(document.BoneKeys[1].Rotation, d.BoneKeys[1].Rotation);
        Assert.IsTrue(d.BoneKeys[1].Bone.Matches("右人差指先先先先", VmdText.BoneNameBytes), "長い名前は 15 バイトで切れて書かれる");
        Assert.AreEqual(((byte)10, (byte)20, (byte)30, (byte)40), d.BoneKeys[0].Curve(0));
        Assert.AreEqual(((byte)64, (byte)0, (byte)64, (byte)127), d.BoneKeys[0].Curve(3));
        Assert.AreEqual(0.75f, d.MorphKeys[0].Weight);
        Assert.AreEqual(-45f, d.CameraKeys[0].Distance);
        Assert.AreEqual(0.01f, d.ShadowKeys[0].Distance);
        Assert.IsFalse(d.VisibilityKeys[0].IkStates[0].Enabled);
    }

    [TestMethod]
    public void InterpolationRowsAreShiftedLikeMmd()
    {
        byte[] ip = VmdWriter.Interpolation((1, 2, 3, 4), (5, 6, 7, 8), (9, 10, 11, 12), (13, 14, 15, 16));

        Assert.HasCount(64, ip);
        CollectionAssert.AreEqual(new byte[] { 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 4, 8, 12, 16 }, ip[..16]);
        Assert.AreEqual(ip[1], ip[16], "2 行目は 1 バイトずれた写し");
        Assert.AreEqual(ip[3], ip[48]);
        Assert.AreEqual(0, ip[63]);
        Assert.AreEqual((byte)127, VmdWriter.Interpolation((200, 200, 200, 200), (0, 0, 0, 0), (0, 0, 0, 0), (0, 0, 0, 0))[0], "127 を超える値は 127 にする");
    }

    [TestMethod]
    public void WrittenVpdReadsBackTheSame()
    {
        var pose = new VpdDocument
        {
            ModelFileName = "動く人形.osm",
            Bones = [new VpdBone("右腕", new Vector3(0, 0.5f, 0), Quaternion.Normalize(new Quaternion(0, 0, 0.422618f, 0.906308f)))],
            Morphs = [new VpdMorph("にっこり", 1)],
        };

        VpdLoadResult result = VpdReader.Read(VpdWriter.Write(pose));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("動く人形.osm", result.Document!.ModelFileName);
        Assert.AreEqual("右腕", result.Document.Bones.Single().Name);
        Assert.AreEqual(0.5f, result.Document.Bones[0].Translation.Y, 1e-6f);
        Assert.AreEqual(0.422618f, result.Document.Bones[0].Rotation.Z, 1e-5f);
        Assert.AreEqual(1f, result.Document.Morphs.Single().Weight);
    }
}
