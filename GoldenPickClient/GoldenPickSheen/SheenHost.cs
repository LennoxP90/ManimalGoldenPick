using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen
{
    // singleton host for the SheenManager MonoBehaviour. created on first use, parked under
    // DontDestroyOnLoad so its callbacks survive scene changes (raid → menu → raid).
    internal static class SheenHost
    {
        private static GameObject _go;
        private static SheenManager _mgr;

        public static SheenManager Manager
        {
            get
            {
                if (_mgr != null) return _mgr;
                _go = new GameObject("GoldenPickSheenHost");
                Object.DontDestroyOnLoad(_go);
                _mgr = _go.AddComponent<SheenManager>();
                return _mgr;
            }
        }

        // safe check: returns null without spawning the host if it's not been created yet
        public static SheenManager ManagerIfExists => _mgr;
    }
}
