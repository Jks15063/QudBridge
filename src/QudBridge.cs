using XRL;
using XRL.World;

namespace QudBridge
{
    // Hook points (from ~/qud-src):
    //  - [ModSensitiveCacheInit]: run by ModManager.ResetModSensitiveStaticCaches (XRL/ModManager.cs:340),
    //    called once after mods are built (XRL/ModManager.cs:653) and again on config hotload (XRL.Core/XRLCore.cs:577).
    //  - [CallAfterGameLoaded]: run by ModManager.CallAfterGameLoaded (XRL/ModManager.cs:374),
    //    called from XRLGame.LoadGame (XRL/XRLGame.cs:1954). Saved-game loads only, not new games.
    [HasModSensitiveStaticCache]
    // Only Player.log lines at load: an in-game message would show up in every request's `messages`
    // (the version is on GET /health).
    [HasCallAfterGameLoaded]
    public static class QudBridgeMod
    {
        // From manifest.json via ModManager.GetMod(Assembly) (XRL/ModManager.cs:836).
        public static readonly string Version =
            ModManager.GetMod(typeof(QudBridgeMod).Assembly)?.Manifest?.Version.ToString() ?? "unknown";

        [ModSensitiveCacheInit]
        public static void OnModsLoaded()
        {
            MetricsManager.LogInfo("QudBridge " + Version + " loaded");
            ActionHandler.RegisterHooks();
            BridgeServer.Start();
        }

        [CallAfterGameLoaded]
        public static void OnGameLoaded()
        {
            MetricsManager.LogInfo("QudBridge: save loaded");
        }
    }

    // New-game path: QudGameBootModule instantiates every [PlayerMutator] type and calls mutate()
    // on the freshly built player (XRL.CharacterBuilds.Qud/QudGameBootModule.cs:303).
    [PlayerMutator]
    public class QudBridgePlayerMutator : IPlayerMutator
    {
        public void mutate(GameObject player)
        {
            MetricsManager.LogInfo("QudBridge: new game started");
        }
    }
}
