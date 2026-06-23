using Comfort.Common;
using EFT.Communications;
using EFT.UI;
using UnityEngine;

namespace Manimal.GoldenPick.Notify
{
    // thin wrapper over the engine toast + a celebratory sound. verified against 4.0
    // source: NotificationManagerClass.DisplayMessageNotification is a static that
    // routes to the singleton when instantiated and no-ops cleanly before then; must be
    // called on the unity main thread (callers drain the relay queue inside Update()).
    internal static class PickNotifier
    {
        // gold-ish text, achievement icon, long on-screen time — this is a brag.
        private static readonly Color Gold = new Color(1f, 0.84f, 0f);

        public static void Show(string message)
        {
            // quest-complete jingle instead of the usual notification blip — earning a
            // pan is an achievement moment. EUISoundType.QuestCompleted is verified in
            // the 4.0 enum. wrapped because GUISounds isnt ready before the menu loads.
            // (if the toast itself also emits a default sound we'll suppress that with a
            //  separate patch — unconfirmed until we hear it in-game.)
            try { Singleton<GUISounds>.Instance?.PlayUISound(EUISoundType.QuestCompleted); }
            catch { /* sound system not up yet */ }

            // NOTE: the pop-up notifier's icon dictionary only maps a subset of
            // ENotificationIconType — passing Achievement throws KeyNotFoundException
            // in NotifierView and the message text falls back to placeholder. Default
            // and Alert are the safe ones (per HackerMod); the "achievement" feel comes
            // from the QuestCompleted sound + gold text instead.
            //
            // guarded like HackerMod's Notify: the manager singleton may not exist yet
            // on the loading screen, and a bad call shouldnt throw into the Update drain.
            try
            {
                NotificationManagerClass.DisplayMessageNotification(
                    message,
                    ENotificationDurationType.Long,
                    ENotificationIconType.Default,
                    Gold);
            }
            catch (System.Exception e)
            {
                Plugin.LogSource?.LogWarning($"[GoldenPick] notification failed: {e.Message}");
            }
        }
    }
}
