using System;
using Dalamud.Plugin.Services;

namespace TysiTweaks;

/// <summary>
/// Adds a .Get() to every Dalamud service interface, so services never need declaring.
/// </summary>
/// <code>IPluginLog.Get().Debug(...);</code>
public static class ServiceExtension {
    private static class ServiceInstance<T> where T : class, IDalamudService {
        public static T? Instance => field ??= Plugin.PluginInterface.GetService(typeof(T)) as T;
    }

    extension<T>(T) where T : class, IDalamudService {
        public static T Get() => ServiceInstance<T>.Instance ?? throw new InvalidOperationException($"Service {typeof(T).Name} not found.");
    }
}
