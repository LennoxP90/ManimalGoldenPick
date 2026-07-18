using Comfort.Common;
using EFT;
using Manimal.GoldenPick.Notify;
using SPT.Reflection.Utils;

namespace Manimal.GoldenPick.Earn
{
    // single funnel for "a player earned a Golden Ice Pick" — local toast + relay broadcast.
    // hangs all consequences off one method so the earn condition can be swapped without
    // touching downstream code.
    internal static class GoldenPickEarner
    {
        public static void EarnGoldenPick(string source)
        {
            Plugin.LogSource?.LogInfo($"[GoldenPick] EarnGoldenPick fired (source: {source})");
            PickNotifier.Show("You just received a Golden Ice Pick!!");
        }

        // the player's profile nickname (e.g. "testdev"). PatchConstants.BackEndSession
        // works in the menu AND in raid, so it's the primary source. the in-raid player
        // is a defensive fallback for the off chance the session isn't resolved yet.
        // both reads are wrapped so a not-ready session/profile can't throw mid-earn.
        // exposed (instead of being private) so the Update drain can match incoming
        // crate_grant broadcasts by our own nickname before forwarding to local server.
        public static string ResolveLocalNickname() => ResolveName();

        // the player's profile ID (sessionId / Mongo-style 24-hex). stable identity that
        // survives nickname changes — what the relay uses for kill-credit + leaderboard
        // ownership. same fallback chain as nickname: BackEndSession primary, in-raid
        // player as defensive fallback. empty string when neither is available so callers
        // can skip the relay round-trip cleanly.
        public static string ResolveLocalProfileId()
        {
            try
            {
                var id = PatchConstants.BackEndSession?.Profile?.Id;
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
            catch { /* backend session not ready */ }

            try
            {
                var id = Singleton<GameWorld>.Instance?.MainPlayer?.Profile?.Id;
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
            catch { /* not in raid / profile not ready */ }

            return string.Empty;
        }

        private static string ResolveName()
        {
            try
            {
                var nick = PatchConstants.BackEndSession?.Profile?.Nickname;
                if (!string.IsNullOrWhiteSpace(nick))
                    return nick;
            }
            catch { /* backend session not ready */ }

            try
            {
                var nick = Singleton<GameWorld>.Instance?.MainPlayer?.Profile?.Nickname;
                if (!string.IsNullOrWhiteSpace(nick))
                    return nick;
            }
            catch { /* not in raid / profile not ready */ }

            return "An operative";
        }
    }
}
