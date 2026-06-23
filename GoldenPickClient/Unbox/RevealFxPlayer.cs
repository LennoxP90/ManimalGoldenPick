using System;
using System.Collections;
using System.IO;
using System.Linq;
using EFT.UI.DragAndDrop;
using UnityEngine;
using UnityEngine.UI;

namespace Manimal.GoldenPick.Unbox
{
    // plays the TF2-style reveal sequence (a Unity particle prefab in goldrevealseq.bundle)
    // on screen during the unbox. EFT's UI is UGUI and a ParticleSystem can't render inside
    // a canvas, so we use render-to-texture:
    //   - instantiate the prefab FAR off-screen (no EFT camera sees it there)
    //   - a dedicated camera framed on it renders into a SQUARE RenderTexture
    //   - a fixed-size UI RawImage shows that texture, repositioned every frame to sit centered
    //     on the target item's on-screen icon (the crate during the countdown, then the pick
    //     after the pop). blended premultiplied so glows ADD and the pow texts/wood OCCLUDE.
    //
    // the icon is tracked by finding the live ItemView whose Item.Id matches the target. while
    // the item is being dragged EFT spawns a separate DraggedItemView that follows the cursor
    // (the ItemView's own cell rect doesnt move), so we prefer that when present — that's what
    // makes the FX stick to the icon mid-drag and into the scabbard slot.
    internal static class RevealFxPlayer
    {
        private const string BundleRelPath = @"SPT\user\mods\GoldenPickServer\bundles\manimal\goldrevealseq.bundle";

        // generation counter for cancellation: each PlayOnIcon / PlaySound captures the current
        // generation; Cancel() bumps the counter, so all in-flight coroutines see their captured
        // gen != current gen and tear down immediately. monotonic so a Cancel doesn't reach into
        // future plays.
        private static int _currentGen;
        public static void Cancel() { _currentGen++; }

        // a layer for the FX. position isolates it anyway (EFT cams are nowhere near the far
        // origin), so the exact layer mostly doesn't matter — change if it clashes.
        private const int FxLayer = 30;
        private static readonly Vector3 FarOrigin = new Vector3(0f, 10000f, 0f);

        // how much of the effect the camera frames. BIGGER = zoomed out = nothing clipped but
        // particles smaller on screen; SMALLER = bigger/fills more but can clip. tune so the
        // whole firework fits without being cut off.
        private const float FxOrthoSize = 8f;

        // square render target resolution + the on-screen square the RawImage occupies. the
        // firework is bigger than the tiny grid cell, so this is a chunk of screen centered on
        // the icon — tune FxScreenSizePx if it reads too big/small over the cell.
        private const int FxRenderSize = 512;
        private const float FxScreenSizePx = 400f;

        // how often we re-scan for the item's ItemView. scanning every frame tanks fps, so we
        // resolve on this throttle and read the cached view's cheap live rect in between.
        private const float ScanInterval = 0.2f;

        // live on-screen size — from the config slider, falling back to the const pre-Awake.
        private static float CurrentSizePx =>
            Plugin.RevealSizePx != null ? Plugin.RevealSizePx.Value : FxScreenSizePx;

        // flip true to log the tracked icon position (~1/sec) so you can sanity-check alignment.
        // tells you whether we found a live view, whether it's the drag ghost, and where it
        // landed vs the actual cursor. turn off once it looks right.
        private const bool DebugTrack = false;

        // reused so the per-frame rect math doesnt allocate a Vector3[4] every frame (that GC
        // churn was part of the fps cost). single-threaded coroutine, so one shared buffer is ok.
        private static readonly Vector3[] _corners = new Vector3[4];

        private static GameObject _prefab;
        private static bool _loadTried;

        private const string SoundBundleRelPath = @"SPT\user\mods\GoldenPickServer\bundles\manimal\goldrevealsound.bundle";
        private static GameObject _soundPrefab;
        private static AudioClip _soundClip;
        private static bool _soundLoadTried;

        // load the reveal prefab from the deployed bundle (once, cached). goldrevealseq.bundle
        // has no dependencyKeys in bundles.json, so a direct AssetBundle load is fine — SPT
        // doesn't eager-load it since no item references it.
        private static GameObject LoadPrefab()
        {
            if (_loadTried) return _prefab;
            _loadTried = true;
            try
            {
                var path = Path.Combine(BepInEx.Paths.GameRootPath, BundleRelPath);
                if (!File.Exists(path))
                {
                    Plugin.LogSource?.LogError($"[GoldenPick] reveal bundle not found at {path}");
                    return null;
                }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Plugin.LogSource?.LogError("[GoldenPick] AssetBundle.LoadFromFile returned null"); return null; }
                _prefab = bundle.LoadAllAssets<GameObject>().FirstOrDefault();
                if (_prefab == null) Plugin.LogSource?.LogError("[GoldenPick] no GameObject in reveal bundle");
                else Plugin.LogSource?.LogInfo($"[GoldenPick] reveal prefab loaded: {_prefab.name}");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick] reveal bundle load failed: {e}"); }
            return _prefab;
        }

        // play the reveal positioned on a moving icon. getTargetId returns the Item.Id to track
        // each frame — the caller flips it from the crate's id to the pick's id at the pop, so
        // the FX hands off from box to pick. maxSeconds is a SAFETY CAP — it actually lingers
        // until every particle system has finished playing.
        public static void PlayOnIcon(Func<string> getTargetId, float maxSeconds)
        {
            var prefab = LoadPrefab();
            if (prefab == null) return;
            Plugin.Instance.StartCoroutine(Run(prefab, getTargetId, maxSeconds));
        }

        private static IEnumerator Run(GameObject prefab, Func<string> getTargetId, float maxSeconds)
        {
            // capture gen at start — if Cancel() bumps the counter, our captured gen no longer
            // matches and the per-frame check tears the FX down on the next iteration.
            int myGen = _currentGen;

            // 1) the effect, far away on the FX layer
            var fx = UnityEngine.Object.Instantiate(prefab, FarOrigin, Quaternion.identity);
            SetLayer(fx, FxLayer);
            var systems = fx.GetComponentsInChildren<ParticleSystem>(true);

            // 2) a camera that renders ONLY the FX layer into a SQUARE texture (square so it
            //    isn't distorted when shown in a square on-screen rect)
            var rt = new RenderTexture(FxRenderSize, FxRenderSize, 16, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("GoldenRevealFxCam");
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = FarOrigin + new Vector3(0f, 0f, -12f);
            cam.transform.LookAt(FarOrigin);
            cam.orthographic = true;
            cam.orthographicSize = FxOrthoSize;
            cam.aspect = 1f;
            cam.cullingMask = 1 << FxLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            // keep the cleared alpha at 0 — EFT renders in HDR, and the HDR/MSAA resolve
            // clobbers the camera's alpha to 1, which is what showed as an opaque BLACK box.
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 60f;
            cam.targetTexture = rt;

            // 3) a top overlay canvas + a FIXED-SIZE RawImage we move onto the icon each frame.
            //    pivot/anchor at (0,0) so anchoredPosition is screen pixels from bottom-left,
            //    matching what WorldToScreenPoint gives us.
            var canvasGo = new GameObject("GoldenRevealFxCanvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var rawGo = new GameObject("GoldenRevealFxImage");
            rawGo.transform.SetParent(canvasGo.transform, false);
            var raw = rawGo.AddComponent<RawImage>();
            raw.texture = rt;
            raw.raycastTarget = false;
            // premultiplied-alpha display (Blend One OneMinusSrcAlpha): the additive glows ADD
            // over the inventory, while the alpha-blended pow texts/smoke OCCLUDE it (solid).
            // works because the particles render over the transparent-BLACK clear, which leaves
            // the texture premultiplied (color already × coverage).
            raw.material = BuildOverlayMaterial();
            // hidden until we've located the icon — otherwise it flashes at the screen-center
            // start position for a frame before the first resolve snaps it onto the crate.
            raw.enabled = false;
            var rect = raw.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(CurrentSizePx, CurrentSizePx);

            // fallback only — used if the icon is never found (item gone); never shown until then
            Vector2 lastPos = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            rect.anchoredPosition = lastPos;

            // 4) follow the icon while the particles play (capped), then tear down. the view is
            //    resolved on a throttle (ScanInterval) — the costly part — while the cheap rect
            //    read runs every frame off the cached view, so drag tracking stays smooth.
            yield return null;
            float t = 0f;
            float nextLog = 0f;
            float scanTimer = 0f;
            ItemView tracked = null;
            int candidates = 0;
            while (t < maxSeconds && systems.Any(ps => ps != null && ps.IsAlive(true)))
            {
                // cancellation: bail out immediately if anyone called RevealFxPlayer.Cancel()
                // since we started. teardown happens in the post-loop block as usual.
                if (_currentGen != myGen) break;

                var id = getTargetId?.Invoke();

                // re-resolve only on the throttle, or when the cached view went stale/away
                scanTimer -= Time.deltaTime;
                if (scanTimer <= 0f || !IsViewValid(tracked, id))
                {
                    tracked = FindBestView(id, out candidates);
                    scanTimer = ScanInterval;
                }

                // keep the last good position when the item momentarily has no live view
                // (e.g. the split second between the crate being consumed and the pick landing)
                Vector2 pos = default;
                bool dragged = false;
                bool found = tracked != null && GetViewScreenPos(tracked, out pos, out dragged);
                if (found) lastPos = pos;
                rect.anchoredPosition = lastPos;
                rect.sizeDelta = new Vector2(CurrentSizePx, CurrentSizePx);
                // reveal only once we've positioned it on the icon (kills the center-flash). once
                // shown it stays shown — it holds last position through brief no-view gaps.
                if (found && !raw.enabled) raw.enabled = true;

                if (DebugTrack && t >= nextLog)
                {
                    nextLog = t + 1f;
                    // probe RT alpha only during the early window (when boing/crash/boot show) —
                    // ReadPixels is a GPU stall, so keep it to a handful of reads.
                    string alpha = t < 5f ? $" rtAlpha={ProbeMaxAlpha(rt):0.00}" : "";
                    Plugin.LogSource?.LogInfo(found
                        ? $"[GoldenPick] track id={id} {(dragged ? "DRAG" : "cell")} cand={candidates} sz={CurrentSizePx:0} -> {lastPos} (cursor {(Vector2)Input.mousePosition}){alpha}"
                        : $"[GoldenPick] track id={id} NO LIVE VIEW cand={candidates} (holding {lastPos}){alpha}");
                }

                t += Time.deltaTime;
                yield return null;
            }

            if (canvasGo) UnityEngine.Object.Destroy(canvasGo);
            if (camGo) UnityEngine.Object.Destroy(camGo);
            if (fx) UnityEngine.Object.Destroy(fx);
            if (rt) { rt.Release(); UnityEngine.Object.Destroy(rt); }
        }

        // is the cached view still the right, live one for this id?
        private static bool IsViewValid(ItemView v, string id)
        {
            return v != null && v.Item != null && v.Item.Id == id && v.gameObject.activeInHierarchy;
        }

        // pick the best live ItemView for this id (the throttled, costly bit). a view that's
        // being dragged wins outright — that's where you're looking. otherwise the LARGEST
        // on-screen icon wins: the equipped/inventory slot is bigger than the quick-use hotbar
        // thumbnail, so the FX sits on the real slot instead of the little copy.
        // FindObjectsOfType (not ...OfTypeAll) skips assets/inactive — far cheaper.
        private static ItemView FindBestView(string id, out int candidates)
        {
            candidates = 0;
            if (string.IsNullOrEmpty(id)) return null;
            ItemView best = null;
            float bestArea = -1f;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<ItemView>())
            {
                if (v == null || v.Item == null || v.Item.Id != id) continue;
                if (!v.gameObject.activeInHierarchy) continue;
                candidates++;
                if (v.DraggedItemView != null) return v;       // dragging — follow this one
                float area = RectArea(v.RectTransform);
                if (area > bestArea) { bestArea = area; best = v; }
            }
            return best;
        }

        // screen-pixel center of this (already resolved) view's icon. prefers the cursor-
        // following drag ghost — the cell rect stays put during a drag, the ghost moves, so
        // tracking the ghost is what sticks the FX to the icon into the scabbard slot.
        private static bool GetViewScreenPos(ItemView v, out Vector2 screenPos, out bool dragged)
        {
            screenPos = default;
            dragged = v.DraggedItemView != null;
            RectTransform rt = dragged
                ? v.DraggedItemView.transform as RectTransform
                : v.RectTransform;
            if (rt == null) return false;

            // world center of the icon rect → screen pixels. overlay canvases need a null
            // camera; camera-space canvases need their worldCamera — handle both. measure the
            // canvas off the rect we're actually using (the drag ghost lives on a top canvas).
            rt.GetWorldCorners(_corners);
            var center = (_corners[0] + _corners[2]) * 0.5f;
            var canvas = rt.GetComponentInParent<Canvas>();
            Camera uiCam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? canvas.worldCamera : null;
            screenPos = RectTransformUtility.WorldToScreenPoint(uiCam, center);
            return true;
        }

        // cheap on-screen size proxy (world width × height) for comparing icon sizes.
        private static float RectArea(RectTransform rt)
        {
            if (rt == null) return 0f;
            rt.GetWorldCorners(_corners);
            return Vector3.Distance(_corners[0], _corners[3]) * Vector3.Distance(_corners[0], _corners[1]);
        }

        // one-shot diagnostic: reads back the max alpha in the center of the render texture.
        // if this is ~1 while solid text/wood is on screen, the camera IS capturing coverage and
        // the ghosting is downstream. if it stays ~0, the particle materials arent writing alpha
        // to the RT — premultiplied display then ADDS them (ghostly over bright bg, fine over
        // dark) which is exactly the symptom. ReadPixels stalls the GPU, so this only runs a few
        // times under DebugTrack — measure real fps with DebugTrack off.
        private static float ProbeMaxAlpha(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                RenderTexture.active = rt;
                int s = 96;
                int x = Mathf.Max(0, rt.width / 2 - s / 2);
                int y = Mathf.Max(0, rt.height / 2 - s / 2);
                tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(x, y, s, s), 0, 0);
                tex.Apply();
                float maxA = 0f;
                foreach (var p in tex.GetPixels()) if (p.a > maxA) maxA = p.a;
                return maxA;
            }
            catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] alpha probe failed: {e.Message}"); return -1f; }
            finally
            {
                RenderTexture.active = prev;
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        // premultiplied-alpha blend (Blend One OneMinusSrcAlpha) for the on-screen overlay.
        // this is the blend that mixes both kinds of particle correctly: GLOW particles (alpha
        // ~0) ADD over the inventory, while SOLID particles (pow text / wood, alpha ~1) OCCLUDE
        // it. it works because the particles render over the transparent-BLACK clear, leaving
        // the texture premultiplied. needs a premultiply shader — tries the stock legacy ones.
        private static Material BuildOverlayMaterial()
        {
            foreach (var name in new[] { "Particles/Alpha Blended Premultiply", "Legacy Shaders/Particles/Alpha Blended Premultiply" })
            {
                var shader = Shader.Find(name);
                if (shader != null)
                {
                    Plugin.LogSource?.LogInfo($"[GoldenPick] reveal overlay using premultiplied shader '{name}'");
                    return new Material(shader);
                }
            }
            Plugin.LogSource?.LogWarning("[GoldenPick] premultiplied shader not found — overlay will look wrong. we'll ship a premultiply material in the bundle.");
            return null;
        }

        // load + play the reveal audio (its own bundle), in sync with the visuals. handles
        // either a prefab carrying an AudioSource or a bare AudioClip. lingers until the clip
        // finishes (capped).
        public static void PlaySound(float maxSeconds)
        {
            if (!LoadSound()) return;
            Plugin.Instance.StartCoroutine(RunSound(maxSeconds));
        }

        private static bool LoadSound()
        {
            if (_soundLoadTried) return _soundPrefab != null || _soundClip != null;
            _soundLoadTried = true;
            try
            {
                var path = Path.Combine(BepInEx.Paths.GameRootPath, SoundBundleRelPath);
                if (!File.Exists(path)) { Plugin.LogSource?.LogError($"[GoldenPick] sound bundle not found at {path}"); return false; }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Plugin.LogSource?.LogError("[GoldenPick] sound AssetBundle.LoadFromFile returned null"); return false; }
                _soundPrefab = bundle.LoadAllAssets<GameObject>().FirstOrDefault();
                if (_soundPrefab == null) _soundClip = bundle.LoadAllAssets<AudioClip>().FirstOrDefault();
                if (_soundPrefab == null && _soundClip == null) Plugin.LogSource?.LogError("[GoldenPick] no AudioSource prefab / AudioClip in sound bundle");
                else Plugin.LogSource?.LogInfo($"[GoldenPick] reveal sound loaded ({(_soundPrefab != null ? "prefab" : "clip")})");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick] sound bundle load failed: {e}"); }
            return _soundPrefab != null || _soundClip != null;
        }

        private static IEnumerator RunSound(float maxSeconds)
        {
            int myGen = _currentGen;
            GameObject host;
            AudioSource src;
            if (_soundPrefab != null)
            {
                host = UnityEngine.Object.Instantiate(_soundPrefab);
                src = host.GetComponentInChildren<AudioSource>();
                if (src == null) { Plugin.LogSource?.LogWarning("[GoldenPick] sound prefab has no AudioSource"); UnityEngine.Object.Destroy(host); yield break; }
            }
            else
            {
                host = new GameObject("GoldenRevealSound");
                src = host.AddComponent<AudioSource>();
                src.clip = _soundClip;
            }
            src.spatialBlend = 0f;  // 2D — full volume regardless of where the host sits
            src.Play();

            yield return null;
            float t = 0f;
            while (t < maxSeconds && src != null && src.isPlaying)
            {
                if (_currentGen != myGen)
                {
                    // cancelled mid-play: stop audio + drop out so the host destroys below
                    if (src != null) src.Stop();
                    break;
                }
                t += Time.deltaTime;
                yield return null;
            }
            if (host) UnityEngine.Object.Destroy(host);
        }

        private static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayer(child.gameObject, layer);
        }
    }
}
