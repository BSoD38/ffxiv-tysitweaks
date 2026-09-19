using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace TysiTweaks;

public static class TweakManager {
    private static readonly List<Tweak> tweaks = [];

    public static IReadOnlyList<Tweak> Tweaks => tweaks;

    public static async Task LoadAsync() {
        tweaks.AddRange(Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type => type.IsSubclassOf(typeof(Tweak)) && !type.IsAbstract)
            .Select(Activator.CreateInstance)
            .OfType<Tweak>()
            .OrderBy(tweak => tweak.DisplayName));

        await Task.WhenAll(tweaks
            .Where(tweak => Plugin.Config.EnabledTweaks.Contains(tweak.Name))
            .Select(tweak => Task.Run(() => EnableAsync(tweak))));
    }

    public static Task UnloadAsync()
        => Task.WhenAll(tweaks.Where(tweak => tweak.IsRunning).Select(tweak => Task.Run(() => DisableAsync(tweak))));

    public static async Task ToggleAsync(Tweak tweak) {
        if (tweak.IsRunning) {
            await DisableAsync(tweak);
            Plugin.Config.EnabledTweaks.Remove(tweak.Name);
        }
        else {
            await EnableAsync(tweak);

            // A tweak that failed to enable stays off across restarts.
            if (tweak.IsRunning) {
                Plugin.Config.EnabledTweaks.Add(tweak.Name);
            }
        }

        Plugin.Config.Save();
    }

    private static async Task EnableAsync(Tweak tweak) {
        try {
            await tweak.OnEnableAsync();
            tweak.IsRunning = true;
            IPluginLog.Get().Info($"Enabled {tweak.Name}");
        }
        catch (Exception e) {
            IPluginLog.Get().Error(e, $"Failed to enable {tweak.Name}, rolling back");
            await DisableAsync(tweak);
        }
    }

    private static async Task DisableAsync(Tweak tweak) {
        try {
            await tweak.OnDisableAsync();
            IPluginLog.Get().Info($"Disabled {tweak.Name}");
        }
        catch (Exception e) {
            IPluginLog.Get().Error(e, $"Failed to disable {tweak.Name}");
        }

        tweak.OpenConfigAction = null;
        tweak.IsRunning = false;
    }
}
