using System;
using Dalamud.Plugin.Services;

namespace TysiTweaks;

// Adds a .Get() to every Dalamud service interface: IPluginLog.Get().Debug(...).
public static class ServiceExtension {
    private static class ServiceInstance<T> where T : class, IDalamudService {
        public static T? Instance => field ??= Plugin.PluginInterface.GetService(typeof(T)) as T;
    }

    extension<T>(T) where T : class, IDalamudService {
        public static T Get() => ServiceInstance<T>.Instance ?? throw new InvalidOperationException($"Service {typeof(T).Name} not found.");
    }
}
