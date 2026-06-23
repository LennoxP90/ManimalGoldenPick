using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen
{
    // helper to find the visible weapon mesh's transform under a WeaponPrefab hierarchy.
    // we parent the sheen cube to THIS instead of Weapon_root because Weapon_root is a rig
    // attachment point that may NOT track the visible mesh in all poses — the menu char's
    // pose puts Weapon_root at the belt while the mesh sits in the hand, so a cube parented
    // to Weapon_root flashes through the belt area instead of the pick.
    //
    // strategy: walk the hierarchy starting from the WeaponPrefab root, return the first
    // SkinnedMeshRenderer or MeshRenderer's transform. that's the actual visible pick mesh.
    internal static class SheenAnchorFinder
    {
        // root is typically the WeaponPrefab's GameObject (which contains Weapon_root deep
        // inside). includeInactive=true so we still find the mesh on UI/menu paths where the
        // hierarchy may be assembled with some objects inactive momentarily.
        public static Transform FindMeshTransform(GameObject root)
        {
            if (root == null) return null;
            var smr = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) return smr.transform;
            var mr = root.GetComponentInChildren<MeshRenderer>(true);
            if (mr != null) return mr.transform;
            return null;
        }
    }
}
