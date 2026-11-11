using System.Text.Json;
using System.Text.RegularExpressions;

namespace Plugin.Core;

/// <summary>
/// plugin.json の中身（指示書 7.3）。プラグインのフォルダーに置く。例:
/// <code>
/// {
///   "id": "example.scene-info",
///   "name": "シーンの情報",
///   "version": "1.0.0",
///   "host": "3DTheater",
///   "sdkVersion": "1.0",
///   "assembly": "Example.SceneInfo.dll",
///   "entryType": "Example.SceneInfo.SceneInfoPlugin",
///   "description": "…",
///   "author": "…",
///   "requires": ["commands", "panels", "scene.read"]
/// }
/// </code>
/// </summary>
public sealed partial record PluginManifest
{
    public const string FileName = "plugin.json";

    /// <summary>プラグインの名前（英数字・. _ -。アプリの中で一意）。</summary>
    public required string Id { get; init; }

    /// <summary>画面に出す名前。</summary>
    public required string Name { get; init; }

    public required Version Version { get; init; }

    /// <summary>対象のアプリ（例: "3DTheater"）。</summary>
    public required string Host { get; init; }

    /// <summary>作ったときの SDK の版（主.副）。アプリの SDK と主の版が同じで、副の版がアプリ以下なら使える。</summary>
    public required Version SdkVersion { get; init; }

    /// <summary>入口の DLL（プラグインのフォルダーから見た場所）。</summary>
    public required string Assembly { get; init; }

    /// <summary>入口の型（<see cref="IPlugin"/> を実装した型の、名前空間を含む名前）。</summary>
    public required string EntryType { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    /// <summary>使う機能（アプリに無いものがあれば読み込まない）。</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>
    /// JSON を読む。足りない項目・形の誤りは <see cref="PluginManifestException"/>（日本語の理由）。
    /// </summary>
    public static PluginManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException($"plugin.json を JSON として読めません（{ex.LineNumber + 1} 行目付近）");
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PluginManifestException("plugin.json の中身が { … } の形ではありません");
            }
            string id = RequiredString(root, "id");
            if (!IdPattern().IsMatch(id))
            {
                throw new PluginManifestException($"id「{id}」に使えない文字があります（英数字と . _ - だけにしてください）");
            }
            string assembly = RequiredString(root, "assembly");
            if (Path.IsPathRooted(assembly) || assembly.Split('/', '\\').Contains(".."))
            {
                throw new PluginManifestException("assembly は、プラグインのフォルダーの中の場所にしてください");
            }
            return new PluginManifest
            {
                Id = id,
                Name = RequiredString(root, "name"),
                Version = RequiredVersion(root, "version"),
                Host = RequiredString(root, "host"),
                SdkVersion = RequiredVersion(root, "sdkVersion"),
                Assembly = assembly,
                EntryType = RequiredString(root, "entryType"),
                Description = OptionalString(root, "description"),
                Author = OptionalString(root, "author"),
                Requires = Strings(root, "requires"),
            };
        }
    }

    /// <summary>ファイルから読む。</summary>
    public static PluginManifest Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginManifestException($"plugin.json を読めません（{ex.Message}）");
        }
        return Parse(json);
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PluginManifestException($"plugin.json に「{name}」（文字列）がありません");
        }
        return value.GetString()!.Trim();
    }

    private static string OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;

    private static Version RequiredVersion(JsonElement root, string name)
    {
        string text = RequiredString(root, name);
        if (!Version.TryParse(text, out Version? version))
        {
            throw new PluginManifestException($"plugin.json の「{name}」（{text}）が版の形（例: 1.0.0）ではありません");
        }
        return version;
    }

    private static string[] Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
        {
            throw new PluginManifestException($"plugin.json の「{name}」は文字列の並び（[\"…\", …]）にしてください");
        }
        return [.. value.EnumerateArray().Select(v => v.GetString()!.Trim()).Where(s => s.Length > 0)];
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();
}

/// <summary>plugin.json の誤り（画面に出せる日本語の理由）。</summary>
public sealed class PluginManifestException(string message) : Exception(message);

/// <summary>アプリの側の情報（どのプラグインを読めるかを決める）。</summary>
/// <param name="HostId">アプリの名前（plugin.json の host と比べる）。</param>
/// <param name="SdkVersion">アプリが出している SDK の版。</param>
/// <param name="Capabilities">アプリが出している機能（plugin.json の requires と比べる）。</param>
public sealed record PluginHostInfo(string HostId, Version SdkVersion, IReadOnlyCollection<string> Capabilities)
{
    /// <summary>このアプリで使えるか。使えなければ理由を返す（使えれば null）。</summary>
    public string? CheckCompatibility(PluginManifest manifest)
    {
        if (!string.Equals(manifest.Host, HostId, StringComparison.OrdinalIgnoreCase))
        {
            return $"このプラグインは「{manifest.Host}」用です（このアプリは「{HostId}」）";
        }
        if (manifest.SdkVersion.Major != SdkVersion.Major)
        {
            return $"SDK の版が合いません（プラグイン {Short(manifest.SdkVersion)}、アプリ {Short(SdkVersion)}。主の版が違います）";
        }
        if (manifest.SdkVersion.Minor > SdkVersion.Minor)
        {
            return $"新しい SDK が必要です（プラグイン {Short(manifest.SdkVersion)}、アプリ {Short(SdkVersion)}）。アプリを新しくしてください";
        }
        string[] missing = [.. manifest.Requires.Where(r => !Capabilities.Contains(r, StringComparer.OrdinalIgnoreCase))];
        return missing.Length > 0 ? $"このアプリに無い機能を使います（{string.Join("・", missing)}）" : null;
    }

    private static string Short(Version version) => $"{version.Major}.{Math.Max(version.Minor, 0)}";
}
