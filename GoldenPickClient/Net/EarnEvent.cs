namespace Manimal.GoldenPick.Net
{
    // wire format for relay broadcasts. one struct covers both message types so the WS
    // receive loop can use one parser; Type discriminates and the optional fields are only
    // populated for crate_grant.
    //
    //  Type = "pick_earned"  — Player + Ts only (legacy global brag broadcast)
    //  Type = "crate_grant" — Player + CrateId + Signature + AwardedAt (relay-issued award,
    //                         dispatched by the HTML test button. BepInEx clients filter to
    //                         their own nickname before acting.)
    //  Type = "raid_result" — Player + NewCount + Awarded + LastResult (push from /raid/end,
    //                         used by the top-right debug overlay so it doesnt need to poll)
    //  Type = "pick_grant"  — Player + PickId + Signature + AwardedAt + SheenColorHex +
    //                         CustomName + CustomDescription + PickNumber. authored via the
    //                         relay's /admin/grant-pick (no crate involved). recipient
    //                         self-selects by nickname.
    internal class EarnEvent
    {
        public string Type = "pick_earned";
        public string Player;
        public long Ts;

        // populated only when Type == "crate_grant"
        // PickNumber (declared below for pick_grant) also rides along on crate_grant — it's
        // the relay's auto-incremented Pick#N for this crate. one field used by both types.
        public string CrateId;
        public string Signature;
        public long AwardedAt;

        // populated only when Type == "raid_result"
        public int NewCount;
        public bool Awarded;
        public string LastResult;

        // populated only when Type == "pick_grant"
        public string PickId;
        public string SheenColorHex;
        public string CustomName;
        public string CustomDescription;
        public int? PickNumber;
    }
}
