using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace TysiTweaks.Ui;

public class TweakBrowser : NativeAddon {
    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        base.OnSetup(addon, atkValueSpan);

        var scrollingNode = new ScrollingNode<VerticalListNode> {
            Position = ContentStartPosition,
            Size = ContentSize,
            AutoHideScrollBar = true,
            ContentNode = {
                FitContents = true,
                FitWidth = true,
                ItemSpacing = 4.0f,
            },
        };

        foreach (var tweak in TweakManager.Tweaks) {
            scrollingNode.ContentNode.AddNode(BuildRow(tweak));
        }

        scrollingNode.AttachNode(this);
        scrollingNode.RecalculateSizes();
    }

    private NodeBase BuildRow(Tweak tweak) {
        var settingsButton = new TextButtonNode {
            Height = 24.0f,
            Width = 90.0f,
            String = "Settings",
            IsEnabled = tweak.OpenConfigAction is not null,
            OnClick = () => tweak.OpenConfigAction?.Invoke(),
        };

        var toggle = new CheckboxNode {
            Height = 24.0f,
            String = tweak.DisplayName,
            Width = ContentSize.X - settingsButton.Width - 24.0f,
            TextTooltip = tweak.Description,
            IsChecked = tweak.IsRunning,
        };

        // Writing IsChecked re-fires OnClick, so the write-back after a toggle has to be muted.
        var suppress = false;

        toggle.OnClick = _ => {
            if (suppress) return;

            Task.Run(async () => {
                await TweakManager.ToggleAsync(tweak);

                await IFramework.Get().Run(() => {
                    suppress = true;
                    toggle.IsChecked = tweak.IsRunning;
                    suppress = false;

                    settingsButton.IsEnabled = tweak.OpenConfigAction is not null;
                });
            });
        };

        return new HorizontalListNode {
            Height = 28.0f,
            ItemSpacing = 4.0f,
            InitialNodes = [toggle, settingsButton],
        };
    }
}
