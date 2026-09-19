using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using KamiToolKit.Nodes;
using TysiTweaks.Ui;

namespace TysiTweaks.Tweaks;

// Ported from HaselTweaks' Always Face Camera.
public class AlwaysFaceCamera : Tweak {
    public override string DisplayName => "Always Face Camera";

    public override string Description =>
        "Makes your character look at the camera whenever the camera is in front of them.\n" +
        "Stays off in combat and while you have a target.";

    private const ulong NoTarget = 0xE0000000UL;

    // The game's own zoom limits.
    private const float MinZoom = 1.5f;
    private const float MaxZoom = 20.0f;

    private AlwaysFaceCameraConfig? config;
    private TweakConfigWindow? configWindow;

    public override Task OnEnableAsync() {
        config = AlwaysFaceCameraConfig.Load();

        configWindow = new TweakConfigWindow {
            InternalName = "TysiFaceCameraConfig",
            Title = DisplayName,
            Size = new Vector2(400.0f, 160.0f),
            BuildNodes = () => [
                new CheckboxNode {
                    Height = 24.0f,
                    String = "Only when the camera is zoomed in closer than:",
                    IsChecked = config.OnlyWhenZoomedIn,
                    OnClick = value => {
                        config.OnlyWhenZoomedIn = value;
                        config.Save();
                    },
                },
                new FloatSliderNode {
                    Height = 20.0f,
                    Min = MinZoom,
                    Max = MaxZoom,
                    Step = 0.5f,
                    Value = config.MaxCameraDistance,
                    OnValueChanged = value => {
                        config.MaxCameraDistance = value;
                        config.Save();
                    },
                },
            ],
        };

        OpenConfigAction = configWindow.Toggle;

        IFramework.Get().Update += OnUpdate;

        return Task.CompletedTask;
    }

    public override async Task OnDisableAsync() {
        IFramework.Get().Update -= OnUpdate;
        await IFramework.Get().Run(() => SetFaceCamera(false));

        if (configWindow is not null) {
            await configWindow.DisposeAsync();
            configWindow = null;
        }

        config = null;
    }

    private unsafe void OnUpdate(IFramework framework) {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer is null || localPlayer->InCombat || localPlayer->GetTargetId() != NoTarget) {
            SetFaceCamera(false);
            return;
        }

        var cameraManager = CameraManager.Instance();
        if (cameraManager is null || cameraManager->Camera is null || cameraManager->ActiveCameraIndex is not 0) {
            SetFaceCamera(false);
            return;
        }

        var camera = cameraManager->Camera;

        if (config is { OnlyWhenZoomedIn: true } && camera->Distance > config.MaxCameraDistance) {
            SetFaceCamera(false);
            return;
        }

        var cameraPosition = camera->SceneCamera.Position;
        var facing = new Vector3(MathF.Sin(localPlayer->Rotation), 0.0f, MathF.Cos(localPlayer->Rotation));
        var towardsCamera = Vector3.Normalize(cameraPosition - localPlayer->Position);

        if (Vector3.Dot(facing, towardsCamera) <= 0.0f) {
            SetFaceCamera(false);
            return;
        }

        SetFaceCamera(true);
        localPlayer->LookAt.CameraVector = cameraPosition;
    }

    private static unsafe void SetFaceCamera(bool enabled) {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer is null) return;

        if (enabled) {
            localPlayer->LookAt.FaceCameraFlag |= 1;
        }
        else {
            localPlayer->LookAt.FaceCameraFlag &= 0xFE;
        }
    }
}

public class AlwaysFaceCameraConfig : TweakConfig<AlwaysFaceCameraConfig> {
    public bool OnlyWhenZoomedIn;
    public float MaxCameraDistance = 6.0f;
}
