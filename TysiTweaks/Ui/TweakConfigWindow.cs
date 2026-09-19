using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace TysiTweaks.Ui;

// Nodes are destroyed when the window closes, so BuildNodes runs fresh on every open.
public class TweakConfigWindow : NativeAddon {
    public required Func<IEnumerable<NodeBase>> BuildNodes { get; init; }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        base.OnSetup(addon, atkValueSpan);

        new VerticalListNode {
            Position = ContentStartPosition,
            Size = ContentSize,
            FitWidth = true,
            ItemSpacing = 4.0f,
            InitialNodes = [..BuildNodes()],
        }.AttachNode(this);
    }
}
