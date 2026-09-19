using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KamiToolKit;
using TysiTweaks.Ui;

namespace TysiTweaks;

public sealed class Plugin : IAsyncDalamudPlugin {
    private const string CommandName = "/tysi";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; set; } = null!;

    internal static Configuration Config { get; private set; } = null!;

    private static TweakBrowser browser = null!;

    public async Task LoadAsync(CancellationToken cancellationToken) {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        await KamiToolKitLibrary.InitializeAsync(PluginInterface, "TysiTweaks");

        browser = new TweakBrowser {
            InternalName = "TysiTweaksBrowser",
            Title = "TysiTweaks",
            Size = new Vector2(500.0f, 500.0f),
        };

        ICommandManager.Get().AddHandler(CommandName, new CommandInfo(OnCommand) {
            HelpMessage = "Open the TysiTweaks window.",
        });

        PluginInterface.UiBuilder.OpenConfigUi += browser.Open;
        PluginInterface.UiBuilder.OpenMainUi += browser.Open;

        await TweakManager.LoadAsync();
    }

    public async ValueTask DisposeAsync() {
        ICommandManager.Get().RemoveHandler(CommandName);

        PluginInterface.UiBuilder.OpenConfigUi -= browser.Open;
        PluginInterface.UiBuilder.OpenMainUi -= browser.Open;

        await TweakManager.UnloadAsync();
        await browser.DisposeAsync();
        await KamiToolKitLibrary.DisposeAsync();
    }

    private static void OnCommand(string command, string arguments)
        => browser.Toggle();
}
