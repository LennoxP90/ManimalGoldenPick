using System;
using System.Collections.Generic;
using System.Reflection;
using Diz.Skinning;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen.Patches
{
    // EFT writes stencil value 2 for both hands AND weapon by default. our sheen shader
    // uses Stencil Ref=2, so without this fix it would paint the arms too. on every
    // PlayerBody.SetSkin, rewrite _StencilType=1 on every body-skin material so only the
    // weapon (still 2) catches the sheen.
    public class BodyStencilPatch : ModulePatch
    {
        private static readonly int StencilTypePropId = Shader.PropertyToID("_StencilType");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PlayerBody), nameof(PlayerBody.SetSkin));

        [PatchPostfix]
        public static void Postfix(PlayerBody __instance, KeyValuePair<EBodyModelPart, ResourceKey> part)
        {
            try
            {
                if (!__instance.BodySkins.TryGetValue(part.Key, out var skin) || skin == null) return;
                var lods = Traverse.Create(skin).Field("_lods").GetValue<AbstractSkin[]>();
                if (lods == null) return;
                foreach (var lod in lods)
                {
                    if (lod == null) continue;
                    var smr = lod.SkinnedMeshRenderer;
                    if (smr == null) continue;
                    foreach (var mat in smr.materials)
                        if (mat != null && mat.HasProperty(StencilTypePropId)) mat.SetFloat(StencilTypePropId, 1f);
                }
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] body stencil failed: {e}"); }
        }
    }
}
