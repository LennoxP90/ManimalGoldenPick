using System;
using System.IO;
using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen
{
    // loads the sheen base material from killstreaksheen.bundle (the custom sheen shader + a
    // material wired up in Unity editor with sane defaults). cached after first load.
    //
    // bundle lives in the server bundles tree alongside our other bundles (goldmat, goldreveal
    // etc) — keeps all goldenpick bundles deployed via the server PostBuild target rather than
    // half client / half server. SPT doesnt eager-load it since no item references it, so a
    // direct AssetBundle.LoadFromFile here is fine — same pattern goldmat/goldreveal use.
    internal static class SheenAssets
    {
        private const string BundleRelPath = @"SPT\user\mods\GoldenPickServer\bundles\manimal\killstreaksheen.bundle";

        private static Material _baseMat;
        private static bool _loadTried;

        public static Material GetBaseMaterial()
        {
            if (_baseMat != null || _loadTried) return _baseMat;
            _loadTried = true;
            try
            {
                var bundlePath = Path.Combine(BepInEx.Paths.GameRootPath, BundleRelPath);
                if (!File.Exists(bundlePath))
                {
                    Plugin.LogSource?.LogError($"[GoldenPick/Sheen] bundle not found at {bundlePath}");
                    return null;
                }

                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null) { Plugin.LogSource?.LogError("[GoldenPick/Sheen] AssetBundle.LoadFromFile returned null"); return null; }

                // prefer the material (carries editor-tuned defaults). fall back to building a
                // fresh Material from just the shader if the .mat isnt in the bundle.
                string matPath = null, shaderPath = null;
                foreach (var a in bundle.GetAllAssetNames())
                {
                    if (matPath == null && a.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) matPath = a;
                    if (shaderPath == null && a.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)) shaderPath = a;
                }

                if (matPath != null)
                {
                    _baseMat = bundle.LoadAsset<Material>(matPath);
                    Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] loaded material '{matPath}'");
                }
                if (_baseMat == null && shaderPath != null)
                {
                    var shader = bundle.LoadAsset<Shader>(shaderPath);
                    if (shader != null) { _baseMat = new Material(shader); Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] loaded shader '{shader.name}' (no .mat, built fresh material)"); }
                }
                bundle.Unload(false);

                if (_baseMat == null) Plugin.LogSource?.LogError("[GoldenPick/Sheen] neither material nor shader found in bundle");
                else Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] base material ready: shader='{_baseMat.shader.name}' supported={_baseMat.shader.isSupported}");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] bundle load failed: {e}"); }
            return _baseMat;
        }
    }
}
