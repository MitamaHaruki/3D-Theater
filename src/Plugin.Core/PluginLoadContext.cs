using System.Reflection;
using System.Runtime.Loader;

namespace Plugin.Core;

/// <summary>
/// プラグイン 1 つ分の読み込み先（指示書 7.3）。プラグインが持ってきた部品（依存する DLL）は、このプラグインの中だけで使う。
/// ただし共通の契約（Plugin.Core・UI の契約・アプリ専用 SDK）は、アプリが読み込んだものを使う
/// （プラグインのフォルダーに写しがあっても読まない。重ねて読むと、同じ名前の別の型になり、アプリとやり取りできない）。
/// .NET の部品と WPF は、もともとアプリと同じものを使う。DLL を外すこと（ホットアンロード）はしない。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _shared;

    public PluginLoadContext(string mainAssemblyPath, IEnumerable<string> sharedAssemblyNames)
        : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _shared = new HashSet<string>(sharedAssemblyNames, StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && _shared.Contains(assemblyName.Name))
        {
            return null; // アプリが読み込んだもの（既定の読み込み先）を使う
        }
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
