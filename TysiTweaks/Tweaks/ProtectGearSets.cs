using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using TysiTweaks.Ui;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace TysiTweaks.Tweaks;

/// <summary>
/// Marks the armoire entries whose item a gear set still needs, and blocks the store itself so the
/// context menu and any other route are covered too.
/// </summary>
/// <remarks>
/// Marking is done by opacity or by dropping the entry from the list, never by the list disabled state
/// and never by hiding a node. A row forwards the mouse wheel to its list through its own collision
/// node, so anything that stops the row taking events also stops the list scrolling over it.
/// </remarks>
public class ProtectGearSets : Tweak {
    public override string DisplayName => "Protect Gear Sets from the Armoire";

    public override string Description =>
        "Fades out every armoire entry whose item is used by one of your gear sets, and refuses to store it.\n" +
        "Storing such an item takes it out of your inventory and leaves the gear set missing a piece.";

    private const string AddonName = "Cabinet";

    /// <summary>Gear sets store a high quality item as its id plus this.</summary>
    private const uint HighQualityOffset = 1_000_000;

    /// <summary>AgentCabinet opens in mode 2 to pick an armoire item for a glamour plate, which stores nothing.</summary>
    private const uint StoreMode = 0;

    /// <summary>Number array the addon keeps its populated slot count in, per AddonCabinet.</summary>
    private const int SlotCountArray = 0x30;

    /// <summary>Opacity of a faded row. Lower is a stronger warning, 255 is untouched.</summary>
    private const byte DimmedAlpha = 90;

    private readonly HashSet<uint> gearsetItems = [];

    /// <summary>Opacity a row had before it was faded. Renderers are recycled as the list scrolls, so
    /// the original has to be put back rather than assumed to be fully opaque.</summary>
    private readonly Dictionary<nint, byte> fadedRows = [];

    private ProtectGearSetsConfig? config;
    private TweakConfigWindow? configWindow;
    private Hook<Cabinet.Delegates.StoreCabinetItem>? storeHook;

    public override async Task OnEnableAsync() {
        config = ProtectGearSetsConfig.Load();

        configWindow = new TweakConfigWindow {
            InternalName = "TysiProtectGearSetsConfig",
            Title = DisplayName,
            Size = new Vector2(450.0f, 120.0f),
            BuildNodes = () => [
                new CheckboxNode {
                    Height = 24.0f,
                    String = "Remove protected entries from the list",
                    IsChecked = config.HideRows,
                    OnClick = value => {
                        config.HideRows = value;
                        config.Save();
                        RequestListRebuild();
                    },
                },
            ],
        };

        OpenConfigAction = configWindow.Toggle;

        unsafe {
            storeHook = IGameInteropProvider.Get().HookFromAddress<Cabinet.Delegates.StoreCabinetItem>(
                Cabinet.MemberFunctionPointers.StoreCabinetItem,
                OnStoreCabinetItem);
        }

        await IFramework.Get().Run(storeHook.Enable);

        var addonLifecycle = IAddonLifecycle.Get();
        addonLifecycle.RegisterListener(AddonEvent.PostUpdate, AddonName, OnCabinetUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnCabinetFinalize);
    }

    public override async Task OnDisableAsync() {
        var addonLifecycle = IAddonLifecycle.Get();
        addonLifecycle.UnregisterListener(OnCabinetUpdate);
        addonLifecycle.UnregisterListener(OnCabinetFinalize);

        await IFramework.Get().Run(() => {
            unsafe {
                Paint((AddonCabinet*)IGameGui.Get().GetAddonByName(AddonName).Address, protect: false);
            }

            RequestListRebuild();
        });

        fadedRows.Clear();

        if (storeHook is not null) {
            await IFramework.Get().Run(storeHook.Dispose);
            storeHook = null;
        }

        if (configWindow is not null) {
            await configWindow.DisposeAsync();
            configWindow = null;
        }

        config = null;
    }

    private unsafe bool OnStoreCabinetItem(Cabinet* cabinet, uint cabinetId) {
        var row = IDataManager.Get().GetExcelSheet<CabinetSheet>().GetRowOrDefault(cabinetId);
        var itemId = row?.Item.RowId ?? 0u;

        RefreshGearsetItems();

        if (itemId is not 0 && (gearsetItems.Contains(itemId) || gearsetItems.Contains(itemId + HighQualityOffset))) {
            IChatGui.Get().PrintError($"{row!.Value.Item.Value.Name.ExtractText()} is used by a gear set and was not stored in the armoire.");
            IPluginLog.Get().Info($"Blocked storing item {itemId} (cabinet row {cabinetId}): a gear set uses it");
            return false;
        }

        return storeHook!.Original(cabinet, cabinetId);
    }

    private unsafe void OnCabinetUpdate(AddonEvent type, AddonArgs args)
        => Paint((AddonCabinet*)args.Addon.Address, protect: true);

    /// <summary>The renderers die with the addon, so the recorded nodes would be stale pointers.</summary>
    private void OnCabinetFinalize(AddonEvent type, AddonArgs args) => fadedRows.Clear();

    private unsafe void Paint(AddonCabinet* addon, bool protect) {
        if (addon is null || addon->ItemList is null) return;

        var agent = AgentCabinet.Instance();
        if (protect && (agent is null || agent->OpenMode != StoreMode)) return;

        if (protect) RefreshGearsetItems();

        var drop = protect && config is { HideRows: true };
        var list = addon->ItemList;
        var slots = addon->ItemSlots;

        for (var index = 0; index < Math.Min(list->ListLength, slots.Length); index++) {
            var blocked = protect && gearsetItems.Contains(ItemIdOf(ref slots[index]));
            SetOpacity(list->GetItemRenderer(index), blocked && !drop ? DimmedAlpha : null);
        }

        if (drop) Drop(list, slots);
    }

    /// <summary>
    /// Closes the gaps by moving the kept slots down over the protected ones and shrinking the list to
    /// match, so the entries are gone rather than blank. The row contents and the click that picks an
    /// item to store both read these same slots, so they stay in agreement.
    /// </summary>
    private unsafe void Drop(AtkComponentList* list, Span<AddonCabinet.ItemSlot> slots) {
        var count = Math.Min(list->ListLength, slots.Length);
        var kept = 0;

        for (var index = 0; index < count; index++) {
            if (gearsetItems.Contains(ItemIdOf(ref slots[index]))) continue;
            if (kept != index) CopySlot(ref slots[index], ref slots[kept]);
            kept++;
        }

        // Compacted on an earlier frame, so the list is left alone and keeps its scroll position.
        if (kept == count) return;

        var slotCount = AtkStage.Instance()->GetNumberArrayData()[SlotCountArray];
        if (slotCount is not null) slotCount->IntArray[0] = kept;

        list->SetItemCount((short)kept);
    }

    /// <summary>The name owns its buffer, so it is copied through the string itself rather than moved.</summary>
    private static unsafe void CopySlot(ref AddonCabinet.ItemSlot from, ref AddonCabinet.ItemSlot to) {
        to.Name.SetString(from.Name.StringPtr);
        to.Unk68 = from.Unk68;
        to.InventorySlotIndex = from.InventorySlotIndex;
        to.InventoryContainerType = from.InventoryContainerType;
        to.ItemsArrayIndex = from.ItemsArrayIndex;
        to.ConditionNormalized = from.ConditionNormalized;
    }

    /// <summary>Makes the agent rebuild the full item list, undoing a compaction.</summary>
    private static unsafe void RequestListRebuild() {
        var agent = AgentCabinet.Instance();
        if (agent is not null) agent->PendingUpdate = true;
    }

    private unsafe void SetOpacity(AtkComponentListItemRenderer* renderer, byte? alpha) {
        if (renderer is null) return;

        var owner = renderer->AtkComponentButton.AtkComponentBase.OwnerNode;
        if (owner is null) return;

        if (alpha is { } value) {
            fadedRows.TryAdd((nint)owner, owner->AtkResNode.Color.A);
            owner->AtkResNode.Color.A = value;
        }
        else if (fadedRows.Remove((nint)owner, out var original)) {
            owner->AtkResNode.Color.A = original;
        }
    }

    private void RefreshGearsetItems() {
        gearsetItems.Clear();

        unsafe {
            var module = RaptureGearsetModule.Instance();
            if (module is null) return;

            foreach (ref var gearset in module->Entries) {
                if (!gearset.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;

                foreach (ref var item in gearset.Items) {
                    if (item.ItemId is not 0) gearsetItems.Add(item.ItemId);
                }
            }
        }
    }

    private static unsafe uint ItemIdOf(ref AddonCabinet.ItemSlot slot) {
        var manager = InventoryManager.Instance();
        if (manager is null) return 0;

        var container = manager->GetInventoryContainer((InventoryType)slot.InventoryContainerType);
        if (container is null) return 0;

        var item = container->GetInventorySlot((int)slot.InventorySlotIndex);
        if (item is null) return 0;

        return item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? item->ItemId + HighQualityOffset : item->ItemId;
    }
}

public class ProtectGearSetsConfig : TweakConfig<ProtectGearSetsConfig> {
    public bool HideRows;
}
