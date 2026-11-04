using Theater3D.Domain;

namespace Theater3D.Editor.Tests;

/// <summary>プレビュー・シーン一覧・タイムラインで共有する選択（指示書 2.7）。</summary>
[TestClass]
public sealed class SelectionServiceTests
{
    private readonly SceneNode _a = new(SceneNodeKind.TestObject, "A");
    private readonly SceneNode _b = new(SceneNodeKind.TestObject, "B");
    private readonly SceneNode _c = new(SceneNodeKind.TestObject, "C");

    [TestMethod]
    public void ReplaceAddAndToggle()
    {
        var selection = new SelectionService();
        selection.Select(_a);
        selection.Select(_b, SelectMode.Add);
        CollectionAssert.AreEqual(new[] { _a, _b }, selection.Items.ToArray());
        Assert.AreSame(_b, selection.Primary, "最後に選んだものが主な選択");

        selection.Select(_a, SelectMode.Toggle);
        CollectionAssert.AreEqual(new[] { _b }, selection.Items.ToArray());

        selection.Select(_c);
        CollectionAssert.AreEqual(new[] { _c }, selection.Items.ToArray());

        selection.Clear();
        Assert.IsNull(selection.Primary);
    }

    [TestMethod]
    public void NoNotificationWhenNothingChanges()
    {
        var selection = new SelectionService();
        selection.Select(_a);
        int count = 0;
        selection.Changed += (_, _) => count++;
        selection.Select(_a);
        selection.SelectMany([_a]);
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public void MutualSyncDoesNotLoop()
    {
        // 2 つの画面が、届いた選択を自分の表示に反映して選択し直しても、通知は往復し続けない
        var selection = new SelectionService();
        object treeView = new();
        object viewport = new();
        int notifications = 0;
        selection.Changed += (_, e) =>
        {
            notifications++;
            if (e.Source != treeView)
            {
                selection.SelectMany(selection.Items.ToList(), treeView);
            }
        };
        selection.Changed += (_, e) =>
        {
            if (e.Source != viewport)
            {
                selection.SelectMany(selection.Items.ToList(), viewport);
            }
        };

        selection.Select(_b, SelectMode.Replace, viewport);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public void SourceIsReported()
    {
        var selection = new SelectionService();
        object? reported = null;
        selection.Changed += (_, e) => reported = e.Source;
        object panel = new();
        selection.Select(_a, SelectMode.Replace, panel);
        Assert.AreSame(panel, reported);
    }
}
