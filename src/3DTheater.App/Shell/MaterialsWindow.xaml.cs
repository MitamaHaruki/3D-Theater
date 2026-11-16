using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 材質の設定（モデルのメニュー・プロパティ欄）。1 つのモデルの材質の一覧から選び、上書き（拡張材質）を変える。
/// - 色の計算: MMD と同じ（トゥーン）か PBR（色合い・金属らしさ・粗さ）
/// - 発光: 色と強さ（どちらの色の計算にも足す）
/// 変えるたびに編集の入口（元に戻せる）を通してモデルの項目へ入れる。つまみを続けて動かした分は 1 つの操作にまとめる。
/// </summary>
public partial class MaterialsWindow : Window
{
    private readonly EditService? _edits;
    private readonly ModelNode? _model;
    private bool _loading;

    internal MaterialsWindow(EditService edits, ModelNode model)
    {
        InitializeComponent();
        _edits = edits;
        _model = model;
        HeaderText.Text = $"モデル「{model.Name}」の材質（{model.MaterialNames.Count} 個）";
        // ロックされたモデルも、一覧と値は見られるようにし、変える部品だけを止める
        bool editable = model.CanEdit;
        LockedNote.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
        ToonRadio.IsEnabled = PbrRadio.IsEnabled = editable;
        ResetButton.IsEnabled = editable;
        TintBox.IsReadOnly = EmissiveBox.IsReadOnly = !editable;
        MetallicSlider.IsEnabled = RoughnessSlider.IsEnabled = EmissiveSlider.IsEnabled = editable;
        RefreshList();
        if (model.MaterialNames.Count > 0)
        {
            MaterialList.SelectedIndex = 0;
        }
        else
        {
            EditorPanel.IsEnabled = false;
            MaterialNameText.Text = "このモデルには材質がありません";
        }
    }

    private int SelectedIndex => MaterialList.SelectedIndex;

    /// <summary>材質の一覧（上書きのある材質には ● を付ける）。選んでいる材質はそのまま。</summary>
    private void RefreshList()
    {
        if (_model is null)
        {
            return;
        }
        int selected = MaterialList.SelectedIndex;
        _loading = true;
        try
        {
            MaterialList.ItemsSource = _model.MaterialNames
                .Select((name, i) => $"{(_model.MaterialOverrides.Items.ContainsKey(i) ? "● " : "　 ")}{i}: {name}")
                .ToArray();
            MaterialList.SelectedIndex = selected;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowValues(MaterialOverride value)
    {
        _loading = true;
        try
        {
            ToonRadio.IsChecked = value.Shading == MaterialShading.Toon;
            PbrRadio.IsChecked = value.Shading == MaterialShading.Pbr;
            TintBox.Text = ImageExport.FormatColor(value.Tint);
            MetallicSlider.Value = value.Metallic;
            RoughnessSlider.Value = value.Roughness;
            EmissiveBox.Text = ImageExport.FormatColor(value.EmissiveColor);
            EmissiveSlider.Value = value.EmissiveStrength;
        }
        finally
        {
            _loading = false;
        }
        UpdateTexts();
    }

    /// <summary>画面の値（小数 2 桁に丸める）。読めない色は今の値のままにする。</summary>
    private MaterialOverride Read(MaterialOverride current) => new MaterialOverride
    {
        Shading = PbrRadio.IsChecked == true ? MaterialShading.Pbr : MaterialShading.Toon,
        Tint = ImageExport.TryParseColor(TintBox.Text, out Vector3 tint) ? tint : current.Tint,
        Metallic = (float)Math.Round(MetallicSlider.Value, 2),
        Roughness = (float)Math.Round(RoughnessSlider.Value, 2),
        EmissiveColor = ImageExport.TryParseColor(EmissiveBox.Text, out Vector3 emissive) ? emissive : current.EmissiveColor,
        EmissiveStrength = (float)Math.Round(EmissiveSlider.Value, 2),
    }.Normalized();

    private void UpdateTexts()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        MetallicText.Text = MetallicSlider.Value.ToString("0.00", c);
        RoughnessText.Text = RoughnessSlider.Value.ToString("0.00", c);
        EmissiveText.Text = EmissiveSlider.Value.ToString("0.0", c);
    }

    /// <summary>画面の値をモデルへ入れる（元に戻せる操作として）。</summary>
    private void Apply()
    {
        if (_loading || _model is null || _edits is null || SelectedIndex < 0 || !_model.CanEdit)
        {
            return;
        }
        UpdateTexts();
        int index = SelectedIndex;
        MaterialOverride value = Read(_model.MaterialOverrides[index]);
        string name = _model.MaterialNames[index];
        bool hadOverride = _model.MaterialOverrides.Items.ContainsKey(index);
        _edits.Change($"材質「{name}」を変える", [_model], () => _model.MaterialOverrides = _model.MaterialOverrides.With(index, value),
            mergeKey: $"material:{_model.Id}:{index}");
        if (hadOverride != _model.MaterialOverrides.Items.ContainsKey(index))
        {
            RefreshList(); // ● を付け外しする
        }
    }

    private void OnMaterialSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _model is null || SelectedIndex < 0)
        {
            return;
        }
        MaterialNameText.Text = $"{SelectedIndex}: {_model.MaterialNames[SelectedIndex]}";
        ShowValues(_model.MaterialOverrides[SelectedIndex]);
    }

    private void OnControlChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Apply();

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || _edits is null || SelectedIndex < 0 || !_model.CanEdit)
        {
            return;
        }
        int index = SelectedIndex;
        _edits.Change($"材質「{_model.MaterialNames[index]}」を PMX のままに戻す", [_model],
            () => _model.MaterialOverrides = _model.MaterialOverrides.With(index, MaterialOverride.None));
        ShowValues(MaterialOverride.None);
        RefreshList();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
