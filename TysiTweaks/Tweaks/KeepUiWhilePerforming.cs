using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TysiTweaks.Tweaks;

public class KeepUiWhilePerforming : Tweak {
    public override string DisplayName => "Keep Chat Bubbles While Performing";

    public override string Description =>
        "Keeps chat bubbles visible while you play an instrument.\n" +
        "Performance mode otherwise hides them along with the rest of the HUD.";

    private const string ChatBubblesAddon = "MiniTalkPlayer";

    // Set by Hide when a bubble times out. The addon's Refresh normally clears it through Show, but skips that while performing.
    private const uint HiddenShowHideFlag = 0x01;

    public override Task OnEnableAsync() {
        IAddonLifecycle.Get().RegisterListener(AddonEvent.PostRefresh, ChatBubblesAddon, OnRefresh);
        return Task.CompletedTask;
    }

    public override Task OnDisableAsync() {
        IAddonLifecycle.Get().UnregisterListener(OnRefresh);
        return Task.CompletedTask;
    }

    private unsafe void OnRefresh(AddonEvent type, AddonArgs args) {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon is null || addon->IsVisible || !ICondition.Get()[ConditionFlag.Performing]) return;

        addon->Show(false, HiddenShowHideFlag);
    }
}
