using System;
using System.Threading.Tasks;

namespace TysiTweaks;

public abstract class Tweak {
    public abstract string DisplayName { get; }
    public abstract string Description { get; }

    /// <summary>Called off the framework thread. Throwing here disables the tweak instead of taking the plugin down.</summary>
    public abstract Task OnEnableAsync();

    /// <summary>Called off the framework thread, on toggle and on plugin unload. Must undo everything OnEnableAsync did.</summary>
    public abstract Task OnDisableAsync();

    /// <summary>Set during OnEnableAsync to give the tweak a Settings button in the browser. Cleared on disable.</summary>
    public Action? OpenConfigAction { get; set; }

    public bool IsRunning { get; internal set; }

    public string Name => GetType().Name;
}
