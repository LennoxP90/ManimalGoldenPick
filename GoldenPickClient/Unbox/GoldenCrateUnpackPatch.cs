using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Manimal.GoldenPick.Earn;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.GoldenPick.Unbox
{
    // intercepts the Sealed Golden Crate's Unpack action — delays for the reveal countdown,
    // re-dispatches the real unpack through vanilla, then moves the granted pick to the
    // crate's old grid spot (sorting-table fallback). placement code is try/catch'd so a
    // wrong assumption about EFT's obfuscated grid internals degrades to "pick lands
    // wherever vanilla put it" instead of breaking the unpack.
    internal class GoldenCrateUnpackPatch : ModulePatch
    {
        // seconds until the "POP" — the pick is granted at this moment. matches the reveal
        // sequence's big-pop timing; the FX hookup will sync the visual pop to this.
        private const float PopDelaySeconds = 3f;
        // safety cap only — the reveal now lingers until every particle system / the audio
        // actually finishes (the ~3s build-up + ~12s trickle + particle lifetimes run well
        // past 15s, so this just prevents a runaway if something loops).
        private const float RevealSeconds = 30f;

        // crate ids we've re-dispatched, so our own second UnpackItem call passes straight
        // through to vanilla instead of being intercepted again (re-entrancy guard).
        private static readonly HashSet<string> PassThrough = new HashSet<string>();

        // ItemUiContext privates we need (reflected once)
        private static readonly FieldInfo TraderControllerField =
            AccessTools.Field(typeof(ItemUiContext), "traderControllerClass");
        private static readonly FieldInfo InventoryField =
            AccessTools.Field(typeof(ItemUiContext), "inventory_0");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.UnpackItem));

        [PatchPrefix]
        private static bool Prefix(ItemUiContext __instance, Item targetItem, ref Task<IResult> __result)
        {
            if (targetItem == null) return true;

            // our own delayed re-dispatch — let vanilla do the actual unpack
            if (PassThrough.Remove(targetItem.Id)) return true;

            // only our crate gets the show
            if (targetItem.TemplateId.ToString() != GoldenPickConstants.GoldenCrateTpl) return true;

            // counterfeit check — every crate must carry a relay-issued Ed25519 signature
            // in the local store. spawned crates dont have one + forged ones cant satisfy
            // the public-key verify. fail closed: any miss → refuse, leave crate in inventory.
            if (!CounterfeitDetector.IsLegitimate(targetItem.Id))
            {
                try { Notify.PickNotifier.Show("Counterfeit Golden Crate — cannot be unpacked. Only crates earned in raid will open."); }
                catch { /* notifier not ready */ }
                // signal failure back to the caller via a properly-typed IResult so EFTs unpack
                // UI handles it cleanly. FailedResult(message, errorCode) is what vanilla uses
                // for "can't do this to item" style refusals.
                __result = Task.FromResult<IResult>(new FailedResult("Counterfeit Golden Crate — cannot be unpacked.", 0));
                return false;
            }

            // capture the crate's grid spot BEFORE it's consumed
            var spot = CaptureSpot(targetItem);

            // snapshot existing picks so we can tell which one is freshly granted
            var inventory = InventoryField?.GetValue(__instance) as Inventory;
            var before = SnapshotPickIds(inventory);

            // hand the caller a task that completes only when our whole flow finishes
            var tcs = new TaskCompletionSource<IResult>();
            __result = tcs.Task;
            Plugin.Instance.StartCoroutine(Flow(__instance, targetItem, spot, inventory, before, tcs));
            return false; // skip the instant unpack; our coroutine drives it
        }

        private static IEnumerator Flow(
            ItemUiContext ctx, Item crate, GridSpot spot, Inventory inventory,
            HashSet<string> before, TaskCompletionSource<IResult> tcs)
        {
            Plugin.LogSource?.LogInfo("[GoldenPick] crate unpack intercepted — countdown started");

            // the id the FX tracks each frame. starts on the crate's icon; we flip it to the
            // pick's icon once the pick exists, so the effect hands off box → pick at the pop.
            // captured by the closure below — mutating it here is seen by RevealFxPlayer.
            string fxTargetId = crate.Id;

            // play the full reveal (fuse → pop at PopDelaySeconds → 12s firework trickle),
            // with the audio fired on the same frame so it stays in sync. the FX rides whatever
            // icon fxTargetId points at (crate now, pick after the pop), following drags too.
            RevealFxPlayer.PlayOnIcon(() => fxTargetId, RevealSeconds);
            RevealFxPlayer.PlaySound(RevealSeconds);

            // pre-boom wait, cancellable each frame via ItemUiContext active-check. closing
            // the stash BEFORE the boom cancels FX + aborts the unpack (crate stays whole).
            // AFTER the boom, a separate sweep below cancels only the FX — unpack must finish.
            float waited = 0f;
            while (waited < PopDelaySeconds)
            {
                if (!IsInventoryOpen())
                {
                    Plugin.LogSource?.LogInfo("[GoldenPick] stash closed pre-boom — cancelling FX + aborting unpack");
                    RevealFxPlayer.Cancel();
                    tcs.SetResult(new FailedResult("Unpack cancelled (stash closed).", 0));
                    yield break;
                }
                waited += Time.deltaTime;
                yield return null;
            }

            // the boom — firework pops on this exact beat. broadcast the earn HERE (not
            // after the unpack round-trip) so the toast + mail land in sync with the visual.
            try { GoldenPickEarner.EarnGoldenPick("crate unpack"); }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick] earn broadcast failed: {e}"); }

            // re-dispatch the real unpack through vanilla

            PassThrough.Add(crate.Id);
            Task<IResult> unpack = ctx.UnpackItem(crate);
            // post-boom: if stash closes during the network round-trip or placement, cancel
            // the lingering visual FX (12s trickle) but DON'T abort — the unpack already
            // happened server-side, the pick is real now.
            bool fxCancelled = false;
            while (unpack != null && !unpack.IsCompleted)
            {
                if (!fxCancelled && !IsInventoryOpen())
                {
                    Plugin.LogSource?.LogInfo("[GoldenPick] stash closed post-boom — cancelling FX (unpack continues)");
                    RevealFxPlayer.Cancel();
                    fxCancelled = true;
                }
                yield return null;
            }
            IResult result = unpack?.Result;

            // place the granted pick at the crate's old spot (sorting-table fallback)
            Item pick = null;
            try
            {
                pick = PlaceGrantedPick(ctx, inventory, before, spot);
                // hand the FX off to the pick's icon so it keeps following the real item
                if (pick != null) fxTargetId = pick.Id;
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick] placement failed: {e}"); }

            // inherit pick_number from the consumed crate so the tooltip shows "Pick #N".
            // fire-and-forget — no need to block on the response.
            if (pick != null)
                Net.InheritPickMetaBridge.Forward(pick.Id, crate.Id);

            // one last close-check — covers the gap between placement and the trickle expiring.
            if (!fxCancelled && !IsInventoryOpen())
                RevealFxPlayer.Cancel();

            tcs.SetResult(result);
        }

        // inventory-open detection. ItemUiContext is the singleton that drives all stash UI;
        // when the inventory screen closes, the context becomes inactive (singleton still
        // exists, gameObject deactivated). this is the most reliable signal we have without
        // touching specific InventoryScreen internals — and works in both menu + raid contexts.
        private static bool IsInventoryOpen()
        {
            try
            {
                var inst = ItemUiContext.Instance;
                return inst != null && inst.gameObject != null && inst.gameObject.activeInHierarchy;
            }
            catch { return true; }  // if reflection of state errors, fail-open (don't accidentally cancel)
        }

        private struct GridSpot
        {
            public StashGridClass Grid;
            public LocationInGrid Loc;
            public bool Valid;
        }

        // crate.CurrentAddress is a grid address (GClass3393) carrying the grid + x/y/rot.
        // clone the location since the address object is invalidated once the crate is gone.
        private static GridSpot CaptureSpot(Item crate)
        {
            try
            {
                if (crate.CurrentAddress is GClass3393 addr && addr.Grid != null && addr.LocationInGrid != null)
                    return new GridSpot { Grid = addr.Grid, Loc = addr.LocationInGrid.Clone(), Valid = true };
            }
            catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] couldnt read crate spot: {e.Message}"); }
            return default;
        }

        private static HashSet<string> SnapshotPickIds(Inventory inv)
        {
            var set = new HashSet<string>();
            try
            {
                var stash = inv?.Stash;
                if (stash != null)
                    foreach (var it in stash.GetAllItems())
                        if (it.TemplateId.ToString() == GoldenPickConstants.GoldenPickTpl)
                            set.Add(it.Id);
            }
            catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] pick snapshot failed: {e.Message}"); }
            return set;
        }

        // returns the freshly-granted pick (so the caller can hand the FX off to its icon),
        // or null if it couldn't be found.
        private static Item PlaceGrantedPick(ItemUiContext ctx, Inventory inventory, HashSet<string> before, GridSpot spot)
        {
            var stash = inventory?.Stash;
            if (stash == null) return null;

            // the new pick = a pick we didn't have before the unpack
            Item pick = stash.GetAllItems().FirstOrDefault(it =>
                it.TemplateId.ToString() == GoldenPickConstants.GoldenPickTpl && !before.Contains(it.Id));
            if (pick == null)
            {
                Plugin.LogSource?.LogWarning("[GoldenPick] couldnt find the granted pick to place");
                return null;
            }

            var controller = TraderControllerField?.GetValue(ctx) as TraderControllerClass;
            if (controller == null) { Plugin.LogSource?.LogWarning("[GoldenPick] no controller for placement"); return pick; }

            // 1) try the crate's old spot — first at the crate's original rotation, then flipped.
            // simulate:true builds + validates the op WITHOUT applying it; the network transaction
            // then APPLIES it, which is what fires the grid's add event so the view redraws. a
            // raw simulate:false move changes the data without that event — hence the pick
            // "only showed up after scrolling". the rotation try-flip catches the common case
            // where the pick is too tall for the crate's slot in default orientation but fits
            // sideways (or vice-versa).
            if (spot.Valid && spot.Grid != null)
            {
                foreach (var rot in new[] { spot.Loc.r, FlipRotation(spot.Loc.r) })
                {
                    var loc = new LocationInGrid(spot.Loc.x, spot.Loc.y, rot);
                    var address = spot.Grid.CreateItemAddress(loc);
                    var move = InteractionsHandlerClass.Move(pick, address, controller, true);
                    if (move.Succeeded)
                    {
                        controller.TryRunNetworkTransaction(move, null);
                        Plugin.LogSource?.LogInfo($"[GoldenPick] pick placed at the crate's old spot (rot {rot})");
                        return pick;
                    }
                }
            }

            // 2) fallback: sorting table. open the sorting-table window the same beat the pick
            // lands there so the user sees WHERE it went instead of having to hunt for it — the
            // panel doesnt auto-pop on its own.
            var sorting = inventory.SortingTable;
            var fbLoc = sorting?.Grid?.FindLocationForItem(pick);
            if (fbLoc != null)
            {
                var move = InteractionsHandlerClass.Move(pick, fbLoc, controller, true);
                if (move.Succeeded)
                {
                    controller.TryRunNetworkTransaction(move, null);
                    Plugin.LogSource?.LogInfo("[GoldenPick] pick moved to the sorting table");
                    OpenSortingTableWindow(ctx, sorting);
                    return pick;
                }
            }

            Plugin.LogSource?.LogInfo("[GoldenPick] left the pick where vanilla placed it (no room at spot or table)");
            return pick;
        }

        private static ItemRotation FlipRotation(ItemRotation r) =>
            r == ItemRotation.Horizontal ? ItemRotation.Vertical : ItemRotation.Horizontal;

        // open the sorting-table window so the user immediately sees where the pick went. uses
        // the existing ItemUiContext.ShowSortingTable pattern from SimpleStashPanel.cs:275 —
        // build a child context off the ItemUiContext's current ItemContextAbstractClass for the
        // sorting-table item itself. wrapped because a missing parent context shouldnt block the
        // unpack itself.
        private static void OpenSortingTableWindow(ItemUiContext ctx, SortingTableItemClass sorting)
        {
            try
            {
                var parent = ctx?.ItemContextAbstractClass;
                if (ctx == null || sorting == null || parent == null) return;
                ctx.ShowSortingTable(parent.CreateChild(sorting), sorting);
            }
            catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] couldnt open sorting-table window: {e.Message}"); }
        }
    }
}
