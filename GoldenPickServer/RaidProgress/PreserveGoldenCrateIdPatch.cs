using System;
using System.Collections.Generic;
using HarmonyLib;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace GoldenPick.RaidProgress;

// Harmony patch on SPT's ItemExtensions.ReplaceIDs to PRESERVE the Item.Id for items whose
// Template is our golden crate. without this the mail flow (MailSendService → ProcessItems
// BeforeAddingToMail → ReplaceIDs) regenerates the crate's id when it's delivered to the
// player's messenger, but the server's /goldenpick/cratesig record and the pick metadata are
// both keyed by the ORIGINAL id — so the post-mail crate's lookup misses and a legitimately-
// earned crate can't unpack.
//
// scoped to our tpl only. every other system that calls ReplaceIDs (loot gen, presets,
// ragfair, hideout production, scav case, cultist circle — 15+ call sites) still gets fresh
// ids as normal. our crate is never loot-generated or preset-derived, so this scoping has no
// known interaction with other systems.
//
// matching is INDEX-BASED rather than reference-based because the `items` parameter could be
// a deferred LINQ query — iterating it in our Prefix would yield different Item instances
// than the iteration inside ReplaceIDs. positional indexing survives either case as long as
// the source enumerable yields deterministically in order (true for List/array/standard SPT
// usage).
public static class PreserveGoldenCrateIdPatch
{
    // both crate AND pick templates need their ids preserved through mail flows. crates: so
    // the unpack-time cratesig lookup finds the right record. picks: so awarded picks land in
    // the player's messenger with the originally-minted id intact (the metadata in
    // PickMetadataStore is keyed by that id).
    private const string GoldenCrateTpl = "9c2f1a0b7e6d4c83a5f10b2e";
    private const string GoldenPickTpl  = "6a371980784a6d8a3ec033ed";

    private static readonly HashSet<string> PreservedTpls = new()
    {
        GoldenCrateTpl,
        GoldenPickTpl,
    };

    private static readonly Harmony _harmony = new Harmony("com.manimal.goldenpick.server.preserve-id");

    public static void Apply()
    {
        var target = AccessTools.Method(typeof(ItemExtensions), nameof(ItemExtensions.ReplaceIDs));
        if (target == null)
        {
            Console.WriteLine("[GoldenPick/PreserveId] FAILED to locate ItemExtensions.ReplaceIDs — patch not applied. crate ids will change through mail, so the client's cratesig lookup will miss and legitimately-earned crates won't unpack.");
            return;
        }
        var prefix = AccessTools.Method(typeof(PreserveGoldenCrateIdPatch), nameof(Prefix));
        var postfix = AccessTools.Method(typeof(PreserveGoldenCrateIdPatch), nameof(Postfix));
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
        Console.WriteLine($"[GoldenPick/PreserveId] patched {target.DeclaringType?.Name}.{target.Name}");
    }

    // capture original Id at position N for any item matching our tpl. null entries for the
    // other positions act as "don't restore at this index."
    public static void Prefix(IEnumerable<Item>? items, out List<string?>? __state)
    {
        __state = null;
        if (items == null) return;
        var list = new List<string?>();
        int matched = 0;
        foreach (var item in items)
        {
            if (item != null && PreservedTpls.Contains(item.Template.ToString()))
            {
                list.Add(item.Id.ToString());
                matched++;
            }
            else
            {
                list.Add(null);
            }
        }
        if (matched > 0)
        {
            __state = list;
            Console.WriteLine($"[GoldenPick/PreserveId] captured {matched} golden-crate Id(s) before ReplaceIDs");
        }
    }

    // restore by ordinal position — i-th item in __result corresponds to i-th captured entry
    public static void Postfix(IEnumerable<Item>? __result, List<string?>? __state)
    {
        if (__result == null || __state == null) return;
        int i = 0;
        int restored = 0;
        foreach (var item in __result)
        {
            if (i < __state.Count && item != null && __state[i] != null)
            {
                item.Id = new MongoId(__state[i]!);
                restored++;
            }
            i++;
        }
        if (restored > 0)
            Console.WriteLine($"[GoldenPick/PreserveId] restored {restored} golden-crate Id(s) after ReplaceIDs");
    }
}
