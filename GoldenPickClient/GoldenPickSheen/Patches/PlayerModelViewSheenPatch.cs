using System;
using System.Reflection;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen.Patches
{
    // when EFT shows a 3D player-model preview (inventory screen, character screen, post-raid
    // summary, etc.), a dedicated camera is added as a CHILD of the PlayerModelView's
    // transform. that camera is NOT the MainMenuCamera and isnt named anything specific — its
    // existence is what we have to detect.
    //
    // method_0 fires when the view is shown; method_1 when its hidden. we walk the children
    // for the Camera component on each, and (de)register it with our SheenManager so the
    // render path knows to draw menu-scoped instances through it.
    //
    // pattern lifted from SPT.WeaponCamoAndStickers' Patch_PlayerModelView_method_0/1 —
    // they solved the same "what camera renders the in-game char preview" question.
    public class PlayerModelViewSheenShowPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PlayerModelView), nameof(PlayerModelView.method_0));

        [PatchPostfix]
        public static void Postfix(PlayerModelView __instance)
        {
            try
            {
                var cam = FindChildCamera(__instance);
                if (cam == null) return;
                var mgr = SheenHost.Manager;
                mgr.PlayerModelViewCameras.Add(cam);
                Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] PlayerModelView opened, registered cam '{cam.name}'");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] PlayerModelView.method_0 postfix failed: {e}"); }
        }

        public static Camera FindChildCamera(PlayerModelView view)
        {
            if (view == null) return null;
            var t = view.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child.TryGetComponent<Camera>(out var cam)) return cam;
            }
            return null;
        }
    }

    public class PlayerModelViewSheenHidePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PlayerModelView), nameof(PlayerModelView.method_1));

        [PatchPrefix]
        public static void Prefix(PlayerModelView __instance)
        {
            try
            {
                var cam = PlayerModelViewSheenShowPatch.FindChildCamera(__instance);
                if (cam == null) return;
                var mgr = SheenHost.ManagerIfExists;
                if (mgr == null) return;
                mgr.PlayerModelViewCameras.Remove(cam);
                mgr.RemoveCamera(cam);  // clear any registered command buffers for this cam
                Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] PlayerModelView closed, unregistered cam '{cam.name}'");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] PlayerModelView.method_1 prefix failed: {e}"); }
        }
    }
}
