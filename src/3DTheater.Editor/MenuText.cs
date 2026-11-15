using System.Text;
using System.Text.RegularExpressions;

namespace Theater3D.Editor;

/// <summary>
/// メニューの項目の名前を、ボタンや見出しに使う形にする（ライブモードの設定画面。設計判断記録 78 章）。
/// メニューのアクセスキー（「保存(_S)」の「(_S)」、「Save_As」の「_」）を外し、「__」は「_」に戻す。
/// </summary>
public static partial class MenuText
{
    public static string Clean(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return string.Empty;
        }
        string text = AccessKeyInParentheses().Replace(header, string.Empty);
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '_')
            {
                if (i + 1 < text.Length && text[i + 1] == '_')
                {
                    result.Append('_'); // 「__」は、名前の中の「_」
                    i++;
                }
                continue; // アクセスキーの印
            }
            result.Append(text[i]);
        }
        return result.ToString().Trim();
    }

    // 「保存(_S)」「開く(_O)...」の「(_S)」「(_O)」
    [GeneratedRegex(@"\(_[^)]\)")]
    private static partial Regex AccessKeyInParentheses();
}
