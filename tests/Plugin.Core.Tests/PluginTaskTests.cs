namespace Plugin.Core.Tests;

/// <summary>時間のかかる処理の終わり方と、書き出しの一時ファイル（<see cref="PluginTaskRunner"/>）。</summary>
[TestClass]
public sealed class PluginTaskTests
{
    private string _folder = string.Empty;

    [TestInitialize]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "PluginTaskTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(_folder, recursive: true);

    private static PluginTaskContext Context(CancellationTokenSource? cancellation = null, List<(double?, string?)>? reports = null) =>
        new((cancellation ?? new CancellationTokenSource()).Token, (f, m) => reports?.Add((f, m)));

    [TestMethod]
    public async Task FinishedWorkIsCompleted()
    {
        var reports = new List<(double?, string?)>();
        PluginTaskOutcome outcome = await PluginTaskRunner.RunAsync(async task =>
        {
            await Task.Yield();
            task.Report(0.5, "半分");
        }, Context(reports: reports));

        Assert.AreEqual(PluginTaskResult.Completed, outcome.Result);
        Assert.IsNull(outcome.Error);
        Assert.HasCount(1, reports);
        Assert.AreEqual((0.5, "半分"), reports[0]);
    }

    [TestMethod]
    public async Task CancelledWorkIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        PluginTaskOutcome outcome = await PluginTaskRunner.RunAsync(async task =>
        {
            cancellation.Cancel();
            await Task.Delay(1000, task.Cancellation);
        }, Context(cancellation));

        Assert.AreEqual(PluginTaskResult.Cancelled, outcome.Result);
        Assert.IsNull(outcome.Error);
    }

    [TestMethod]
    public async Task ExceptionsAreFailuresAndAreNotThrown()
    {
        PluginTaskOutcome thrown = await PluginTaskRunner.RunAsync(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("プラグインの誤り");
        }, Context());
        PluginTaskOutcome synchronous = await PluginTaskRunner.RunAsync(_ => throw new FormatException("すぐに失敗"), Context());
        PluginTaskOutcome noTask = await PluginTaskRunner.RunAsync(_ => null!, Context());

        Assert.AreEqual(PluginTaskResult.Failed, thrown.Result);
        Assert.AreEqual("プラグインの誤り", thrown.Error?.Message);
        Assert.AreEqual(PluginTaskResult.Failed, synchronous.Result, "await の前に投げた例外も受け止める");
        Assert.IsInstanceOfType<FormatException>(synchronous.Error);
        Assert.AreEqual(PluginTaskResult.Failed, noTask.Result, "Task を返さない処理");
    }

    [TestMethod]
    public async Task ACancellationExceptionWithoutARequestIsAFailure()
    {
        PluginTaskOutcome outcome = await PluginTaskRunner.RunAsync(_ => throw new OperationCanceledException("頼まれていない取り消し"), Context());

        Assert.AreEqual(PluginTaskResult.Failed, outcome.Result, "利用者が取り消していないのに止まったのはエラー");
    }

    [TestMethod]
    public void ProgressIsKeptBetweenZeroAndOne()
    {
        var reports = new List<(double?, string?)>();
        PluginTaskContext context = Context(reports: reports);

        context.Report(1.5);
        context.Report(-0.2, "前");
        context.Report(double.NaN, "分からない");
        context.Report(null);

        CollectionAssert.AreEqual(new List<(double?, string?)> { (1d, null), (0d, "前"), (null, "分からない"), (null, null) }, reports);
    }

    [TestMethod]
    public async Task ExportReplacesTheFileOnlyWhenWritingSucceeds()
    {
        string path = Path.Combine(_folder, "キー.csv");
        await File.WriteAllTextAsync(path, "元の中身");
        string? written = null;

        await PluginTaskRunner.ExportSafelyAsync(path, async temporary =>
        {
            written = temporary;
            Assert.AreEqual(_folder, Path.GetDirectoryName(temporary), "一時ファイルは同じフォルダー（置き換えが 1 回の移動で済むように）");
            Assert.AreEqual(".csv", Path.GetExtension(temporary), "拡張子は同じ（拡張子で形を決める書き手のため）");
            Assert.IsFalse(File.Exists(temporary));
            await File.WriteAllTextAsync(temporary, "新しい中身");
        });

        Assert.AreEqual("新しい中身", await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(written), "一時ファイルは残らない");
        Assert.HasCount(1, Directory.GetFiles(_folder));
    }

    [TestMethod]
    public async Task AFailedOrCancelledExportKeepsTheOriginalFile()
    {
        string path = Path.Combine(_folder, "キー.csv");
        await File.WriteAllTextAsync(path, "元の中身");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PluginTaskRunner.ExportSafelyAsync(path, async temporary =>
        {
            await File.WriteAllTextAsync(temporary, "書きかけ");
            throw new InvalidOperationException("途中で失敗");
        }));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PluginTaskRunner.ExportSafelyAsync(path, async temporary =>
        {
            await File.WriteAllTextAsync(temporary, "書きかけ");
            throw new OperationCanceledException();
        }));

        Assert.AreEqual("元の中身", await File.ReadAllTextAsync(path), "元のファイルは壊さない");
        Assert.HasCount(1, Directory.GetFiles(_folder), "書きかけの一時ファイルは残さない");
    }

    [TestMethod]
    public async Task AnExportThatWritesNothingIsAnError()
    {
        string path = Path.Combine(_folder, "新しい.csv");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PluginTaskRunner.ExportSafelyAsync(path, _ => Task.CompletedTask));

        StringAssert.Contains(ex.Message, "書き出しませんでした");
        Assert.IsFalse(File.Exists(path));
    }
}
