using System.Windows;
using System.Windows.Controls;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>シーンの名前を聞く窓（設計判断記録 78 章の 4 段目）。使えない名前なら、理由を出して「保存」を押せない形にする。</summary>
public partial class SceneNameWindow : Window
{
    public SceneNameWindow(string initialName)
    {
        InitializeComponent();
        FolderText.Text = $"置き場所: {SceneLibrary.Folder}";
        NameBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Check();
    }

    /// <summary>入れた名前（「保存」を押したときだけ）。</summary>
    public string? SceneName { get; private set; }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Check();

    private void Check()
    {
        (string? name, string? problem) = SceneNames.Validate(NameBox.Text);
        ProblemText.Text = NameBox.Text.Length == 0 ? string.Empty : problem ?? string.Empty;
        OkButton.IsEnabled = name is not null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (SceneNames.Validate(NameBox.Text).Name is { } name)
        {
            SceneName = name;
            DialogResult = true;
        }
    }
}
