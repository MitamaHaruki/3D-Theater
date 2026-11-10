using Theater3D.Formats.Images;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Tests;

[TestClass]
public sealed class PmxToonTests
{
    [TestMethod]
    public void SharedToonFileNamesAreRecognized()
    {
        Assert.AreEqual("toon01.bmp", PmxToon.SharedFileName(0));
        Assert.AreEqual("toon10.bmp", PmxToon.SharedFileName(9));
        Assert.AreEqual(0, PmxToon.SharedIndexOfFileName("toon01.bmp"));
        Assert.AreEqual(9, PmxToon.SharedIndexOfFileName(@"Data\TOON10.BMP"), "フォルダ付き・大文字でもよい");
        Assert.AreEqual(-1, PmxToon.SharedIndexOfFileName("toon00.bmp"));
        Assert.AreEqual(-1, PmxToon.SharedIndexOfFileName("toon11.bmp"));
        Assert.AreEqual(-1, PmxToon.SharedIndexOfFileName("toon-1.bmp"));
        Assert.AreEqual(-1, PmxToon.SharedIndexOfFileName("mytoon01.bmp"));
    }

    [TestMethod]
    public void ToonIsResolvedLikeMmd()
    {
        string[] textures = ["toon/skin.bmp", "toon03.bmp"];
        bool Exists(int index) => index == 0;

        Assert.AreEqual(ToonReference.Shared(2), PmxToon.Resolve(new PmxMaterial { SharedToon = true, ToonIndex = 2 }, textures, Exists));
        Assert.IsTrue(PmxToon.Resolve(new PmxMaterial { SharedToon = true, ToonIndex = 10 }, textures, Exists).IsNone, "共有トゥーンは 0〜9");
        Assert.AreEqual(ToonReference.Texture(0), PmxToon.Resolve(new PmxMaterial { ToonIndex = 0 }, textures, Exists));
        Assert.AreEqual(ToonReference.Shared(2), PmxToon.Resolve(new PmxMaterial { ToonIndex = 1 }, textures, Exists),
            "モデルの横に無い toon03.bmp は、MMD 付属の共有トゥーンを指している");
        Assert.AreEqual(ToonReference.Texture(1), PmxToon.Resolve(new PmxMaterial { ToonIndex = 1 }, textures, _ => true),
            "モデルの横にあればそれを使う");
        Assert.IsTrue(PmxToon.Resolve(new PmxMaterial { ToonIndex = -1 }, textures, Exists).IsNone);
        Assert.IsTrue(PmxToon.Resolve(new PmxMaterial { ToonIndex = 5 }, textures, Exists).IsNone, "範囲外の番号");
    }

    [TestMethod]
    public void DefaultToonsAreWhiteOnTopAndShadowBelow()
    {
        for (int i = 0; i < PmxToon.SharedCount; i++)
        {
            DecodedImage image = DefaultToons.Create(i);
            Assert.HasCount(DefaultToons.Width * DefaultToons.Height * 4, image.Bgra);
            byte[] top = image.Bgra[..4];
            byte[] bottom = image.Bgra[^4..];
            CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, top, $"toon{i + 1:00} の上は白");
            System.Numerics.Vector3 shadow = DefaultToons.ShadowColor(i) * 255f;
            Assert.AreEqual((int)MathF.Round(shadow.X), bottom[2], $"toon{i + 1:00} の下は影の色（赤）");
            Assert.AreEqual((int)MathF.Round(shadow.Z), bottom[0], $"toon{i + 1:00} の下は影の色（青）");
            Assert.IsLessThan(255, (bottom[0] + bottom[1] + bottom[2]) / 3, "影の色は白より暗い");
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DefaultToons.Create(10));
    }
}
