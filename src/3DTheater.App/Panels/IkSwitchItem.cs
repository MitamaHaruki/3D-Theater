namespace Theater3D.App.Panels;

/// <summary>表示・IK の、IK ボーン 1 本の「IK を使う」（プロパティ欄のチェックボックス 1 つ）。変えると <paramref name="set"/> を呼ぶ。</summary>
public sealed class IkSwitchItem(string name, bool isEnabled, bool isEditable, Action<bool> set)
{
    public string Name { get; } = name;

    public bool IsEditable { get; } = isEditable;

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (value != isEnabled)
            {
                isEnabled = value;
                set(value);
            }
        }
    }
}
