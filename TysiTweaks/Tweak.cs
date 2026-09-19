using System;
using System.Threading.Tasks;

namespace TysiTweaks;

public abstract class Tweak {
    public abstract string DisplayName { get; }
    public abstract string Description { get; }

    // Both run off the framework thread. Throwing in OnEnableAsync disables the tweak instead of taking the plugin down.
    public abstract Task OnEnableAsync();

    // Must undo everything OnEnableAsync did.
    public abstract Task OnDisableAsync();

    // Set in OnEnableAsync for a Settings button in the browser, cleared on disable.
    public Action? OpenConfigAction { get; set; }

    public bool IsRunning { get; internal set; }

    public string Name => GetType().Name;
}
