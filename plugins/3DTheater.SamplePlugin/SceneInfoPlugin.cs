using System.Globalization;
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

    private static int RunCount(IPluginContext context) =>
        int.TryParse(context.Settings.Get(RunCountKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;

    private static int IncrementRunCount(IPluginContext context)
    {
        int count = RunCount(context) + 1;
        context.Settings.Set(RunCountKey, count.ToString(CultureInfo.InvariantCulture));
        return count;
    }
}
