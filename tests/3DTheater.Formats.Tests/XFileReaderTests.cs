using System.IO.Compression;
using System.Numerics;
using System.Text;
using Theater3D.Formats.Pmx;
using Theater3D.Formats.Vmd;
using Theater3D.Formats.XFile;

namespace Theater3D.Formats.Tests;

/// <summary>DirectX の .x（MMD のアクセサリ）と .vac の読み込み。データはすべてここで作る（実在のアクセサリは使わない）。</summary>
[TestClass]
public sealed class XFileReaderTests
{
    // 四角（4 隅の面）と三角の 2 枚。材質は中に書いたもの（半透明・テクスチャとスフィア）と、外に名前付きで書いたものの参照
    // （make_xfixture.py で作った。先頭にくり返しのコメントがあり、圧縮版の 2 つ目のブロックは前のブロックの中身を参照する）
    private const string TextBase64 = "eG9mIDAzMDJ0eHQgMDA2NAovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgovLyCDZYNYg2eXcILMgq2C6JXUgrWCzINSg4GDk4NngUKRT4LMg3WDjYNig06CzJKGkGeC8I5Rj8aCs4K5gumCvYLfgsmBQZOvgraVtoLwib2TeILgj5GCrYFCCi8vIINlg1iDZ5dwgsyCrYLoldSCtYLMg1KDgYOTg2eBQpFPgsyDdYONg2KDToLMkoaQZ4LwjlGPxoKzgrmC6YK9gt+CyYFBk6+CtpW2gvCJvZN4guCPkYKtgUIKLy8gg2WDWINnl3CCzIKtguiV1IK1gsyDUoOBg5ODZ4FCkU+CzIN1g42DYoNOgsyShpBngvCOUY/GgrOCuYLpgr2C34LJgUGTr4K2lbaC8Im9k3iC4I+Rgq2BQgp0ZW1wbGF0ZSBNZXNoIHsKIDwzRDgyQUI0NC02MkRBLTExY2YtQUIzOS0wMDIwQUY3MUU0MzM+CiBEV09SRCBuVmVydGljZXM7CiBhcnJheSBWZWN0b3IgdmVydGljZXNbblZlcnRpY2VzXTsKIFsuLi5dCn0KTWF0ZXJpYWwgU2hhcmVkIHsKIDEuMDswLjA7MC4wOzEuMDs7CiA4LjA7CiAwLjE7MC4xOzAuMTs7CiAwLjU7MC4wOzAuMDs7Cn0KTWVzaCBCb3ggewogNDsKIDAuMDswLjA7MC4wOywKIDEuMDswLjA7MC4wOywKIDEuMDsxLjA7MC4wOywKIDAuMDsxLjA7MC4wOzsKIDI7CiA0OzAsMywyLDE7LAogMzswLDIsMTs7CiBNZXNoTWF0ZXJpYWxMaXN0IHsKICAyOwogIDI7CiAgMCwKICAxOzsKICBNYXRlcmlhbCB7CiAgIDAuODswLjg7MC44OzAuNTs7CiAgIDUuMDsKICAgMC4wOzAuMDswLjA7OwogICAwLjQ7MC40OzAuNDs7CiAgIFRleHR1cmVGaWxlbmFtZSB7CiAgICAidGV4LmJtcCplbnYuc3BhIjsKICAgfQogIH0KICB7IFNoYXJlZCB9CiB9CiBNZXNoTm9ybWFscyB7CiAgMTsKICAwLjA7MC4wOy0xLjA7OwogIDI7CiAgNDswLDAsMCwwOywKICAzOzAsMCwwOzsKIH0KIE1lc2hUZXh0dXJlQ29vcmRzIHsKICA0OwogIDAuMDsxLjA7LAogIDEuMDsxLjA7LAogIDEuMDswLjA7LAogIDAuMDswLjA7OwogfQp9Cg==";
    private const string CompressedBase64 = "eG9mIDAzMDJ0emlwMDA2NFENAABlBW8AQ0vj0tdXaE5tjmhOn17QdKZpbdOLqVeatjadaQ5qbmye3Jze6DTRH8grbe5tTmr2azozqW1CetOHvsD+Y02bm3Y2vWza23S/6WSj4+T1Tdumbmv60Ll3ckXTg/6JTWsbnbhGzR41e9TsUbNHutkA3AdWAUNL7ZHfT4MwEMff+1dc9miAtMB0psaEiXtymqiZD8se6lYzEhhLqcvMwv9ury1s/gG+EXJwd/32cz8Y2AN7YA/sgf1/bC2rfSm0hLlstnAicJfkkzibpml4HedZyNj6K8ymyW1IaUyz2Q17TJPknkD+8fKaw24hlS7WsuEEhFLiBxZyrWsFB59f9oqVkSyjKFqRlsxNRVWIEt62QskN1mUR5dQb+kY9MR8CNGK8MxuOex1HFPY9rY/ISO35mRP8xfqQnUN6EZq7MUcGDZIgDhieJyZA1+SxTtf2U9ForGcvuBc1arBC6IdDhSkx4WcbWwWM7WTwp1mfMPW92cS7POpvJWdFKXeiko4JIy2P0We1v5K7Q9TsxchqW+Ls1K3VBK3r/LlWlSgbe53Zfn3V0O/aTYHD2weHt9Ojz3uM7+ahrtXGwdIOxtxG+w13vt80XIzZmt/2Cw==";

    private static byte[] Text => Convert.FromBase64String(TextBase64);

    [TestMethod]
    public void TextAccessoryBecomesAPmxDocument()
    {
        PmxLoadResult result = XFileReader.Read(Text, name: "箱");
        Assert.IsTrue(result.Succeeded, string.Join(" / ", result.Report.Issues.Select(i => i.Message)));
        PmxDocument d = result.Document!;

        Assert.AreEqual("箱", d.Name);
        Assert.HasCount(4, d.Vertices, "位置と法線の組が同じ頂点は 1 つ");
        Assert.HasCount(9, d.Indices, "四角は三角 2 枚に分ける");
        Assert.AreEqual(new Vector3(10, 10, 0), d.Vertices[2].Position, "MMD と同じく 10 倍");
        Assert.AreEqual(new Vector3(0, 0, -1), d.Vertices[0].Normal);
        Assert.AreEqual(new Vector2(1, 0), d.Vertices[2].Uv);
        Assert.IsTrue(d.Vertices.All(v => v.WeightType == PmxWeightType.Bdef1 && v.Bone1 == 0));
        Assert.HasCount(1, d.Bones);

        Assert.HasCount(2, d.Materials);
        PmxMaterial inline = d.Materials[0];
        Assert.AreEqual(6, inline.IndexCount);
        Assert.AreEqual(new Vector4(0.8f, 0.8f, 0.8f, 0.5f), inline.Diffuse);
        Assert.AreEqual(new Vector3(0.4f), inline.Ambient, "自己照明は環境色");
        Assert.IsTrue(inline.Flags.HasFlag(PmxMaterialFlags.DoubleSided), "半透明は両面");
        Assert.IsFalse(inline.Flags.HasFlag(PmxMaterialFlags.Edge), "輪郭線なし");
        Assert.AreEqual("tex.bmp", d.Textures[inline.TextureIndex]);
        Assert.AreEqual("env.spa", d.Textures[inline.SphereTextureIndex]);
        Assert.AreEqual(PmxSphereMode.Add, inline.SphereMode);
        Assert.AreEqual(-1, inline.ToonIndex, "トゥーンなし");

        PmxMaterial shared = d.Materials[1];
        Assert.AreEqual("Shared", shared.Name, "名前付きの材質を参照で使う");
        Assert.AreEqual(3, shared.IndexCount);
        Assert.AreEqual(8f, shared.Specularity);
        Assert.AreEqual(-1, shared.TextureIndex);
        Assert.IsFalse(shared.Flags.HasFlag(PmxMaterialFlags.DoubleSided));
        Assert.IsTrue(result.Report.Issues.Any(i => i.Message.Contains("10 倍")), "10 倍にしたことを知らせる");
    }

    [TestMethod]
    public void CompressedBlocksMayReferThePreviousBlock()
    {
        byte[] compressed = Convert.FromBase64String(CompressedBase64);
        Assert.AreEqual("tzip", Encoding.ASCII.GetString(compressed, 8, 4));
        PmxLoadResult result = XFileReader.Read(compressed);
        Assert.IsTrue(result.Succeeded, string.Join(" / ", result.Report.Issues.Select(i => i.Message)));
        AssertSameShape(XFileReader.Read(Text).Document!, result.Document!);
        Assert.AreEqual("圧縮したテキスト", XFileReader.FormatName(compressed));
    }

    [TestMethod]
    public void CompressedFilesWrittenBlockByBlockAreRead()
    {
        // .NET の Deflate でブロックごとに縮めたもの（前のブロックを参照しない）
        byte[] text = Text;
        byte[] body = text[16..];
        var output = new MemoryStream();
        output.Write("xof 0302tzip0032"u8);
        output.Write(BitConverter.GetBytes(text.Length));
        for (int start = 0; start < body.Length; start += 1000)
        {
            byte[] plain = body[start..Math.Min(body.Length, start + 1000)];
            var packed = new MemoryStream();
            using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(plain);
            }
            output.Write(BitConverter.GetBytes((ushort)plain.Length));
            output.Write(BitConverter.GetBytes((ushort)(packed.Length + 2)));
            output.Write("CK"u8);
            output.Write(packed.ToArray());
        }
        PmxLoadResult result = XFileReader.Read(output.ToArray());
        Assert.IsTrue(result.Succeeded, string.Join(" / ", result.Report.Issues.Select(i => i.Message)));
        AssertSameShape(XFileReader.Read(text).Document!, result.Document!);
    }

    [TestMethod]
    public void BinaryAccessoryIsReadLikeText()
    {
        var b = new XBinary("xof 0302bin 0032");
        b.Template("Mesh"); // テンプレートは読み飛ばす
        b.Name("Mesh"); b.Name("Box"); b.Open();
        b.Ints(4);
        b.Floats(0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0);
        b.Ints(2, 4, 0, 3, 2, 1, 3, 0, 2, 1);
        b.Name("MeshMaterialList"); b.Open();
        b.Ints(2, 2, 0, 1);
        b.Name("Material"); b.Open();
        b.Floats(0.8f, 0.8f, 0.8f, 0.5f, 5, 0, 0, 0, 0.4f, 0.4f, 0.4f);
        b.Name("TextureFilename"); b.Open(); b.String("tex.bmp*env.spa"); b.Close();
        b.Close();
        b.Name("Material"); b.Open(); b.Floats(1, 0, 0, 1, 8, 0.1f, 0.1f, 0.1f, 0.5f, 0, 0); b.Close();
        b.Close();
        b.Name("MeshNormals"); b.Open(); b.Ints(1); b.Floats(0, 0, -1); b.Ints(2, 4, 0, 0, 0, 0, 3, 0, 0, 0); b.Close();
        b.Name("MeshTextureCoords"); b.Open(); b.Ints(4); b.Floats(0, 1, 1, 1, 1, 0, 0, 0); b.Close();
        b.Close();

        PmxLoadResult result = XFileReader.Read(b.ToArray());
        Assert.IsTrue(result.Succeeded, string.Join(" / ", result.Report.Issues.Select(i => i.Message)));
        AssertSameShape(XFileReader.Read(Text).Document!, result.Document!);
        Assert.AreEqual("バイナリ", XFileReader.FormatName(b.ToArray()));
    }

    [TestMethod]
    public void FramesMoveTheirMeshes()
    {
        const string text = """
            xof 0302txt 0032
            Frame Root {
             FrameTransformMatrix { 1,0,0,0, 0,1,0,0, 0,0,1,0, 2,0,0,1;; }
             Mesh {
              3;
              0;0;0;, 1;0;0;, 0;1;0;;
              1;
              3;0,1,2;;
             }
            }
            """;
        PmxLoadResult result = XFileReader.Read(Encoding.ASCII.GetBytes(text));
        Assert.IsTrue(result.Succeeded, string.Join(" / ", result.Report.Issues.Select(i => i.Message)));
        PmxDocument d = result.Document!;
        Assert.AreEqual(new Vector3(20, 0, 0), d.Vertices[0].Position, "骨組みの行列（x に 2）を掛けてから 10 倍");
        Assert.HasCount(1, d.Materials, "材質の一覧が無ければ白い材質 1 つ");
        Vector3 n = d.Vertices[0].Normal;
        Assert.AreEqual(1f, n.Z, 1e-5f, "法線が無ければ面の向きから求める（時計回りが表。この三角は +Z の側から見て時計回り）");
    }

    [TestMethod]
    public void BrokenFilesAreReported()
    {
        PmxLoadResult notX = XFileReader.Read(Encoding.ASCII.GetBytes("PMX 2.0 ではない"));
        Assert.IsFalse(notX.Succeeded);
        StringAssert.Contains(notX.Report.Issues[0].Message, ".x ではない");

        const string outOfRange = "xof 0302txt 0032\nMesh { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,7;; }";
        PmxLoadResult bad = XFileReader.Read(Encoding.ASCII.GetBytes(outOfRange));
        Assert.IsFalse(bad.Succeeded);
        StringAssert.Contains(bad.Report.Issues[^1].Message, "範囲の外");

        var cut = new XBinary("xof 0302bin 0032");
        cut.Name("Mesh"); cut.Open(); cut.Ints(3);
        byte[] truncated = cut.ToArray()[..^2];
        Assert.IsFalse(XFileReader.Read(truncated).Succeeded, "途中で切れたバイナリ");

        Assert.IsFalse(XFileReader.Read(Encoding.ASCII.GetBytes("xof 0302txt 0032\nHeader { 1; 0; 1; }")).Succeeded, "形が無い");
    }

    [TestMethod]
    public void TextureNamesSplitIntoTextureAndSphere()
    {
        Assert.AreEqual(("a.bmp", "b.sph", PmxSphereMode.Multiply), XFileConverter.SplitTexture("a.bmp*b.sph"));
        Assert.AreEqual(((string?)null, "b.SPA", PmxSphereMode.Add), XFileConverter.SplitTexture("b.SPA"));
        Assert.AreEqual(("tex\\a.png", (string?)null, PmxSphereMode.None), XFileConverter.SplitTexture("tex\\a.png"));
        Assert.AreEqual(((string?)null, (string?)null, PmxSphereMode.None), XFileConverter.SplitTexture(""));
    }

    [TestMethod]
    public void VacTellsWhereToAttach()
    {
        string vac = "ネギ(右手)\r\nnegi.x\r\n1.0\r\n-0.5,-1.0,0.00\r\n0.0,0.0,3.14\r\n右手\r\n\r\n\r\n// MikuMikuDance用アクセサリファイル\r\n";
        VacFile v = VacFile.Parse(VmdText.ShiftJis.GetBytes(vac));
        Assert.AreEqual("ネギ(右手)", v.Name);
        Assert.AreEqual("negi.x", v.XFile);
        Assert.AreEqual(1f, v.Scale);
        Assert.AreEqual(new Vector3(-0.5f, -1, 0), v.Position);
        Assert.AreEqual(179.9087f, v.RotationDegrees.Z, 1e-3f, "回転はラジアンで書く");
        Assert.AreEqual("右手", v.BoneName);

        VacFile loose = VacFile.Parse(VmdText.ShiftJis.GetBytes("床\nfloor.x\n2\n0,0,0\n0,0,0\n\n"));
        Assert.IsNull(loose.BoneName, "ボーンが空なら付けない");
        Assert.AreEqual(2f, loose.Scale);
        Assert.ThrowsExactly<ModelFormatException>(() => VacFile.Parse(VmdText.ShiftJis.GetBytes("名前だけ\n")));
    }

    [TestMethod]
    public void ConvertedDocumentsPassTheValidator()
    {
        PmxLoadResult result = XFileReader.Read(Text);
        Assert.IsFalse(result.Report.Issues.Any(i => i.Severity >= LoadSeverity.Warning),
            "テクスチャを探さないとき、注意は出ない: " + string.Join(" / ", result.Report.Issues.Select(i => i.Message)));
    }

    private static void AssertSameShape(PmxDocument expected, PmxDocument actual)
    {
        CollectionAssert.AreEqual(expected.Indices, actual.Indices);
        CollectionAssert.AreEqual(expected.Vertices.Select(v => v.Position).ToArray(), actual.Vertices.Select(v => v.Position).ToArray());
        CollectionAssert.AreEqual(expected.Vertices.Select(v => v.Uv).ToArray(), actual.Vertices.Select(v => v.Uv).ToArray());
        CollectionAssert.AreEqual(expected.Materials.Select(m => (m.Diffuse, m.IndexCount, m.TextureIndex, m.SphereMode)).ToArray(),
            actual.Materials.Select(m => (m.Diffuse, m.IndexCount, m.TextureIndex, m.SphereMode)).ToArray());
        CollectionAssert.AreEqual(expected.Textures, actual.Textures);
    }

    /// <summary>バイナリの .x を書く（テスト用）。</summary>
    private sealed class XBinary
    {
        private readonly MemoryStream _data = new();

        public XBinary(string header) => _data.Write(Encoding.ASCII.GetBytes(header));

        public void Name(string name)
        {
            byte[] bytes = VmdText.ShiftJis.GetBytes(name);
            Word(1);
            Dword((uint)bytes.Length);
            _data.Write(bytes);
        }

        public void String(string text)
        {
            byte[] bytes = VmdText.ShiftJis.GetBytes(text);
            Word(2);
            Dword((uint)bytes.Length);
            _data.Write(bytes);
            Word(20); // ;
        }

        public void Ints(params int[] values)
        {
            Word(6);
            Dword((uint)values.Length);
            foreach (int v in values)
            {
                Dword((uint)v);
            }
        }

        public void Floats(params float[] values)
        {
            Word(7);
            Dword((uint)values.Length);
            foreach (float v in values)
            {
                _data.Write(BitConverter.GetBytes(v));
            }
        }

        public void Template(string name)
        {
            Word(31);
            Name(name);
            Open();
            Word(5);
            _data.Write(new byte[16]); // GUID
            Word(40); // WORD 型
            Name("nVertices");
            Word(20);
            Close();
        }

        public void Open() => Word(10);

        public void Close() => Word(11);

        public byte[] ToArray() => _data.ToArray();

        private void Word(int value) => _data.Write(BitConverter.GetBytes((ushort)value));

        private void Dword(uint value) => _data.Write(BitConverter.GetBytes(value));
    }
}
