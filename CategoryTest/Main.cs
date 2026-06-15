using System;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using System.Reflection;

// Must be in HarmonyLib namespace so our Cecil scan finds it,
// and HarmonyX's PatchAll can read it at runtime.
namespace HarmonyLib
{
    internal class HarmonyPatchCategory : Attribute
    {
        public string Category { get; }
        public HarmonyPatchCategory(string category) { Category = category; }
    }
}

namespace CategoryTest
{
    public class Main
    {
        private static Harmony _harmony;
        private static MethodInfo _unpatchAllStr;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            _harmony = new Harmony("TestCategory");
            _harmony.PatchAll();

            _unpatchAllStr = typeof(Harmony).GetMethod("UnpatchAll",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string) }, null);

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            return true;
        }

        static bool OnToggle(UnityModManager.ModEntry entry, bool active)
        {
            if (!active)
            {
                Debug.Log("[CategoryTest] Toggle OFF → UnpatchAll(\"TestCategory\")");
                _unpatchAllStr?.Invoke(_harmony, new object[] { "TestCategory" });
            }
            else
            {
                _harmony.PatchAll();
            }
            return true;
        }

        static void OnGUI(UnityModManager.ModEntry entry)
        {
            GUILayout.Label("Toggle OFF to test UnpatchAll(\"TestCategory\")");
        }
    }

    [HarmonyPatchCategory("TestCategory")]
    [HarmonyPatch(typeof(Debug), "Log", new[] { typeof(object) })]
    class Patch_Log
    {
        [HarmonyPostfix]
        static void Postfix()
        {
        }
    }
}
