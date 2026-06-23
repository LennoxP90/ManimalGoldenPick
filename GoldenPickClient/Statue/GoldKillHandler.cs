using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.GoldenPick.Statue
{
    // anyone killed by the golden pick turns gold + a sound plays. subscribes to
    // Player.OnPlayerDeadStatic (hands us the killing weapon's tpl cleanly) instead of
    // Harmony-patching OnDead, which would require reflecting around the protected
    // LastDamageInfo field.
    //
    // the gild replaces every material slot on the body-skin renderers with goldmaterial
    // (from goldmat.bundle). re-applied a few times over ~0.4s because the body's
    // ragdoll/corpse transition resets materials right after OnDead.
    internal static class GoldKillHandler
    {
        private const string MatBundleRelPath = @"SPT\user\mods\GoldenPickServer\bundles\manimal\goldmat.bundle";
        private const string SoundBundleRelPath = @"SPT\user\mods\GoldenPickServer\bundles\manimal\goldkillsound.bundle";

        private static Material _gold;
        private static bool _matLoadTried;
        private static GameObject _soundPrefab;
        private static bool _soundLoadTried;

        private static bool _subscribed;

        // EFT pools equipment-model prefabs (helmet, rig, etc) and reuses them across raid →
        // menu → next raid. our gild assigns to renderer.sharedMaterials which persists on the
        // pooled instance — without restoration, the next time that pooled prefab gets attached
        // to ANY character (e.g. the menu player-scav) it still wears our gold.
        //
        // fix: snapshot the original materials per-renderer at gild time, restore them at raid
        // end (when Singleton<GameWorld>.Instance goes null). list grows during the raid as
        // more corpses are gilded; one sweep at raid end un-gilds them all.
        private static readonly List<(Renderer Renderer, Material[]? Originals)> _gilded = new List<(Renderer, Material[]?)>();

        public static bool HasGildedRenderers => _gilded.Count > 0;

        // subscribe at plugin load; the event fires on the main thread inside OnDead so all the
        // unity work below is safe to do directly.
        public static void Init()
        {
            if (_subscribed) return;
            Player.OnPlayerDeadStatic += OnPlayerDead;
            _subscribed = true;
            Plugin.LogSource?.LogInfo("[GoldenPick] gold-kill handler armed");
        }

        public static void Shutdown()
        {
            if (!_subscribed) return;
            Player.OnPlayerDeadStatic -= OnPlayerDead;
            _subscribed = false;
        }

        private static void OnPlayerDead(Player victim, IPlayer killer, DamageInfoStruct damage, EBodyPart bodyPart)
        {
            try
            {
                if (victim == null) return;

                // only kills dealt by our pick gild the corpse
                var weapon = damage.Weapon;
                if (weapon == null || weapon.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;

                // real-pick gate — only authored picks (admin grant or crate-derived) make
                // gold statues + count for the leaderboard. counterfeits that slipped past
                // the audit just produce a regular ragdoll. resolve via StablePickIdResolver
                // so hideout-firing-range clones map back to the scabbard pick's stable id.
                var pickId = GoldenPickSheen.StablePickIdResolver.Resolve(weapon.Id);
                var meta = GoldenPickSheen.PickMetadataLookup.GetOrNull(pickId);
                if (meta == null)
                {
                    Plugin.LogSource?.LogInfo($"[GoldenPick] golden-pick kill but pick {pickId} has NO metadata — regular ragdoll, not counted");
                    return;
                }

                Plugin.LogSource?.LogInfo($"[GoldenPick] golden-pick kill (real, id={pickId}) — gilding the corpse");

                // submit the kill to the relay leaderboard. fire-and-forget so the statue
                // effect doesn't wait on the network. relay verifies owner_profile_id matches
                // (with nickname fallback for legacy NULL-profileId rows); on success it ALSO
                // refreshes the stored owner_nickname so renames propagate to the leaderboard.
                var killerProfId = Earn.GoldenPickEarner.ResolveLocalProfileId();
                var killerNick   = Earn.GoldenPickEarner.ResolveLocalNickname();
                if (!string.IsNullOrEmpty(killerProfId) && !string.IsNullOrEmpty(killerNick))
                    Net.PickKillBridge.Submit(pickId, killerProfId, killerNick);

                var gold = LoadMaterial();
                var sound = LoadSoundPrefab();
                // drive the gild + sound from a coroutine so we can re-apply across the ragdoll
                // transition. started on the plugin so it survives this stack frame.
                Plugin.Instance.StartCoroutine(GildAndSound(victim, gold, sound));
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick] gold-kill effect failed: {e}"); }
        }

        private static IEnumerator GildAndSound(Player victim, Material gold, GameObject soundPrefab)
        {
            // the sound prefab plays on awake, so just spawning it at the corpse fires it once
            if (soundPrefab != null) PlayGoldSound(soundPrefab, victim != null ? victim.Position : Vector3.zero);

            // gild now, then a couple more times — OnDead is followed by the ragdoll/corpse
            // swap which can reset materials, so re-applying briefly makes the gold stick.
            // we also stiffen the ragdoll on the 2nd pass — the rigidbodies dont exist yet on
            // frame 0 (CreateCorpse runs later in the same OnDead method per Player.cs:7521),
            // so a one-frame wait lets them spawn before we grab them.
            bool stiffened = false;
            for (int i = 0; i < 3; i++)
            {
                if (victim == null) yield break;
                if (gold != null) GildVictim(victim, gold);

                if (i >= 1 && !stiffened)
                    stiffened = StiffenRagdoll(victim);

                yield return new WaitForSeconds(0.2f);
            }
        }

        // statue effect: crank drag + angularDrag on every ragdoll Rigidbody so the body still
        // respects gravity (it falls + lands naturally) but doesnt flop or recoil from impacts.
        // NOT kinematic — that would freeze the death pose mid-air. heavier mass also makes
        // bullet impacts barely budge it. returns true once we actually found rigidbodies, so
        // the caller stops retrying.
        private static bool StiffenRagdoll(Player victim)
        {
            try
            {
                if (victim == null || victim.gameObject == null) return false;
                var bodies = victim.gameObject.GetComponentsInChildren<Rigidbody>(true);
                if (bodies == null || bodies.Length == 0) return false;
                foreach (var rb in bodies)
                {
                    if (rb == null) continue;
                    rb.drag = 8f;          // damps sliding/falling speed
                    rb.angularDrag = 15f;  // damps the floppy rotation — main "stiff" knob
                    rb.mass *= 5f;         // heavier = less affected by future impulses
                }
                Plugin.LogSource?.LogInfo($"[GoldenPick] ragdoll stiffened ({bodies.Length} rigidbodies)");
                return true;
            }
            catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] ragdoll stiffen failed: {e.Message}"); return false; }
        }

        // gild ONLY the victim's body-skin renderers (face, hair, base clothing under equipment).
        // we deliberately SKIP equipment-slot renderers (helmet, rig, backpack, etc) because
        // those come from a pool shared across all characters — mutating sharedMaterials on
        // pooled prefabs leaks the gold to whoever the pool issues that prefab to next,
        // including the player's menu character on the result screen + future raid bots.
        //
        // PlayerBody.BodySkins is EFTs canonical body-skin grouping (Dictionary keyed by
        // EBodyPart). each entry exposes the skin renderers for that part. read via
        // reflection so we don't bake in the obfuscated container type name. fallback:
        // if BodySkins isn't accessible (decompile drift), walk all renderers and use a
        // name-based heuristic to skip equipment.
        private static void GildVictim(Player victim, Material gold)
        {
            try
            {
                var body = victim.PlayerBody;
                if (body == null) return;

                var renderers = CollectBodySkinRenderers(body);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var cur = r.sharedMaterials;
                    if (cur == null || cur.Length == 0)
                    {
                        _gilded.Add((r, null));
                        r.sharedMaterial = gold;
                        continue;
                    }
                    _gilded.Add((r, (Material[])cur.Clone()));
                    var mats = new Material[cur.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = gold;
                    r.sharedMaterials = mats;
                }
            }
            catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] gild pass failed: {e.Message}"); }
        }

        private static System.Reflection.FieldInfo _bodySkinsField;
        private static bool _bodySkinsLookupFailed;

        private static List<Renderer> CollectBodySkinRenderers(EFT.PlayerBody body)
        {
            var result = new List<Renderer>(32);

            // primary path: PlayerBody.BodySkins (Dictionary<EBodyPart, *>). each value has
            // a Renderers property/field with the skin renderers for that part. body skins
            // are NOT pooled across characters — every Player has its own — so mutating
            // them is safe.
            if (!_bodySkinsLookupFailed)
            {
                try
                {
                    if (_bodySkinsField == null)
                        _bodySkinsField = HarmonyLib.AccessTools.Field(typeof(EFT.PlayerBody), "BodySkins");
                    var dict = _bodySkinsField?.GetValue(body) as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        foreach (var v in dict.Values)
                        {
                            if (v == null) continue;
                            var t = v.GetType();
                            var prop = HarmonyLib.AccessTools.Property(t, "Renderers");
                            var fld  = prop == null ? HarmonyLib.AccessTools.Field(t, "Renderers") : null;
                            var rs = (prop != null ? prop.GetValue(v) : fld?.GetValue(v)) as System.Collections.IEnumerable;
                            if (rs == null) continue;
                            foreach (var item in rs) if (item is Renderer r) result.Add(r);
                        }
                        if (result.Count > 0) return result;
                    }
                    _bodySkinsLookupFailed = true;
                    Plugin.LogSource?.LogWarning("[GoldenPick] PlayerBody.BodySkins yielded 0 renderers — falling back to name-heuristic filter");
                }
                catch (Exception e)
                {
                    _bodySkinsLookupFailed = true;
                    Plugin.LogSource?.LogWarning($"[GoldenPick] PlayerBody.BodySkins reflection failed ({e.GetType().Name}: {e.Message}) — falling back to name-heuristic filter");
                }
            }

            // fallback: walk all renderers, skip anything whose hierarchy is under a slot
            // (equipment). slot_* / *equipment* / *rig* / *helmet* / *backpack* in any
            // ancestor name → treat as equipment, dont gild.
            var all = new List<Renderer>(64);
            body.GetRenderersNonAlloc(all);
            foreach (var r in all)
            {
                if (r == null) continue;
                if (IsUnderEquipmentSlot(r.transform)) continue;
                result.Add(r);
            }
            return result;
        }

        private static bool IsUnderEquipmentSlot(Transform t)
        {
            while (t != null)
            {
                var name = t.name;
                if (!string.IsNullOrEmpty(name))
                {
                    var lower = name.ToLowerInvariant();
                    if (lower.StartsWith("slot_") || lower.Contains("equipment")
                        || lower.Contains("rig") || lower.Contains("helmet")
                        || lower.Contains("backpack") || lower.Contains("armor"))
                        return true;
                }
                t = t.parent;
            }
            return false;
        }

        // called at raid end (when GameWorld.Instance goes null) to undo every gild we applied
        // during the raid. silently skips renderers that got destroyed in the meantime
        // (corpses that despawned). after this runs the pool is clean — the menu char picks
        // up any pooled equipment prefab in its original look.
        public static void RestoreGildedRenderers()
        {
            int restored = 0, skipped = 0;
            foreach (var (r, originals) in _gilded)
            {
                if (r == null) { skipped++; continue; }
                if (originals == null) { skipped++; continue; }   // nothing meaningful to put back
                try { r.sharedMaterials = originals; restored++; }
                catch { skipped++; }
            }
            _gilded.Clear();
            if (restored > 0 || skipped > 0)
                Plugin.LogSource?.LogInfo($"[GoldenPick] gild restored on {restored} renderer(s) at raid end ({skipped} skipped)");
        }

        private static void PlayGoldSound(GameObject prefab, Vector3 pos)
        {
            try
            {
                var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                // self-destruct after the clip so it doesnt linger; fall back to a safe cap
                float life = 15f;
                var src = go.GetComponentInChildren<AudioSource>();
                if (src != null && src.clip != null) life = src.clip.length + 0.5f;
                UnityEngine.Object.Destroy(go, life);
            }
            catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] gold sound failed: {e.Message}"); }
        }

        // load goldmaterial from its bundle (once, cached). loaded directly — no item references
        // it so SPT doesnt eager-load it, same as the reveal bundles.
        private static Material LoadMaterial()
        {
            if (_matLoadTried) return _gold;
            _matLoadTried = true;
            try
            {
                var path = Path.Combine(BepInEx.Paths.GameRootPath, MatBundleRelPath);
                if (!File.Exists(path)) { Plugin.LogSource?.LogError($"[GoldenPick] gold material bundle not found at {path}"); return null; }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Plugin.LogSource?.LogError("[GoldenPick] gold material AssetBundle.LoadFromFile returned null"); return null; }
                _gold = bundle.LoadAllAssets<Material>().FirstOrDefault();
                if (_gold == null) Plugin.LogSource?.LogError("[GoldenPick] no Material in goldmat bundle");
                else Plugin.LogSource?.LogInfo($"[GoldenPick] gold material loaded: {_gold.name}");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick] gold material load failed: {e}"); }
            return _gold;
        }

        private static GameObject LoadSoundPrefab()
        {
            if (_soundLoadTried) return _soundPrefab;
            _soundLoadTried = true;
            try
            {
                var path = Path.Combine(BepInEx.Paths.GameRootPath, SoundBundleRelPath);
                if (!File.Exists(path)) { Plugin.LogSource?.LogError($"[GoldenPick] gold kill sound bundle not found at {path}"); return null; }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Plugin.LogSource?.LogError("[GoldenPick] gold kill sound AssetBundle.LoadFromFile returned null"); return null; }
                _soundPrefab = bundle.LoadAllAssets<GameObject>().FirstOrDefault();
                if (_soundPrefab == null) Plugin.LogSource?.LogError("[GoldenPick] no prefab in goldkillsound bundle");
                else Plugin.LogSource?.LogInfo($"[GoldenPick] gold kill sound loaded: {_soundPrefab.name}");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick] gold kill sound load failed: {e}"); }
            return _soundPrefab;
        }
    }
}
