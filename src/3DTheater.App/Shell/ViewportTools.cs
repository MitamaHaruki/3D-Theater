using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// プレビューの左ボタンの操作を受け取る道具（プラグインの編集の道具。指示書 7.2 の「編集ツール」）。
/// 道具を選んでいる間、プレビューはギズモを出さず、左ボタンを押すとまず道具に渡す（道具が受け取らなければ、クリックで選ぶ）。
/// </summary>
public interface IViewportTool
{
    /// <summary>左ボタンを押した。true ならドラッグを受け取る。</summary>
    bool Press(ViewportPointer pointer);

    /// <summary>ドラッグ中に動いた。false なら、道具がドラッグをやめた（エラーなど。マウスを放す）。</summary>
    bool Move(ViewportPointer pointer);

    /// <summary>左ボタンを放した（ドラッグの終わり）。</summary>
    void Release(ViewportPointer pointer);

    /// <summary>ドラッグを取り消す（Esc・ドラッグ中の右クリック・道具を外したとき）。</summary>
    void Cancel();

    /// <summary>道具を外した。</summary>
    void Deactivate();
}

/// <summary>プレビューの中のマウスの位置（区画の左上から、WPF の画面の単位）と、そのときの視点。</summary>
public readonly record struct ViewportPointer(Point Position, Size ViewSize, ModifierKeys Modifiers, Ray Ray, Matrix4x4 ViewProjection);

/// <summary>今選んでいるプレビューの道具（全プレビューで共通。無ければアプリの道具＝ギズモ）。</summary>
public sealed class ViewportTools
{
    /// <summary>選んでいる道具（無ければ null）。</summary>
    public IViewportTool? Active { get; private set; }

    /// <summary>どこかのプレビューで道具のドラッグの途中か（その間は、元に戻す・やり直しをしない）。</summary>
    public bool IsDragging { get; set; }

    /// <summary>選んでいる道具が変わった（ボタンの表示とギズモの表示を合わせる）。</summary>
    public event EventHandler? ActiveChanged;

    /// <summary>道具を変える前に、ドラッグの途中なら取り消してほしい（プレビューが受け取る）。</summary>
    public event EventHandler? DragCancelRequested;

    /// <summary>道具を選ぶ（null ならアプリの道具に戻す）。前の道具は、ドラッグを取り消してから外す。</summary>
    public void Activate(IViewportTool? tool)
    {
        if (ReferenceEquals(tool, Active))
        {
            return;
        }
        if (IsDragging)
        {
            DragCancelRequested?.Invoke(this, EventArgs.Empty);
        }
        IViewportTool? old = Active;
        Active = tool;
        old?.Deactivate();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }
}
