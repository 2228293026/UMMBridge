using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using System.IO;
using System.Reflection;
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
    }

    private static bool RedirectModsPath(ref string __result)
    {
        var ummModsDir = Path.Combine(MelonEnvironment.GameRootDirectory, "UMMMods");
        Directory.CreateDirectory(ummModsDir);
        __result = ummModsDir;
        return false;
    }
}