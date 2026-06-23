using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen
{
    // the killstreak sheen palette — 8 colors that an earned pick can carry. picked from the
    // users palette screenshot (TF2 killstreak sheen vibes).
    //
    // assignment is DETERMINISTIC per item id, not random-then-stored: every player who sees the
    // same pick (in their hands, on the ground, in someone elses inventory) renders the same
    // color, with NO server roundtrip and NO persisted state — because Item.Id is a fresh MongoId
    // assigned when the pick is granted, hash(id) % 8 is effectively random across picks but
    // stable for any single pick across sessions/clients.
    internal static class SheenColors
    {
        private static readonly Color[] Palette = new[]
        {
            Hex(0x28, 0xFF, 0x46),   // green
            Hex(0xF2, 0xAC, 0x0A),   // orange/gold
            Hex(0xFF, 0x1E, 0xFF),   // pink/magenta
            Hex(0xFF, 0x4B, 0x05),   // orange-red
            Hex(0x64, 0xFF, 0x0A),   // lime
            Hex(0xC8, 0x14, 0x0F),   // crimson
            Hex(0x28, 0x62, 0xC8),   // blue
            Hex(0x69, 0x14, 0xFF),   // violet
        };

        public static Color ForItemId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return Palette[0];
            // simple stable hash — avoids string.GetHashCode which is randomized per process
            // in .NET, so the SAME item would pick different colors across game launches.
            int h = 0;
            for (int i = 0; i < itemId.Length; i++) h = h * 31 + itemId[i];
            int idx = ((h % Palette.Length) + Palette.Length) % Palette.Length;
            return Palette[idx];
        }

        private static Color Hex(byte r, byte g, byte b) =>
            new Color(r / 255f, g / 255f, b / 255f, 1f);
    }
}
