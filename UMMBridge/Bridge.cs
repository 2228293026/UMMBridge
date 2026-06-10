using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityModManagerNet;

namespace UMMBridge;

public class Bridge : MelonPlugin
{
    private static readonly MethodInfo OriginalModsPathGetter = typeof(UnityModManager).GetProperty(nameof(UnityModManager.modsPath), BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
    private static readonly MethodInfo RedirectMethod = typeof(Bridge).GetMethod(nameof(RedirectModsPath), BindingFlags.Static | BindingFlags.NonPublic);

    public override void OnApplicationStarted()
    {
        var harmony = new HarmonyLib.Harmony("UMMBridge");
        harmony.Patch(OriginalModsPathGetter, new HarmonyMethod(RedirectMethod));

        LoggerInstance.Msg("Starting UMM");
        UnityModManager.Start();
        LoggerInstance.Msg("Loading UI");
        AccessTools.Method(typeof(Injector), "RunUI").Invoke(null, null);

        PatchUI();
    }

    private static bool RedirectModsPath(ref string __result)
    {
        var ummModsDir = Path.Combine(MelonEnvironment.GameRootDirectory, "UMMMods");
        Directory.CreateDirectory(ummModsDir);
        __result = ummModsDir;
        return false;
    }

    private void PatchUI()
    {
        try
        {
            var uiType = typeof(UnityModManager).Assembly.GetType("UnityModManagerNet.UnityModManager+UI");
            if (uiType == null)
            {
                LoggerInstance.Warning("UI nested type not found");
                return;
            }
            var windowFunc = AccessTools.Method(uiType, "WindowFunction");
            if (windowFunc == null)
            {
                LoggerInstance.Warning("UI.WindowFunction not found");
                return;
            }
            new HarmonyLib.Harmony("UMMBridge.UI").Patch(windowFunc, postfix: new HarmonyMethod(typeof(Bridge), nameof(Post_WindowFunction)));
            LoggerInstance.Msg("Mods Dir button added to UMM window");
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning($"Failed to patch UMM UI: {ex.Message}");
        }
    }

    private static void Post_WindowFunction(object __instance)
    {
        var rect = Traverse.Create(__instance).Field("mWindowRect").GetValue<Rect>();
        var btnRect = new Rect(rect.width - 105, 3, 100, 22);
        if (GUI.Button(btnRect, "Mods Dir"))
        {
            var path = Path.Combine(MelonEnvironment.GameRootDirectory, "UMMMods");
            Process.Start("explorer.exe", path);
        }
    }
}