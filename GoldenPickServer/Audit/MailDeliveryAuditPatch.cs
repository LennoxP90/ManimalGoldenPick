using System.Reflection;
using GoldenPick.RaidProgress;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Dialog;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace GoldenPick.Audit;

// counterfeit-pick block at mail-deliver time. Prefix on MailSendService.SendMessageToPlayer
// scans attachments for golden picks; any pick id not in PickMetadataStore gets its template
// rewritten to red rebel before the mail lands. mail-time (not inventory-render-time) so we
// mutate once per delivery instead of every UI redraw.
//
// SKIPS the audit when PickMetadataStore.Count == 0 — that's either a fresh install OR a
// wiped metadata file (which would otherwise red-rebel every legit pick from any mod).
//
// our own grant-pick stores metadata BEFORE calling MailSendService, so admin grants pass.
// crate-derived picks come through the unpack flow, not mail — unaffected.
public class MailDeliveryAuditPatch : AbstractPatch
{
    private const string GoldenPickTpl = "6a371980784a6d8a3ec033ed";
    private const string RedRebelTpl   = "5c0126f40db834002a125382";

    // static refs the prefix reads — AbstractPatch's static-method requirement leaves us
    // no other DI hook. wired by MailAuditEnabler at OnLoad time.
    internal static PickMetadataStore? Store;
    internal static ISptLogger<MailDeliveryAuditPatch>? Logger;

    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(MailSendService), nameof(MailSendService.SendMessageToPlayer));

    [PatchPrefix]
    public static void Prefix(SendMessageDetails messageDetails)
    {
        try
        {
            var items = messageDetails?.Items;
            if (items == null || items.Count == 0) return;
            if (Store == null) return;

            // refuse to run on empty store — see header comment for why
            if (Store.Count == 0) return;

            int transformed = 0;
            foreach (var item in items)
            {
                if (item.Template != GoldenPickTpl) continue;
                if (Store.TryGet(item.Id) != null) continue;
                Logger?.Warning($"[GoldenPick/MailAudit] counterfeit pick blocked at mail-deliver id={item.Id} → red rebel");
                item.Template = RedRebelTpl;
                transformed++;
            }
            if (transformed > 0 && Logger != null)
                Logger.Info($"[GoldenPick/MailAudit] mail to {messageDetails!.RecipientId}: {transformed} counterfeit pick(s) red-rebel'd");
        }
        catch (Exception e)
        {
            // never block the mail flow — log + let it through unchanged
            Logger?.Error($"[GoldenPick/MailAudit] prefix failed (mail delivers unchanged): {e}");
        }
    }
}

// IOnLoad hook that wires the static refs + enables the Harmony patch at server boot.
// runs after all services are registered with DI so PickMetadataStore is constructable.
[Injectable]
public class MailAuditEnabler(
    ISptLogger<MailDeliveryAuditPatch> logger,
    PickMetadataStore store
) : SPTarkov.Server.Core.DI.IOnLoad
{
    public Task OnLoad()
    {
        try
        {
            MailDeliveryAuditPatch.Store  = store;
            MailDeliveryAuditPatch.Logger = logger;
            new MailDeliveryAuditPatch().Enable();
            logger.Info("[GoldenPick/MailAudit] mail-delivery counterfeit audit ARMED");
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick/MailAudit] failed to arm — counterfeit picks via mail wont be caught: {e}");
        }
        return Task.CompletedTask;
    }
}
