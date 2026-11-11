using Plugin.Core;

namespace Plugin.TestPlugins;

/// <summary>正しく動くプラグイン: コマンドを 1 つ登録し、設定に始めた回数を書く。</summary>
public sealed class GoodPlugin : IPlugin
{
    public static int ShutdownCount { get; set; }

    public void Initialize(IPluginContext context)
    {
        int started = int.TryParse(context.Settings.Get("started"), out int n) ? n : 0;
        context.Settings.Set("started", (started + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        context.Commands.Register(new PluginCommand { Id = "hello", Title = "こんにちは", Execute = () => { } });
        context.Log.Info("始めました");
    }

    public void Shutdown() => ShutdownCount++;
}

/// <summary>始めるときに例外を投げる（登録した途中のコマンドは外れること）。</summary>
public sealed class ThrowingPlugin : IPlugin
{
    public void Initialize(IPluginContext context)
    {
        context.Commands.Register(new PluginCommand { Id = "half", Title = "途中まで", Execute = () => { } });
        throw new InvalidOperationException("わざと失敗しました");
    }

    public void Shutdown()
    {
    }
}

/// <summary>止めるときに例外を投げる。</summary>
public sealed class ShutdownThrowingPlugin : IPlugin
{
    public void Initialize(IPluginContext context)
    {
    }

    public void Shutdown() => throw new InvalidOperationException("止めるときに失敗しました");
}

/// <summary>引数なしで作れない。</summary>
public sealed class NeedsArgumentPlugin(string name) : IPlugin
{
    public string Name { get; } = name;

    public void Initialize(IPluginContext context)
    {
    }

    public void Shutdown()
    {
    }
}

/// <summary>IPlugin を実装していない。</summary>
public sealed class NotAPlugin
{
}
