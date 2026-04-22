using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ProdToy.Sdk;

/// <summary>
/// Ambient logger for plugin internals. The SDK is loaded once and shared
/// across all plugin AssemblyLoadContexts, so we key the context by the
/// calling assembly — each plugin DLL is a distinct Assembly, so calls from
/// plugin A never leak to plugin B's log tag.
///
/// Usage in a plugin's <c>Initialize(context)</c>:
/// <code>
/// PluginLog.Bootstrap(context);
/// </code>
/// Internal helper classes can then log without threading the context through:
/// <code>
/// PluginLog.Info("scheduled alarm");
/// PluginLog.Warn("missed fire window");
/// PluginLog.Error("ring failed", ex);
/// </code>
/// Before <c>Bootstrap</c> runs (or for calls from assemblies that never bootstrapped),
/// the calls are silently dropped.
/// </summary>
public static class PluginLog
{
    private static readonly ConcurrentDictionary<Assembly, IPluginContext> _contexts = new();

    /// <summary>
    /// Register the caller's plugin context. Every assembly that calls this
    /// gets its own entry keyed by its Assembly, so multiple plugins coexist
    /// in the shared SDK without stepping on each other.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Bootstrap(IPluginContext context)
    {
        if (context == null) return;
        _contexts[Assembly.GetCallingAssembly()] = context;
    }

    /// <summary>Log INFO, tagged with the calling plugin's id.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Info(string message)
    {
        if (_contexts.TryGetValue(Assembly.GetCallingAssembly(), out var ctx))
            ctx.Log(message);
    }

    /// <summary>Log WARN, tagged with the calling plugin's id.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Warn(string message)
    {
        if (_contexts.TryGetValue(Assembly.GetCallingAssembly(), out var ctx))
            ctx.LogWarn(message);
    }

    /// <summary>Log ERROR, tagged with the calling plugin's id.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Error(string message, Exception? ex = null)
    {
        if (_contexts.TryGetValue(Assembly.GetCallingAssembly(), out var ctx))
            ctx.LogError(message, ex);
    }
}
