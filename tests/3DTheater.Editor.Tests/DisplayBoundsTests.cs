using System.Numerics;

namespace Theater3D.Editor.Tests;

[TestClass]
public sealed class DisplayBoundsTests
{
    // 体（幅 2・高さ 20・奥行き 2 の範囲に散らばる点）と、そのほかの点
    private static (Vector3[] Positions, uint[] Indices) Model(IEnumerable<Vector3> extra)
    {
        var random = new Random(5);
        var points = new List<Vector3>();
        for (int i = 0; i < 1200; i++)
        {
            points.Add(new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 20, (float)random.NextDouble() * 2 - 1));
        }
        points.AddRange(extra);
        uint[] indices = [.. Enumerable.Range(0, points.Count - (points.Count % 3)).Select(i => (uint)i)];
        return ([.. points], indices);
    }

    [TestMethod]
    public void HiddenPartsFarAwayAreLeftOut()
    {
        // 眼鏡を足元のずっと下に隠しておくモデル
        var (positions, indices) = Model(Enumerable.Range(0, 60).Select(i => new Vector3(i * 0.01f, -983, 0)));

        bool[] core = DisplayBounds.CoreVertices(positions, indices);
        var (min, max) = DisplayBounds.Compute(positions, core);

        Assert.IsGreaterThan(-0.5f, min.Y, "隠した部品は箱に入れない");
        Assert.IsGreaterThan(19f, max.Y);
        Assert.IsLessThan(-0.9f, min.X);
        Assert.IsFalse(core[^1]);
    }

    [TestMethod]
    public void LongPartsThatBelongToTheModelStayIn()
    {
        // 体から横に伸びる長い物（剣など）は、ほかから離れていないので残す
        var (positions, indices) = Model(Enumerable.Range(0, 90).Select(i => new Vector3(i * 0.25f, 10, 0)));

        var (_, max) = DisplayBounds.Compute(positions, DisplayBounds.CoreVertices(positions, indices));

        Assert.IsGreaterThan(22f, max.X);
    }

    [TestMethod]
    public void UnusedVerticesAreLeftOutButFacelessModelsUseAll()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new Vector3(500, 0, 0)];

        bool[] core = DisplayBounds.CoreVertices(positions, [0, 1, 2]);
        Assert.IsFalse(core[3], "面に使われていない頂点は入れない");
        Assert.AreEqual(new Vector3(1, 1, 0), DisplayBounds.Compute(positions, core).Max);

        bool[] all = DisplayBounds.CoreVertices(positions, []);
        Assert.IsTrue(all.All(c => c));
    }

    [TestMethod]
    public void EmptyOrUnmarkedInputFallsBackToAllVertices()
    {
        Assert.AreEqual((Vector3.Zero, Vector3.Zero), DisplayBounds.Compute([], []));
        Vector3[] positions = [new Vector3(-1, 0, 0), new Vector3(2, 3, 4)];
        Assert.AreEqual((new Vector3(-1, 0, 0), new Vector3(2, 3, 4)), DisplayBounds.Compute(positions, [false, false]));
    }
}
