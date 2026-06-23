using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.GoldenPick.Statue
{
    // EFT pools equipment-model prefabs across raid → menu → next raid. our gild kept gold
    // sharedMaterials on those pooled renderer instances, so the NEXT raid's bots spawned
    // wearing our gold helmets/rigs/etc. polling for Singleton<GameWorld>.Instance == null
    // in Plugin.Update fires too late — by the time we notice, the menu char has already
    // pulled the gold-tainted pool entries.
    //
    // GameWorld.Dispose is the deterministic teardown — EFT calls it when the raid ends,
    // BEFORE the result screen returns to menu. Prefix-patching it lets us restore the
    // pooled renderers' originals before anyone else touches them.
    public class GameWorldDisposeRestorePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Dispose));

        [PatchPrefix]
        public static void Prefix()
        {
            try
            {
                if (GoldKillHandler.HasGildedRenderers)
                {
                    Plugin.LogSource?.LogInfo("[GoldenPick] GameWorld.Dispose → restoring gilded equipment");
                    GoldKillHandler.RestoreGildedRenderers();
                }
            }
            catch (System.Exception e)
            {
                Plugin.LogSource?.LogError($"[GoldenPick] gild restore at GameWorld.Dispose failed: {e}");
            }
        }
    }
}
