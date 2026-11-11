using System.Globalization;
using System.IO;
using Plugin.Core;
using Plugin.UI.Wpf;
using Theater3D.Plugin.Sdk;

namespace Theater3D.SamplePlugin;

/// <summary>
/// サンプルのプラグイン。共通の契約（コマンド・設定）・画面の拡張（パネル・メッセージ）・3D Theater の SDK（シーンを読む・編集する）を一通り使う。
/// </summary>
public sealed class SceneInfoPlugin : IPlugin
{
    private const string RunCountKey = "runCount";

    /// <summary>まばたきのモーフの名前（MMD のモデルでよく使う名前）。</summary>
    internal const string BlinkMorph = "まばたき";

    private IPluginContext? _context;
    private SceneInfoView? _view;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        ITheaterScene scene = context.GetService<ITheaterScene>()
            ?? throw new InvalidOperationException("このアプリにはシーンの SDK がありません");
        IPluginUi? ui = context.GetService<IPluginUi>();

        context.Commands.Register(new PluginCommand
        {
            Id = "show-frame",
            Title = "今のフレームを知らせる",
            Shortcut = "Ctrl+Alt+I",
            Description = "今のフレーム・フレームの速さ・モデルの数を知らせます（使った回数を、プラグインの設定に覚えます）",
            Execute = () =>
            {
                int count = IncrementRunCount(context);
                ui?.ShowMessage(
                    $"今のフレームは {scene.CurrentFrame} / {scene.EndFrame}（{scene.FrameRate} fps）です。\n" +
                    $"モデルは {scene.Models.Count} 体、選んでいるのは「{scene.SelectedName ?? "なし"}」です。\n\n" +
                    $"（このコマンドを使った回数: {count} 回）");
                _view?.Refresh();
            },
        });
        ITheaterEditor? editor = context.GetService<ITheaterEditor>();
        context.Commands.Register(new PluginCommand
        {
            Id = "insert-blink",
            Title = "まばたきを入れる",
            Description = "選んだモデルの「まばたき」に、今のフレームから閉じて開くキー（0 → 3 フレーム後に 1 → 6 フレーム後に 0）を打ちます。1 回の「元に戻す」で消せます",
            Execute = () => InsertBlink(scene, editor, ui),
            CanExecute = () => editor is not null && scene.SelectedModelId is not null,
        });
        context.Commands.Register(new PluginCommand
        {
            Id = "go-to-start",
            Title = "先頭のフレームへ",
            Description = "再生位置を 0 フレーム目へ動かします",
            Execute = () => scene.SeekFrame(0),
            CanExecute = () => scene.CurrentFrame != 0,
        });

        context.GetService<IPanelRegistry>()?.Register(new PluginPanel
        {
            Id = "scene-info",
            Title = "シーンの情報（サンプル）",
            Placement = PanelPlacement.Right,
            CreateContent = () => _view = new SceneInfoView(scene, () => RunCount(context)),
        });

        // ツールバーのボタンと右クリックのメニュー（どちらも登録したコマンドを実行する。SDK 1.2）
        context.GetService<IToolbarRegistry>()?.Register(new PluginToolbarButton { CommandId = "insert-blink", Glyph = "\uE890" }); // 目の絵（Segoe Fluent Icons）
        IContextMenuRegistry? menus = context.GetService<IContextMenuRegistry>();
        menus?.Register(new PluginContextMenuItem { CommandId = "insert-blink", Place = TheaterMenuPlaces.Preview });
        menus?.Register(new PluginContextMenuItem { CommandId = "insert-blink", Place = TheaterMenuPlaces.SceneTree });

        // 状態表示: 今のフレームを時間で（再生位置が変わるたびに出し直す）
        if (context.GetService<IStatusBarRegistry>()?.Add("time") is { } time)
        {
            time.ToolTip = "今のフレームを時間で表したもの（サンプルのプラグイン）";
            void ShowTime() => time.Text = FormatTime(scene.CurrentFrame, scene.FrameRate);
            scene.FrameChanged += (_, _) => ShowTime();
            ShowTime();
        }

        // 時間のかかる処理（進み具合と取り消し）と、ファイルの形式（SDK 1.3）
        IPluginTasks? tasks = context.GetService<IPluginTasks>();
        context.Commands.Register(new PluginCommand
        {
            Id = "count-demo",
            Title = "時間のかかる処理の見本（5 秒）",
            Description = "0.1 秒ずつ 50 まで数えます。進み具合の窓の「取り消す」で途中でやめられます",
            Execute = () => _ = tasks?.RunAsync("数えています（見本）", async task =>
            {
                for (int i = 1; i <= 50; i++)
                {
                    await Task.Delay(100, task.Cancellation);
                    task.Report(i / 50.0, $"{i} / 50");
                }
            }),
            CanExecute = () => tasks is not null,
        });
        context.GetService<IFileFormatRegistry>()?.Register(new PluginFileFormat
        {
            Id = "key-csv",
            Title = "キーの CSV（サンプル）",
            Extensions = [".csv"],
            CanImport = () => editor is not null && scene.SelectedModelId is not null,
            CanExport = () => scene.SelectedModelId is not null,
            SuggestExportName = () => scene.Models.FirstOrDefault(m => m.Id == scene.SelectedModelId)?.Name + "_キー",
            Export = async (path, task) =>
            {
                string modelId = scene.SelectedModelId ?? throw new InvalidOperationException("モデルを選んでください");
                task.Report(null, "キーを集めています");
                List<KeyCsv.Row> rows = KeyCsv.Collect(scene, modelId); // SDK は画面のスレッドで使う
                await Task.Run(() => KeyCsv.Write(path, rows, task), task.Cancellation); // ファイルは別のスレッドで書く
                context.Log.Info($"キーを {rows.Count} 個書き出しました");
            },
            Import = async (path, task) =>
            {
                string modelId = scene.SelectedModelId ?? throw new InvalidOperationException("モデルを選んでください");
                List<KeyCsv.Row> rows = await Task.Run(() => KeyCsv.Read(path, task), task.Cancellation);
                task.Report(null, "キーを当てはめています"); // await の後は画面のスレッドに戻っている
                int applied = 0;
                editor!.Edit($"キーの CSV の読み込み（{Path.GetFileName(path)}）", edit =>
                {
                    foreach (KeyCsv.Row row in rows)
                    {
                        bool ok = row.Bone is { } bone ? edit.SetBoneKey(modelId, row.Name, bone) : edit.SetMorphKey(modelId, row.Name, row.Morph!.Value);
                        applied += ok ? 1 : 0;
                    }
                });
                context.Log.Info($"キーを {applied} 個読み込みました（全部で {rows.Count} 個）");
                if (applied < rows.Count)
                {
                    ui?.ShowMessage($"キーを {applied} 個読み込みました。{rows.Count - applied} 個は、モデルに同じ名前のボーン・モーフが無いか、ロックしているため読み込めませんでした。");
                }
            },
        });
        context.Log.Info("始めました");
    }

    public void Shutdown()
    {
        _view?.Detach();
        _view = null;
        _context?.Log.Info("止めました");
    }

    // 3 つのキーを 1 つの編集にまとめる（アプリの「元に戻す」1 回で 3 つとも消える）
    private static void InsertBlink(ITheaterScene scene, ITheaterEditor? editor, IPluginUi? ui)
    {
        if (editor is null || scene.SelectedModelId is not { } modelId)
        {
            return;
        }
        if (!scene.GetMorphNames(modelId).Contains(BlinkMorph))
        {
            ui?.ShowMessage($"選んだモデルに「{BlinkMorph}」のモーフがありません。");
            return;
        }
        int frame = scene.CurrentFrame;
        bool changed = editor.Edit("まばたきを入れる", edit =>
        {
            edit.SetMorphKey(modelId, BlinkMorph, new MorphKeyInfo(frame, 0f));
            edit.SetMorphKey(modelId, BlinkMorph, new MorphKeyInfo(frame + 3, 1f));
            edit.SetMorphKey(modelId, BlinkMorph, new MorphKeyInfo(frame + 6, 0f));
        });
        if (!changed)
        {
            ui?.ShowMessage($"キーは変わりませんでした（「{BlinkMorph}」をロックしているか、同じキーがすでにあります）。");
        }
    }

    /// <summary>フレームを「時間 分:秒.百分の一秒」にする。</summary>
    internal static string FormatTime(int frame, int frameRate)
    {
        double seconds = frame / (double)Math.Max(1, frameRate);
        int minutes = (int)(seconds / 60);
        return string.Create(CultureInfo.InvariantCulture, $"時間 {minutes}:{seconds - minutes * 60:00.00}");
    }

    private static int RunCount(IPluginContext context) =>
        int.TryParse(context.Settings.Get(RunCountKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;

    private static int IncrementRunCount(IPluginContext context)
    {
        int count = RunCount(context) + 1;
        context.Settings.Set(RunCountKey, count.ToString(CultureInfo.InvariantCulture));
        return count;
    }
}
