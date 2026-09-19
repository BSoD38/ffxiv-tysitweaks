using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using AgentId = Dalamud.Game.Agent.AgentId;

namespace TysiTweaks.Tweaks;

/// <summary>
/// Sends the portrait update itself when a gear set is updated, re-applying a linked glamour plate first,
/// and keeps the game's confirmation preview from opening at all.
/// </summary>
public class AutoUpdatePortrait : Tweak {
    public override string DisplayName => "Auto-Update Portraits";

    public override string Description =>
        "Updates the portrait linked to a gear set when you update that gear set, without the confirmation window.\n" +
        "A linked glamour plate is applied to your character first (cities and sanctuaries only).";

    /// <summary>Keeps the plate and the portrait from landing in the same instant as the gear set update.</summary>
    private static readonly TimeSpan HumanDelay = TimeSpan.FromMilliseconds(500.0);

    /// <summary>
    /// How long the equipped gear has to stop changing before it counts as the final look.
    /// A glamour plate lands piece by piece, so the checksum moves several times on the way.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(400.0);

    /// <summary>Past this, the game's own preview is opened for the player instead.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10.0);

    /// <summary>The preview is kept from opening for this long after the portrait is sent, since the
    /// update itself is another gear change the game would offer a preview for.</summary>
    private static readonly TimeSpan SuppressAfterSend = TimeSpan.FromSeconds(5.0);

    private enum Result { Updated, NotNeeded, NotReady, Failed }

    /// <summary>LogMessage row for "Portrait set as instant portrait."</summary>
    private const uint InstantPortraitNotice = 5865;

    private Hook<RaptureGearsetModule.Delegates.UpdateGearset>? updateGearsetHook;

    /// <summary>Audits every portrait update leaving the client, including any the game sends itself.</summary>
    private Hook<BannerHelper.Delegates.SendBannerData>? sendBannerDataHook;

    /// <summary>Cancels a plate re-equip that is still queued when the tweak is switched off.</summary>
    private CancellationTokenSource? cts;

    private int activeGearsetId = -1;
    private int sendsThisUpdate;
    private DateTime sendNotBefore;
    private DateTime sendDeadline;
    private DateTime suppressUntil;
    private DateTime lastGearChange;
    private uint lastChecksum;
    private bool plateApplyPending;
    private bool swallowInstantPortraitNotice;

    public override async Task OnEnableAsync() {
        cts = new CancellationTokenSource();

        unsafe {
            updateGearsetHook = IGameInteropProvider.Get().HookFromAddress<RaptureGearsetModule.Delegates.UpdateGearset>(
                RaptureGearsetModule.MemberFunctionPointers.UpdateGearset,
                OnUpdateGearset);

            sendBannerDataHook = IGameInteropProvider.Get().HookFromAddress<BannerHelper.Delegates.SendBannerData>(
                BannerHelper.MemberFunctionPointers.SendBannerData,
                OnSendBannerData);
        }

        await IFramework.Get().Run(() => {
            updateGearsetHook.Enable();
            sendBannerDataHook.Enable();
        });

        IChatGui.Get().LogMessage += OnLogMessage;
        IAgentLifecycle.Get().RegisterListener(AgentEvent.PreShow, AgentId.BannerPreview, OnBannerPreviewPreShow);
        IFramework.Get().Update += OnFrameworkUpdate;
    }

    public override async Task OnDisableAsync() {
        IFramework.Get().Update -= OnFrameworkUpdate;
        IAgentLifecycle.Get().UnregisterListener(AgentEvent.PreShow, AgentId.BannerPreview, OnBannerPreviewPreShow);
        IChatGui.Get().LogMessage -= OnLogMessage;

        cts?.Cancel();
        cts?.Dispose();
        cts = null;

        activeGearsetId = -1;
        suppressUntil = default;
        plateApplyPending = false;
        swallowInstantPortraitNotice = false;

        await IFramework.Get().Run(() => {
            updateGearsetHook?.Dispose();
            sendBannerDataHook?.Dispose();
        });

        updateGearsetHook = null;
        sendBannerDataHook = null;
    }

    private unsafe bool OnSendBannerData(BannerHelper* helper, BannerData* data) {
        var log = IPluginLog.Get();
        var ours = activeGearsetId >= 0;

        if (ours && ++sendsThisUpdate > 1) {
            log.Error($"Blocked a second portrait update for one gear set update (checksum {data->Checksum:X})");
            return false;
        }

        log.Info($"Portrait update leaving the client: {(ours ? "sent by the tweak" : "sent by the game")} (checksum {data->Checksum:X})");

        return sendBannerDataHook!.Original(helper, data);
    }

    /// <summary>
    /// Re-equipping the gear set makes the game announce the linked portrait a second time, which reads
    /// as the portrait having been updated twice. Only the notice after the real update is kept.
    /// </summary>
    private void OnLogMessage(ILogMessage message) {
        if (!swallowInstantPortraitNotice || message.LogMessageId is not InstantPortraitNotice) return;

        swallowInstantPortraitNotice = false;
        message.PreventOriginal();
    }

    private unsafe int OnUpdateGearset(RaptureGearsetModule* module, int gearsetId) {
        var gearset = module->IsValidGearset(gearsetId) ? module->GetGearset(gearsetId) : null;
        var hasPortrait = gearset is not null && gearset->BannerIndex is not 0;
        var plate = gearset is null ? (byte)0 : gearset->GlamourSetLink;
        var now = DateTime.UtcNow;

        // Set before the call, because the game opens the preview from inside it.
        if (hasPortrait) suppressUntil = now + Timeout;

        var result = updateGearsetHook!.Original(module, gearsetId);

        if (!hasPortrait) return result;

        activeGearsetId = gearsetId;
        sendsThisUpdate = 0;
        sendNotBefore = now + HumanDelay;
        sendDeadline = now + Timeout;
        lastGearChange = now;
        lastChecksum = 0;

        var canApplyPlate = plate is not 0 && module->CurrentGearsetIndex == gearsetId && UIGlobals.CanApplyGlamourPlates(true);
        if (canApplyPlate) {
            plateApplyPending = true;
            IFramework.Get().RunOnTick(() => ApplyPlate(gearsetId, plate), delay: HumanDelay, cancellationToken: cts!.Token);
        }
        else if (plate is not 0) {
            IPluginLog.Get().Info($"Gear set {gearsetId} is linked to glamour plate {plate} but it cannot be applied here");
        }

        IPluginLog.Get().Info($"Gear set {gearsetId} updated, taking over its portrait{(canApplyPlate ? $" after glamour plate {plate}" : "")}");

        return result;
    }

    private unsafe void ApplyPlate(int gearsetId, byte plate) {
        swallowInstantPortraitNotice = true;
        var result = RaptureGearsetModule.Instance()->EquipGearset(gearsetId, plate);
        plateApplyPending = false;
        sendNotBefore = DateTime.UtcNow + HumanDelay;
        IPluginLog.Get().Info($"Re-equipped gear set {gearsetId} with glamour plate {plate} (result {result})");
    }

    private void OnBannerPreviewPreShow(AgentEvent type, AgentArgs args) {
        if (DateTime.UtcNow > suppressUntil) return;

        args.PreventOriginal();
        IPluginLog.Get().Info("Kept the portrait preview from opening");
    }

    private void OnFrameworkUpdate(IFramework framework) {
        if (activeGearsetId < 0) return;

        var now = DateTime.UtcNow;
        var checksum = EquippedChecksum();

        if (checksum != lastChecksum) {
            lastChecksum = checksum;
            lastGearChange = now;
        }

        var settled = !plateApplyPending && now >= sendNotBefore && now - lastGearChange >= SettleTime;

        if (settled) {
            switch (TryUpdatePortrait(activeGearsetId, checksum)) {
                case Result.Updated:
                    suppressUntil = now + SuppressAfterSend;
                    activeGearsetId = -1;
                    return;

                case Result.NotNeeded:
                    suppressUntil = default;
                    activeGearsetId = -1;
                    swallowInstantPortraitNotice = false;
                    return;

                case Result.Failed:
                    FallBackToPreview("the game refused the portrait update");
                    return;
            }
        }

        if (now > sendDeadline) {
            FallBackToPreview(plateApplyPending
                ? "the glamour plate never finished applying"
                : $"the gear never settled (checksum {checksum:X})");
        }
    }

    private unsafe void FallBackToPreview(string reason) {
        activeGearsetId = -1;
        suppressUntil = default;
        swallowInstantPortraitNotice = false;
        IPluginLog.Get().Warning($"Opening the game's portrait preview because {reason}");
        AgentBannerPreview.Instance()->Show();
    }

    private static unsafe uint EquippedChecksum() => UIGlobals.GenerateEquippedItemsChecksum();

    /// <summary>Runs the same steps the preview's confirm button does.</summary>
    private static unsafe Result TryUpdatePortrait(int gearsetId, uint checksum) {
        var log = IPluginLog.Get();
        var gearsetModule = RaptureGearsetModule.Instance();

        if (!gearsetModule->IsValidGearset(gearsetId)) {
            log.Warning($"No portrait update: gear set {gearsetId} is not valid");
            return Result.Failed;
        }

        var gearset = gearsetModule->GetGearset(gearsetId);
        if (gearset is null) {
            log.Warning("No portrait update: gear set is null");
            return Result.Failed;
        }

        var banner = gearset->GetBanner();
        if (banner is null) {
            log.Warning($"No portrait update: gear set {gearsetId} has no portrait to update");
            return Result.Failed;
        }

        if (banner->Checksum == checksum) {
            log.Info($"No portrait update needed: it already matches the gear (checksum {checksum:X})");
            return Result.NotNeeded;
        }

        var localPlayer = (Character*)Control.GetLocalPlayer();
        if (localPlayer is null) return Result.NotReady;

        var bannerHelper = UIModule.Instance()->GetUIModuleHelpers()->BannerHelper;

        if (!bannerHelper->BannerModuleEntry_IsCurrentCharaCardBannerOutdated(banner, false)) return Result.NotReady;
        if (!bannerHelper->BannerModuleEntry_IsCharacterDataOutdated(banner, false)) return Result.NotReady;

        banner->LastUpdated = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        banner->Checksum = checksum;
        bannerHelper->BannerModuleEntry_ApplyRaceGenderHeightTribe(banner, localPlayer);
        BannerModule.Instance()->UserFileEvent.HasChanges = true;

        var bannerData = new BannerData();
        bannerHelper->BannerData_ApplyBannerModuleEntry(&bannerData, banner);

        if (!bannerHelper->SendBannerData(&bannerData)) {
            log.Warning($"Portrait update for gear set {gearsetId} was rejected by the game");
            return Result.Failed;
        }

        log.Info($"Portrait update for gear set {gearsetId} sent (checksum {checksum:X})");
        return Result.Updated;
    }
}
