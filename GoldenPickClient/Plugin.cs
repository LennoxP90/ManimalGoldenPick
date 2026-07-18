using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Manimal.GoldenPick.Earn;
using Manimal.GoldenPick.GoldenPickSheen.Patches;
using Manimal.GoldenPick.Statue;

namespace Manimal.GoldenPick
{
    // forge-compliant: GUID reverse-domain lowercase, name "Username-ModName".
    // version comes from ModVersion.g.cs (generated from $(ModVersion) in Directory.Build.props).
    [BepInPlugin(PluginGuid, PluginName, ModVersion.Value)]
    [BepInDependency("com.wtt.commonlib")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.manimal.goldenpick";
        public const string PluginName = "Manimal-GoldenPick";

        public static ManualLogSource LogSource;
        // lets static patch contexts (the unbox flow) start coroutines on us
        public static Plugin Instance;

        // --- config ---
        // on-screen size (px) of the reveal effect square that rides the icon. exposed so you
        // can drag it to a size you like in the config manager (it's read live each frame).
        public static ConfigEntry<float> RevealSizePx;

        // in-game/menu sheen cube values are now HARDCODED in SheenManager (dialed in via
        // earlier live-tuning sessions). only the preview-window cube remains tunable —
        // preview-spawned prefabs use a different mesh-transform convention (different rig
        // root) so the cube needs its own offset/scale there.
        public static ConfigEntry<float> PreviewCubePosX, PreviewCubePosY, PreviewCubePosZ;
        public static ConfigEntry<float> PreviewCubeRotX, PreviewCubeRotY, PreviewCubeRotZ;
        public static ConfigEntry<float> PreviewCubeSizeX, PreviewCubeSizeY, PreviewCubeSizeZ;

        private void Awake()
        {
            LogSource = Logger;
            Instance = this;
            LogSource.LogInfo("GoldenPick loaded!");

            RevealSizePx = Config.Bind("Reveal", "RevealSizePx", 800f,
                new ConfigDescription("On-screen size (px) of the reveal effect over the item icon.",
                    new AcceptableValueRange<float>(100f, 1400f)));

            // preview cube — defaults seed from the in-game-hardcoded values, but the preview
            // prefab uses a different mesh-transform root so live-tuning these in the field is
            // how you align the cube with the previewed pick mesh.
            var posRange  = new AcceptableValueRange<float>(-2f, 2f);
            var rotRange  = new AcceptableValueRange<float>(-180f, 180f);
            var sizeRange = new AcceptableValueRange<float>(0.001f, 2f);
            // defaults dialed in via live tuning — these land the cube on the previewed pick
            // in the inspect popup. preview prefab uses a rotated mesh root so the rotation
            // values are non-trivial (91, -13, 18) vs the clean (0, 180, 0) of the in-game rig.
            PreviewCubePosX  = Config.Bind("Sheen Cube (Preview)", "PosX",  0.20633f, new ConfigDescription("Preview cube center X (local)", posRange));
            PreviewCubePosY  = Config.Bind("Sheen Cube (Preview)", "PosY",  0.04694f, new ConfigDescription("Preview cube center Y (local)", posRange));
            PreviewCubePosZ  = Config.Bind("Sheen Cube (Preview)", "PosZ",  0.39169f, new ConfigDescription("Preview cube center Z (local)", posRange));
            PreviewCubeRotX  = Config.Bind("Sheen Cube (Preview)", "RotX",  91.2676f, new ConfigDescription("Preview cube rotation X (Euler degrees)",  rotRange));
            PreviewCubeRotY  = Config.Bind("Sheen Cube (Preview)", "RotY",  -13.521f, new ConfigDescription("Preview cube rotation Y (Euler degrees)",  rotRange));
            PreviewCubeRotZ  = Config.Bind("Sheen Cube (Preview)", "RotZ",  18.5915f, new ConfigDescription("Preview cube rotation Z (Euler degrees)",  rotRange));
            PreviewCubeSizeX = Config.Bind("Sheen Cube (Preview)", "SizeX", 0.25954f, new ConfigDescription("Preview cube size X (projector volume width)",  sizeRange));
            PreviewCubeSizeY = Config.Bind("Sheen Cube (Preview)", "SizeY", 0.51277f, new ConfigDescription("Preview cube size Y (projector volume height)", sizeRange));
            PreviewCubeSizeZ = Config.Bind("Sheen Cube (Preview)", "SizeZ", 0.69708f, new ConfigDescription("Preview cube size Z (projector volume length)", sizeRange));

            // intercept the Sealed Golden Crate unpack → reveal countdown + smart placement
            new Unbox.GoldenCrateUnpackPatch().Enable();

            // arm the gold-statue-on-kill effect (Player.OnPlayerDeadStatic subscription)
            GoldKillHandler.Init();

            // every .Enable() is SafeEnable-wrapped — one broken patch shouldn't take down
            // the whole Awake (we had a regression where a generic-method patch threw IL
            // Compile Error → Unity marked the component unhealthy → Update stopped → toast
            // + relay drain both went silent).
            //
            // WeaponPrefab.OnEnable is the universal in-hands hook — fires on every weapon
            // activation across raid spawn / hideout / manual swap, no matter the weapon type.
            SafeEnable("WeaponPrefabOnEnableSheenPatch",   () => new WeaponPrefabOnEnableSheenPatch().Enable());
            SafeEnable("KnifeControllerDestroySheenPatch", () => new KnifeControllerDestroySheenPatch().Enable());
            SafeEnable("MenuModelSheenPatch",              () => new MenuModelSheenPatch().Enable());
            SafeEnable("MenuModelSheenClearPatch",         () => new MenuModelSheenClearPatch().Enable());
            // PlayerModelView pair — registers the in-game preview cameras (inventory screen,
            // character screen, etc.) so menu-scoped sheen instances render through them.
            // without these, MainMenuCamera alone covers the loading screen but in-game menus
            // would silently render no sheen.
            SafeEnable("PlayerModelViewSheenShowPatch",    () => new PlayerModelViewSheenShowPatch().Enable());
            SafeEnable("PlayerModelViewSheenHidePatch",    () => new PlayerModelViewSheenHidePatch().Enable());
            SafeEnable("WeaponPreviewOpenSheenPatch",      () => new WeaponPreviewOpenSheenPatch().Enable());
            SafeEnable("WeaponPreviewCloseSheenPatch",     () => new WeaponPreviewCloseSheenPatch().Enable());
            SafeEnable("BodyStencilPatch",                 () => new BodyStencilPatch().Enable());
            // display layer — name / description / pick number / colored icon strip. all
            // template-gated first so they're cheap for non-pick items.
            SafeEnable("PickIconCaptionPatch",   () => new PickIconCaptionPatch().Enable());
            SafeEnable("PickIconAutoSizePatch",  () => new PickIconAutoSizePatch().Enable());
            SafeEnable("PickInspectNamePatch",   () => new PickInspectNamePatch().Enable());
            SafeEnable("PickInspectDescPatch",   () => new PickInspectDescPatch().Enable());
            SafeEnable("PickIconStripPatch",     () => new PickIconStripPatch().Enable());
            // raid-end gild teardown — restores the pooled equipment-model renderers we
            // mutated during the raid. fires at GameWorld.Dispose, before EFT recycles the
            // pooled prefabs for the menu char / next raid's bots.
            SafeEnable("GameWorldDisposeRestorePatch", () => new Statue.GameWorldDisposeRestorePatch().Enable());
            // red-rebel extract bypass — a registered golden pick satisfies the red rebel
            // item requirement AND lifts the armor-size restriction on the same extract
            SafeEnable("RedRebelHasItemBypassPatch",     () => new Extract.RedRebelHasItemBypassPatch().Enable());
            SafeEnable("RedRebelEmptyOrSizeBypassPatch", () => new Extract.RedRebelEmptyOrSizeBypassPatch().Enable());
            SafeEnable("RedRebelEmptyBypassPatch",       () => new Extract.RedRebelEmptyBypassPatch().Enable());
        }

        // wrapper so one bad patch logs + moves on instead of taking down the whole Awake
        private static void SafeEnable(string name, System.Action enable)
        {
            try { enable(); }
            catch (System.Exception e)
            {
                LogSource?.LogError($"[GoldenPick] patch '{name}' failed to register, skipping: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            GoldKillHandler.Shutdown();
        }
    }
}
