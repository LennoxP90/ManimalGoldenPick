using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace Manimal.GoldenPick.GoldenPickSheen
{
    // one SheenInstance per active golden-pick on screen. each owns a per-pick material clone
    // (different picks can carry different colors simultaneously) + a cube proxy parented to
    // the weapon root (its transform IS the projector volume) + per-camera CommandBuffers
    // registered when the camera can see this pick.
    internal class SheenManager : MonoBehaviour
    {
        // shader property ids cached once — same names across all material clones
        private static readonly int SheenColorId    = Shader.PropertyToID("_SheenColor");
        private static readonly int ScrollSpeedId   = Shader.PropertyToID("_ScrollSpeed");
        private static readonly int ScrollDelayId   = Shader.PropertyToID("_ScrollDelay");
        private static readonly int BandSharpnessId = Shader.PropertyToID("_BandSharpness");
        private static readonly int FresnelPowerId  = Shader.PropertyToID("_FresnelPower");
        private static readonly int IntensityId     = Shader.PropertyToID("_Intensity");
        private static readonly int OpacityId       = Shader.PropertyToID("_Opacity");
        private static readonly int HueCycleSpeedId = Shader.PropertyToID("_HueCycleSpeed");

        // emissive pass uses an MPB so we can share one MPB across all instances per-frame
        private static readonly MaterialPropertyBlock _emissMpb = new MaterialPropertyBlock();

        // primitive cube — the projector volume DrawMesh'd in the command buffer
        private Mesh _cube;

        // shader tuning baked in — lift into BepInEx config if you ever want field tuning
        private const float ScrollSpeed       = 0.18f;
        private const float ScrollDelay       = 0.0f;
        private const float BandSharpness     = 18.0f;
        private const float FresnelPower      = 4.0f;
        private const float Intensity         = 4.0f;
        private const float Opacity           = 0.08f;
        private const float HueCycleSpeed     = 0.0f;
        private const float EmissiveIntensity = 0.04f;

        // in-game / menu cube transform — hardcoded after live tuning landed these values on
        // the pick mesh. preview path uses its own config (Plugin.PreviewCube*) because the
        // preview prefab parents the mesh under a different rig root.
        private static readonly Vector3 IngameCubePos  = new Vector3(0f,    -0.07f, 0.11f);
        private static readonly Vector3 IngameCubeRot  = new Vector3(0f,    180f,   0f);
        private static readonly Vector3 IngameCubeSize = new Vector3(0.1f,  0.25f,  0.5f);

        private class SheenInstance
        {
            public string     Key;          // composite dictionary key: "<itemId>@<scope>"
            public string     OwnerItemId;  // raw item id for matching against MainPlayer.HandsController.Item.Id
            public string     Scope;        // "ingame" / "menu" / "preview"
            public GameObject CubeProxy;
            public Material   Mat;          // Instantiate(baseMat) — unique per pick
            public Color      KitColor;

            public readonly Dictionary<Camera, CommandBuffer> CmdBuffers   = new Dictionary<Camera, CommandBuffer>();
            public readonly Dictionary<Camera, CommandBuffer> EmissBuffers = new Dictionary<Camera, CommandBuffer>();
        }

        // composite key "{itemId}@{scope}" — lets one pick have simultaneous instances across
        // in-game / menu / preview contexts. without this, the menu hook would clobber the
        // in-game instance and tear down its cube proxy.
        public const string ScopeIngame  = "ingame";
        public const string ScopeMenu    = "menu";
        public const string ScopePreview = "preview";

        public static string MakeKey(string itemId, string scope) => $"{itemId}@{scope}";

        private readonly Dictionary<string, SheenInstance> _instances = new Dictionary<string, SheenInstance>();

        // weapon inspect-preview cams + the pick id each is showing. id mapping keeps the
        // preview from leaking sheen from other equipped picks.
        public readonly HashSet<Camera> WeaponPreviewCameras = new HashSet<Camera>();
        public readonly Dictionary<Camera, string> PreviewCameraItemId = new Dictionary<Camera, string>();

        // picks currently on the loading-screen / menu character. the menu cam draws everything
        // equipped — we only render sheen for picks we explicitly registered.
        public readonly HashSet<string> MenuModeItemIds = new HashSet<string>();

        // in-game player-model preview cams (inventory / character / post-raid summary).
        // dynamically created as children of PlayerModelView, registered by the open/close hooks.
        // rendered the same as MainMenuCamera — both draw menu-scoped sheen.
        public readonly HashSet<Camera> PlayerModelViewCameras = new HashSet<Camera>();

        private void Awake()
        {
            _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        }

        private void OnEnable()
        {
            Camera.onPreCull   += OnPreCullCallback;
            Camera.onPreRender += OnPreRenderCallback;
        }

        private void OnDisable()
        {
            Camera.onPreCull   -= OnPreCullCallback;
            Camera.onPreRender -= OnPreRenderCallback;
            foreach (var inst in _instances.Values) DestroyInstance(inst);
            _instances.Clear();
        }

        // callers must pass the scope they care about — in-game / menu / preview coexist.
        public bool HasInstance(string itemId, string scope = ScopeIngame) =>
            !string.IsNullOrEmpty(itemId) && _instances.ContainsKey(MakeKey(itemId, scope));

        public void Activate(string itemId, Transform weaponRoot, GameObject meshGo, Color kitColor, Material baseMat, string scope = ScopeIngame)
        {
            if (string.IsNullOrEmpty(itemId) || weaponRoot == null || baseMat == null) return;
            var key = MakeKey(itemId, scope);

            // replace any prior instance in THIS scope only — different scopes for the same
            // id are supposed to coexist
            if (_instances.TryGetValue(key, out var old)) DestroyInstance(old);

            var inst = new SheenInstance
            {
                Key         = key,
                OwnerItemId = itemId,
                Scope       = scope,
                KitColor    = kitColor,
                // clone so _SheenColor writes here don't bleed to other picks
                Mat         = Instantiate(baseMat),
            };

            inst.CubeProxy = new GameObject("GoldenSheenCube_" + key);
            inst.CubeProxy.transform.SetParent(weaponRoot, false);
            _instances[key] = inst;

            // one-shot: the sheen shader's Stencil Ref=2 silently rejects everything if the
            // weapon's material doesn't write 2 to the stencil. log what we find so a "no
            // visible sheen" symptom is diagnosable.
            DiagnoseStencil(weaponRoot);
        }

        private static readonly int StencilTypePropId = Shader.PropertyToID("_StencilType");
        private void DiagnoseStencil(Transform weaponRoot)
        {
            try
            {
                int rendererCount = 0, withProp = 0;
                string sampleShader = null;
                float sampleStencilVal = -1f;
                foreach (var r in weaponRoot.GetComponentsInChildren<Renderer>(true))
                {
                    rendererCount++;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        if (sampleShader == null) sampleShader = m.shader != null ? m.shader.name : "(null)";
                        if (m.HasProperty(StencilTypePropId))
                        {
                            withProp++;
                            if (sampleStencilVal < 0f) sampleStencilVal = m.GetFloat(StencilTypePropId);
                        }
                    }
                }
                Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] stencil check: {rendererCount} renderer(s), {withProp} mat(s) have _StencilType, sample stencil={sampleStencilVal} (need 2 for sheen to draw), sample shader='{sampleShader}'");
            }
            catch (System.Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick/Sheen] stencil diag failed: {e.Message}"); }
        }

        public void Deactivate(string itemId, string scope = ScopeIngame)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            var key = MakeKey(itemId, scope);
            if (_instances.TryGetValue(key, out var inst))
            {
                DestroyInstance(inst);
                _instances.Remove(key);
            }
        }

        // wipe a closing preview cam's command buffers from every instance
        public void RemoveCamera(Camera cam)
        {
            foreach (var inst in _instances.Values) RemoveCameraFromInstance(inst, cam);
        }

        // explicitly-registered cams (menu, preview, PlayerModelView) bypass the deferred
        // check — preview RT cams report Forward but actually composite into deferred, and
        // gating them on renderingPath rejects them before our allow-list runs.
        private bool CanSeeSheen(Camera cam)
        {
            if (!cam || !cam.isActiveAndEnabled) return false;
            if (cam.name == "MainMenuCamera") return true;
            if (PlayerModelViewCameras.Contains(cam)) return true;
            if (WeaponPreviewCameras.Contains(cam)) return true;
            if (cam.renderingPath != RenderingPath.DeferredShading) return false;
            try { if (CameraClass.Instance?.Camera == cam) return true; } catch { }
            if (cam.CompareTag("OpticCamera")) return true;
            return false;
        }

        // MainMenuCamera (loading screen) and PlayerModelView cams both render menu-scoped
        // instances — unified so OnPreCull/OnPreRender don't have to branch.
        private bool IsMenuRenderingCam(Camera cam) =>
            cam.name == "MainMenuCamera" || PlayerModelViewCameras.Contains(cam);

        private void OnPreCullCallback(Camera cam)
        {
            if (!CanSeeSheen(cam)) return;

            bool isPreview = WeaponPreviewCameras.Contains(cam);
            bool isMenuCam = IsMenuRenderingCam(cam);

            string previewId = null;
            if (isPreview) PreviewCameraItemId.TryGetValue(cam, out previewId);

            foreach (var inst in _instances.Values)
            {
                if (inst.CubeProxy == null || inst.Mat == null) continue;
                // each cam type renders only its scope's instances
                if (isPreview && inst.Scope != ScopePreview) continue;
                if (isMenuCam && inst.Scope != ScopeMenu) continue;
                if (!isPreview && !isMenuCam && inst.Scope != ScopeIngame) continue;
                // preview cam → only the specific previewed item; menu cam → registered ids only
                if (isPreview && previewId != null && inst.OwnerItemId != previewId) continue;
                if (isMenuCam && !MenuModeItemIds.Contains(inst.Key)) continue;

                if (!inst.CmdBuffers.ContainsKey(cam))
                {
                    var buf = new CommandBuffer { name = "GoldenSheen_" + inst.Key };
                    cam.AddCommandBuffer(CameraEvent.BeforeLighting, buf);
                    inst.CmdBuffers[cam] = buf;
                }
                // emissive pass — preview cams are well-lit RTs without that buffer, skip them
                if (!isPreview && !inst.EmissBuffers.ContainsKey(cam))
                {
                    var eBuf = new CommandBuffer { name = "GoldenSheenEmiss_" + inst.Key };
                    cam.AddCommandBuffer(CameraEvent.AfterLighting, eBuf);
                    inst.EmissBuffers[cam] = eBuf;
                }
            }
        }

        private void OnPreRenderCallback(Camera cam)
        {
            if (!CanSeeSheen(cam)) return;
            if (_cube == null) return;

            bool isPreview = WeaponPreviewCameras.Contains(cam);
            bool isMenuCam = IsMenuRenderingCam(cam);
            string previewId = null;
            if (isPreview) PreviewCameraItemId.TryGetValue(cam, out previewId);

            // main game cam: only draw sheen for whatever pick the player is currently holding.
            // without this, two equipped picks both project through the FP weapon model → two
            // colors at once.
            string activeItemId = null;
            if (!isPreview && !isMenuCam)
            {
                try { activeItemId = Singleton<GameWorld>.Instance?.MainPlayer?.HandsController?.Item?.Id; }
                catch { /* draw all */ }
            }

            foreach (var inst in _instances.Values)
            {
                if (inst.CubeProxy == null || inst.Mat == null) continue;
                if (!inst.CmdBuffers.TryGetValue(cam, out var buf)) continue;

                // scope gating mirrors OnPreCull
                bool wrongScope = (isPreview && inst.Scope != ScopePreview)
                                || (isMenuCam && inst.Scope != ScopeMenu)
                                || (!isPreview && !isMenuCam && inst.Scope != ScopeIngame);
                bool wrongPreviewKey = isPreview && previewId != null && inst.OwnerItemId != previewId;
                bool notMenuRegistered = isMenuCam && !MenuModeItemIds.Contains(inst.Key);
                // main-cam matches by OwnerItemId (raw) — game world only knows the bare item
                // id from MainPlayer.HandsController.Item
                bool notHeldByPlayer = !isPreview && !isMenuCam && activeItemId != null
                                       && inst.OwnerItemId != activeItemId;

                if (wrongScope || wrongPreviewKey || notMenuRegistered || notHeldByPlayer)
                {
                    buf.Clear();
                    if (inst.EmissBuffers.TryGetValue(cam, out var eb0)) eb0.Clear();
                    continue;
                }

                // push this pick's kit color into its own cloned material
                inst.Mat.SetColor(SheenColorId,    inst.KitColor);
                inst.Mat.SetFloat(ScrollSpeedId,   ScrollSpeed);
                inst.Mat.SetFloat(ScrollDelayId,   ScrollDelay);
                inst.Mat.SetFloat(BandSharpnessId, BandSharpness);
                inst.Mat.SetFloat(FresnelPowerId,  FresnelPower);
                inst.Mat.SetFloat(IntensityId,     Intensity);
                inst.Mat.SetFloat(OpacityId,       Opacity);
                inst.Mat.SetFloat(HueCycleSpeedId, HueCycleSpeed);

                // cube transform — in-game/menu hardcoded from live tuning, preview gets its
                // own config because preview prefabs use a different rig root
                Vector3 pos, rot, size;
                if (inst.Scope == ScopePreview)
                {
                    pos  = new Vector3(Plugin.PreviewCubePosX.Value,  Plugin.PreviewCubePosY.Value,  Plugin.PreviewCubePosZ.Value);
                    rot  = new Vector3(Plugin.PreviewCubeRotX.Value,  Plugin.PreviewCubeRotY.Value,  Plugin.PreviewCubeRotZ.Value);
                    size = new Vector3(Plugin.PreviewCubeSizeX.Value, Plugin.PreviewCubeSizeY.Value, Plugin.PreviewCubeSizeZ.Value);
                }
                else
                {
                    pos  = IngameCubePos;
                    rot  = IngameCubeRot;
                    size = IngameCubeSize;
                }
                inst.CubeProxy.transform.localPosition = pos;
                inst.CubeProxy.transform.localRotation = Quaternion.Euler(rot);
                inst.CubeProxy.transform.localScale = Vector3.one; // size baked into matrix below

                var scaleMatrix = Matrix4x4.Scale(size);
                var matrix = inst.CubeProxy.transform.localToWorldMatrix * scaleMatrix;

                // pass 1: BeforeLighting → GBuffer0 (albedo tint, stencil-masked to the weapon)
                buf.Clear();
                buf.SetRenderTarget(BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.CameraTarget);
                buf.DrawMesh(_cube, matrix, inst.Mat, 0, 0);

                // pass 2: AfterLighting → CameraTarget (self-illumination glow)
                if (inst.EmissBuffers.TryGetValue(cam, out var eBuf))
                {
                    eBuf.Clear();
                    eBuf.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);
                    _emissMpb.SetFloat(OpacityId, EmissiveIntensity);
                    eBuf.DrawMesh(_cube, matrix, inst.Mat, 0, 0, _emissMpb);
                }
            }
        }

        private void CleanupInstance(SheenInstance inst)
        {
            foreach (var kvp in inst.CmdBuffers) if (kvp.Key) kvp.Key.RemoveCommandBuffer(CameraEvent.BeforeLighting, kvp.Value);
            inst.CmdBuffers.Clear();
            foreach (var kvp in inst.EmissBuffers) if (kvp.Key) kvp.Key.RemoveCommandBuffer(CameraEvent.AfterLighting, kvp.Value);
            inst.EmissBuffers.Clear();
        }

        private void RemoveCameraFromInstance(SheenInstance inst, Camera cam)
        {
            if (inst.CmdBuffers.TryGetValue(cam, out var buf))
            {
                if (cam) cam.RemoveCommandBuffer(CameraEvent.BeforeLighting, buf);
                inst.CmdBuffers.Remove(cam);
            }
            if (inst.EmissBuffers.TryGetValue(cam, out var eBuf))
            {
                if (cam) cam.RemoveCommandBuffer(CameraEvent.AfterLighting, eBuf);
                inst.EmissBuffers.Remove(cam);
            }
        }

        private void DestroyInstance(SheenInstance inst)
        {
            CleanupInstance(inst);
            if (inst.CubeProxy != null) { Destroy(inst.CubeProxy); inst.CubeProxy = null; }
            if (inst.Mat       != null) { Destroy(inst.Mat);       inst.Mat       = null; }
        }
    }
}
