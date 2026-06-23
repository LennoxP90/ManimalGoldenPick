using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Dialog.Commando.SptCommands;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Services;

namespace GoldenPick.Commands;

// SPT chat-bot command for password-based pick redemption. user opens dialogue with the
// Commando bot and types:
//
//     spt redeem MyPassword123
//
// flow:
//  1. parse the password from request.Text
//  2. read the CURRENT nickname from the local SPT profile (server-trusted — we never trust
//     the client's claim of who they are)
//  3. POST {password, currentNickname} to relay /pick/redeem
//  4. relay validates password, mints a fresh pick with the original metadata, broadcasts
//     pick_grant
//  5. existing pick_grant pipeline (CrateGrantBridge → server → mail) delivers the pick
//  6. send success/fail mail back through the Commando bot so the player sees confirmation
//
// note: the actual pick delivery is async — by the time this command returns, the pick
// MIGHT not yet be in the player's mailbox. the success message is sent regardless so the
// player knows the redemption was accepted; the pick mail arrives moments later from the
// pick_grant broadcast pipeline.
[Injectable]
public class RedeemPickCommand(
    SPTarkov.Server.Core.Models.Utils.ISptLogger<RedeemPickCommand> logger,
    MailSendService mailSendService,
    ProfileHelper profileHelper,
    GoldenPickRelayClient relayClient
) : ISptCommand
{
    public string Command => "redeem";
    public string CommandHelp => "Usage: spt redeem <password> — claim a custom golden pick you've been issued";

    public ValueTask<string> PerformAction(UserDialogInfo commandHandler, MongoId sessionId, SendMessageRequest request)
    {
        // request.Text is the full message: "spt redeem MyPassword" — strip the prefix and
        // grab everything after "redeem ". password may contain spaces, so don't split on them.
        var text = request.Text ?? string.Empty;
        var idx = text.IndexOf("redeem", StringComparison.OrdinalIgnoreCase);
        var password = idx >= 0 ? text.Substring(idx + "redeem".Length).Trim() : string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            mailSendService.SendUserMessageToPlayer(sessionId, commandHandler,
                "Usage: spt redeem <password>");
            return new ValueTask<string>(request.DialogId);
        }

        // current nickname from the local profile — authority for who receives the pick
        var profile = profileHelper.GetPmcProfile(sessionId);
        var nickname = profile?.Info?.Nickname;
        if (string.IsNullOrEmpty(nickname))
        {
            mailSendService.SendUserMessageToPlayer(sessionId, commandHandler,
                "Could not resolve your profile nickname. Try again after a moment.");
            return new ValueTask<string>(request.DialogId);
        }

        // fire-and-forget the relay call so the chat response isn't blocked on network
        _ = Task.Run(async () =>
        {
            try
            {
                var resp = await relayClient.RedeemPick(password, nickname, sessionId.ToString());
                string reply;
                if (resp == null)
                    reply = "Redemption couldn't reach the GoldenPick relay. Try again in a moment.";
                else if (!resp.Ok)
                    reply = "That password doesn't match any pick on record.";
                else
                {
                    var label = resp.CustomName ?? "your pick";
                    var num = resp.PickNumber.HasValue ? $" (#{resp.PickNumber.Value})" : "";
                    reply = $"Redemption accepted — {label}{num} will arrive in your messenger shortly.";
                }
                mailSendService.SendUserMessageToPlayer(sessionId, commandHandler, reply);
            }
            catch (Exception e)
            {
                logger.Error($"[GoldenPick] redeem command failed: {e.Message}");
                mailSendService.SendUserMessageToPlayer(sessionId, commandHandler, "Redemption failed unexpectedly.");
            }
        });

        return new ValueTask<string>(request.DialogId);
    }
}
