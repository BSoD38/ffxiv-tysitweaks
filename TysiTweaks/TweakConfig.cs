using System;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace TysiTweaks;

/// <summary>
/// Per-tweak settings, stored as {TypeName}.json in the plugin config directory.
/// </summary>
public abstract class TweakConfig<T> where T : TweakConfig<T>, new() {
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, WriteIndented = true };

    private static string PathFor(Type type)
        => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, $"{type.Name}.json");

    public static T Load() {
        var path = PathFor(typeof(T));

        try {
            if (File.Exists(path)) {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
            }
        }
        catch (Exception e) {
            IPluginLog.Get().Error(e, $"Failed to load {path}, falling back to defaults");
        }

        return new T();
    }

    public void Save() {
        try {
            Plugin.PluginInterface.ConfigDirectory.Create();
            File.WriteAllText(PathFor(GetType()), JsonSerializer.Serialize(this, GetType(), Options));
        }
        catch (Exception e) {
            IPluginLog.Get().Error(e, $"Failed to save {GetType().Name}");
        }
    }
}
