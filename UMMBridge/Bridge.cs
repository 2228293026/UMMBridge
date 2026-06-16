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
        AssemblyInterceptor.Initialize();

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
        var ummModsDir = AssemblyInterceptor.ModsRootPath;
        Directory.CreateDirectory(ummModsDir);
        __result = ummModsDir;
        return false;
    }

    /// <summary>
    /// Called from Cecil-rewritten mod assemblies to resolve
    /// Assembly.get_Location() for byte-loaded assemblies.
    /// Returns the original file path if cached, otherwise falls
    /// back to the real Location.
    /// </summary>
    public static string GetAssemblyLocation(Assembly asm)
    {
        // Fast path — most calls happen after CWT is populated
        if (AssemblyInterceptor._assemblyOriginalPath.TryGetValue(asm, out var path))
            return path;

        // Fallback for static constructors that run inside Assembly.Load():
        // the CWT entry hasn't been added yet, but the thread-local
        // _currentLoadingPath is set before Assembly.Load().
        var loadingPath = AssemblyInterceptor._currentLoadingPath;
        if (loadingPath != null)
            return loadingPath;

        return asm.Location;
    }

    /// <summary>
    /// Proxy for Harmony 2.3.x Patch() calls intercepted via Cecil rewriting
    /// in mod assemblies (no transpiler detected).  Translates 0Harmony_UMM's
    /// HarmonyMethod objects to HarmonyX HarmonyMethod and delegates to
    /// HarmonyX, ensuring only HarmonyX's MonoMod creates native detours.
    /// </summary>
    public static MethodInfo ProxyPatch(
        object harmonyIgnored,  // 0Harmony_UMM's Harmony instance, unused
        MethodBase original,
        object prefix,
        object postfix,
        object transpiler,
        object finalizer)
    {
        var id = "UMMBridge.Proxy." + original.DeclaringType?.Name + "." + original.Name;
        var harmony = new HarmonyLib.Harmony(id);

        HarmonyLib.HarmonyMethod ToHx(object hm)
        {
            if (hm == null) return null;
            var srcType = hm.GetType();
            var mi = srcType.GetField("method")?.GetValue(hm) as MethodInfo;
            if (mi == null) return null;

            var hx = new HarmonyLib.HarmonyMethod(mi);

            // Read known fields by name.  0Harmony_UMM and HarmonyX share
            // the same field names/CLR types for priority, before, after,
            // debug.  Fields only in one side (beforeHard, afterHard,
            // mergedBefore/After) are never read, avoiding cross-assembly
            // type mismatches without needing type-level reflection checks.
            var p = srcType.GetField("priority")?.GetValue(hm);
            if (p is int _) typeof(HarmonyLib.HarmonyMethod).GetField("priority")?.SetValue(hx, p);

            var d = srcType.GetField("debug")?.GetValue(hm);
            if (d is bool _) typeof(HarmonyLib.HarmonyMethod).GetField("debug")?.SetValue(hx, d);

            var bf = srcType.GetField("before")?.GetValue(hm);
            if (bf is string[] _) typeof(HarmonyLib.HarmonyMethod).GetField("before")?.SetValue(hx, bf);

            var af = srcType.GetField("after")?.GetValue(hm);
            if (af is string[] _) typeof(HarmonyLib.HarmonyMethod).GetField("after")?.SetValue(hx, af);

            return hx;
        }

        var proc = harmony.CreateProcessor(original);
        var prefixHx = ToHx(prefix);
        if (prefixHx != null) proc.AddPrefix(prefixHx);
        var postfixHx = ToHx(postfix);
        if (postfixHx != null) proc.AddPostfix(postfixHx);
        var transpilerHx = ToHx(transpiler);
        if (transpilerHx != null) proc.AddTranspiler(transpilerHx);
        var finalizerHx = ToHx(finalizer);
        if (finalizerHx != null) proc.AddFinalizer(finalizerHx);

        return proc.Patch();
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
            var path = AssemblyInterceptor.ModsRootPath;
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
    }
}