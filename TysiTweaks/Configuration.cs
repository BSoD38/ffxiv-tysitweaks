using System.Collections.Generic;
using Dalamud.Configuration;

namespace TysiTweaks;

public class Configuration : IPluginConfiguration {
    public int Version { get; set; } = 1;

    public HashSet<string> EnabledTweaks { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
