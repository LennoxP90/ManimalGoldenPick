namespace Manimal.GoldenPick
{
    internal static class GoldenPickConstants
    {
        // tpl of our cloned crowbar. matches the _id key in
        // ServerModFiles/db/CustomItems/golden_frying_pan.json. fresh 24-hex,
        // distinct from every vanilla item so theres no collision. the kill patch
        // (M3) compares the swinging weapon's tpl against this to decide whether to
        // gild the corpse.
        public const string GoldenPickTpl = "6a371980784a6d8a3ec033ed";

        // tpl of the Sealed Golden Crate (the unpackable RandomLootContainer that
        // yields the pick). matches the _id key in db/CustomItems/golden_crate.json.
        // the unpack-intercept patch (step 2) compares the unpacked item's tpl against
        // this to fire the reveal sequence instead of an instant grant.
        public const string GoldenCrateTpl = "9c2f1a0b7e6d4c83a5f10b2e";
    }
}
