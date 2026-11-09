using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Import;

/// <summary>
/// モデルの読み込みと、モデルを外す操作（どちらも元に戻せる）。
/// 外している間も GPU に載せた形（<see cref="LoadedModel"/>）を手放さずに持っておき、戻すときにそのまま使う。
/// 履歴から消えたとき（古くなった・新しいプロジェクトにした・終了した）に、外したままのモデルだけを手放す。
/// </summary>
internal sealed class ModelPresenceAction : IUndoableAction, IDisposable
{
    private readonly EditorContext _context;
    private readonly ModelNode _node;
    private readonly LoadedModel _loaded;
    private readonly bool _isImport;
    private int _index;

    private ModelPresenceAction(EditorContext context, ModelNode node, LoadedModel loaded, bool isImport, int index)
    {
        _context = context;
        _node = node;
        _loaded = loaded;
        _isImport = isImport;
        _index = index;
    }

    public string Description => _isImport ? $"モデル「{_node.Name}」の読み込み" : $"モデル「{_node.Name}」を外す";

    /// <summary>読み込んだモデルを、元に戻せる操作として履歴に積む（読み込んだ後に呼ぶ）。</summary>
    public static void RecordImport(EditorContext context, ModelNode node)
    {
        if (context.GetLoaded(node) is { } loaded)
        {
            int index = context.Scene.ModelsGroup.Children.IndexOf(node);
            context.Undo.Push(new ModelPresenceAction(context, node, loaded, isImport: true, index));
        }
    }

    /// <summary>モデルをシーンから外す（元に戻せる）。外したら true。</summary>
    public static bool Remove(EditorContext context, ModelNode node)
    {
        if (node.Parent != context.Scene.ModelsGroup || context.GetLoaded(node) is not { } loaded)
        {
            return false;
        }
        if (!node.CanEdit)
        {
            AppLog.Warning($"モデル「{node.Name}」は編集ロック中のため外せません（シーン一覧の鍵で外せます）");
            return false;
        }
        var action = new ModelPresenceAction(context, node, loaded, isImport: false, context.Scene.ModelsGroup.Children.IndexOf(node));
        action.TakeOut();
        context.Undo.Push(action);
        AppLog.Info($"モデル「{node.Name}」をシーンから外しました（Ctrl+Z で戻せます）");
        return true;
    }

    public void Undo()
    {
        if (_isImport)
        {
            TakeOut();
        }
        else
        {
            PutBack();
        }
    }

    public void Redo()
    {
        if (_isImport)
        {
            PutBack();
        }
        else
        {
            TakeOut();
        }
    }

    public void Dispose()
    {
        // シーンに無い（外したままの）ときだけ手放す。シーンにあるモデルは、シーンの側で片付ける
        if (_node.Parent is null && !_context.Models.ContainsKey(_node.Id))
        {
            _loaded.Dispose();
        }
    }

    private void TakeOut()
    {
        _context.Playback.Pause();
        _index = Math.Max(0, _context.Scene.ModelsGroup.Children.IndexOf(_node));
        // 外すモデルの中を選んでいたら、選択とキーの選択を外す
        if (_context.Selection.Items.Any(n => n == _node || n.FindAncestor<ModelNode>() == _node))
        {
            _context.Selection.Clear();
        }
        _context.KeySelection.Clear();
        _context.Scene.Remove(_node);
        _context.Models.Remove(_node.Id);
        _context.Animation.NotifyStateChanged(); // 未登録の変更の数が変わることがある
        _context.RenderLoop.InvalidateAll();
    }

    private void PutBack()
    {
        _context.Playback.Pause();
        _context.Models[_node.Id] = _loaded;
        _context.Scene.Insert(_context.Scene.ModelsGroup, _index, _node);
        _context.Animation.ApplyFrame();
        _context.Physics.Reset(); // 外していた間の物理の続きではなく、今の姿勢から始める
        _context.Animation.NotifyStateChanged();
        _context.RenderLoop.InvalidateAll();
    }
}
