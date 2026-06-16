using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityModManagerNet;

namespace UMMBridge;

public class Bridge : MelonPlugin
{
    /// <summary>
    /// Tracks which methods are patched by which Harmony runtime.
    /// true  = 0Harmony_UMM native Patch() wrote a native detour.
    /// false = ProxyPatch (HarmonyX) wrote the detour.
    /// Used by the dual-MonoMod conflict detection to ensure at most
    /// one runtime writes a JMP at any given method entry.
    /// </summary>
    internal static readonly ConcurrentDictionary<MethodBase, bool> PatchedMethods = new();

    /// <summary>
    /// Methods checked via HarmonyX.GetPatchInfo and confirmed clean
    /// (no HarmonyX patches exist).  Avoids repeated dictionary-lock
    /// lookups for methods never touched by MelonLoader mods.
    /// </summary>
    internal static readonly ConcurrentDictionary<MethodBase, byte> ConflictCheckClean = new();

    /// <summary>
    /// Called from the Cecil-injected prefix in 0Harmony_UMM's
    /// Harmony.Patch().  Returns true if the method was already
    /// patched by HarmonyX (via ProxyPatch or any ML mod) →
    /// 0Harmony_UMM should skip and not create a conflicting
    /// native detour.
    /// </summary>
    public static bool IsMethodPatchedByProxy(MethodBase original)
    {
        // 1) Check our own ProxyPatch tracking
        if (PatchedMethods.TryGetValue(original, out var fromProxy))
        {
            Melon<Bridge>.Logger.Msg(
                $"ConflictCheck: \"{original.DeclaringType?.Name}.{original.Name}\" is tracked (fromProxy={fromProxy}) — [{GetCallingModName()}]");
            return !fromProxy;
        }

        // 1b) Fast negative cache — already checked HarmonyX, no patches found
        if (ConflictCheckClean.ContainsKey(original))
            return false;

        // 2) Check HarmonyX's global patch info — catches ML mods
        //    (RemovePaused) that patch methods via direct HarmonyX
        //    calls, not through ProxyPatch.
        try
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(original);
            if (info != null &&
                (info.Prefixes?.Count > 0 ||
                 info.Postfixes?.Count > 0 ||
                 info.Transpilers?.Count > 0 ||
                 info.Finalizers?.Count > 0))
            {
                Melon<Bridge>.Logger.Msg(
                    $"ConflictCheck: \"{original.DeclaringType?.Name}.{original.Name}\" has HarmonyX patches — skip native detour (caller=[{GetCallingModName()}])");
                    Melon<Bridge>.Logger.Msg($"[DBG] PatchedMethods SET {original.DeclaringType?.Name}.{original.Name}=false (source=GetPatchInfo hit)");
                PatchedMethods[original] = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning(
                $"ConflictCheck: GetPatchInfo failed for \"{original.DeclaringType?.Name}.{original.Name}\": {ex.Message}");
        }

        // 3) No HarmonyX patches — cache negative result for fast path
        ConflictCheckClean[original] = 0;

        Melon<Bridge>.Logger.Msg(
            $"ConflictCheck: \"{original.DeclaringType?.Name}.{original.Name}\" — no conflict, proceed (caller=[{GetCallingModName()}])");
        return false;
    }

    /// <summary>
    /// Walks the call stack to identify which UMM mod triggered
    /// the conflict check.  Skips internal frames (0Harmony_UMM,
    /// UMMBridge) and returns the first mod assembly name found.
    /// </summary>
    internal static string GetCallingModName()
    {
        try
        {
            var stack = new StackTrace(false);
            for (var i = 0; i < stack.FrameCount; i++)
            {
                var asm = stack.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
                if (asm == null) continue;
                var name = asm.GetName().Name;
                if (name != "0Harmony_UMM" && name != "UMMBridge")
                    return name;
            }
        }
        catch { }
        return "?";
    }

    /// <summary>
    /// Called from the Cecil-injected check in PatchFunctions.UpdateWrapper()
    /// when IsMethodPatchedByProxy already returned true (conflict detected).
    /// Routes non-transpiler patches through HarmonyX so both runtimes'
    /// patches coexist.  For methods with transpilers, returns original
    /// (skip detour — calling convention mismatch).
    /// </summary>
    public static MethodInfo RouteConflictUpdateWrapper(MethodBase original, object patchInfoObj)
    {
        // Check for transpilers — Harmony 2.3.6 calling convention is
        // incompatible with HarmonyX's, so we can't proxy transpilers.
        try
        {
            var piType = patchInfoObj.GetType();
            var transpilers = piType.GetField("transpilers")?.GetValue(patchInfoObj) as Array;
            if (transpilers != null && transpilers.Length > 0)
            {
                Melon<Bridge>.Logger.Msg(
                    $"ConflictCheck: \"{original.DeclaringType?.Name}.{original.Name}\" has transpilers — skip native detour");
                return (MethodInfo)original;
            }

            // No transpilers — apply through HarmonyX
            Melon<Bridge>.Logger.Msg(
                $"ConflictCheck: \"{original.DeclaringType?.Name}.{original.Name}\" — routing to HarmonyX");

            var hx = new HarmonyLib.Harmony(
                "UMMBridge.Route." + original.DeclaringType?.Name + "." + original.Name);
            var proc = hx.CreateProcessor(original);

            var prefixes = piType.GetField("prefixes")?.GetValue(patchInfoObj) as Array;
            if (prefixes != null)
                foreach (var p in prefixes) proc.AddPrefix(PatchToHx(p));

            var postfixes = piType.GetField("postfixes")?.GetValue(patchInfoObj) as Array;
            if (postfixes != null)
                foreach (var p in postfixes) proc.AddPostfix(PatchToHx(p));

            var finalizers = piType.GetField("finalizers")?.GetValue(patchInfoObj) as Array;
            if (finalizers != null)
                foreach (var p in finalizers) proc.AddFinalizer(PatchToHx(p));

            var result = proc.Patch();
                    Melon<Bridge>.Logger.Msg($"[DBG] PatchedMethods SET {original.DeclaringType?.Name}.{original.Name}=false (source=RouteConflict)");
            PatchedMethods[original] = false;
            return result;
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning(
                $"RouteUpdateWrapper failed for \"{original.DeclaringType?.Name}.{original.Name}\": {ex.Message}");
            return (MethodInfo)original;
        }
    }

    /// <summary>
    /// Converts a 0Harmony_UMM Patch object (reflection, cross-assembly) to a
    /// HarmonyX HarmonyMethod by reading its fields by name.
    /// </summary>
    private static HarmonyLib.HarmonyMethod PatchToHx(object patch)
    {
        if (patch == null) return null;
        var srcType = patch.GetType();

        var mi = srcType.GetProperty("PatchMethod")?.GetValue(patch, null) as MethodInfo;
        if (mi == null) return null;

        var hx = new HarmonyLib.HarmonyMethod(mi);

        var p = srcType.GetField("priority")?.GetValue(patch);
        if (p is int _) typeof(HarmonyLib.HarmonyMethod).GetField("priority")?.SetValue(hx, p);

        var d = srcType.GetField("debug")?.GetValue(patch);
        if (d is bool _) typeof(HarmonyLib.HarmonyMethod).GetField("debug")?.SetValue(hx, d);

        var bf = srcType.GetField("before")?.GetValue(patch);
        if (bf is string[] _) typeof(HarmonyLib.HarmonyMethod).GetField("before")?.SetValue(hx, bf);

        var af = srcType.GetField("after")?.GetValue(patch);
        if (af is string[] _) typeof(HarmonyLib.HarmonyMethod).GetField("after")?.SetValue(hx, af);

        return hx;
    }

    /// <summary>
    /// HarmonyX prefix on 0Harmony_UMM's Harmony.Patch().
    /// When a transpiler call is detected, tries to migrate any
    /// ProxyPatch-created patches from HarmonyX to 0Harmony_UMM
    /// so both UMM mods coexist on the same method.
    /// </summary>
    public static bool Prefix_UMMPatch(MethodBase original, object transpiler)
    {
        if (transpiler == null) return true;

        // Try migration (works when all HarmonyX patches are UMMBridge proxies)
        if (TryResolveConflict(original))
        {
            Melon<Bridge>.Logger.Msg(
                $"Prefix_UMMPatch: migrated \"{original.DeclaringType?.Name}.{original.Name}\"");
            return true;
        }

        // Migration returned false.  Could be "no HarmonyX patches at all" or
        // "non-UMM owner present".  Check which:
        Patches info;
        try { info = HarmonyLib.Harmony.GetPatchInfo(original); }
        catch { return true; }
        if (info == null) return true; // no conflict at all, let it proceed

        // Check ALL categories (including Transpilers) for non-UMMBridge owners
        var all = (info.Prefixes?.ToArray() ?? Array.Empty<Patch>())
            .Concat(info.Postfixes?.ToArray() ?? Array.Empty<Patch>())
            .Concat(info.Transpilers?.ToArray() ?? Array.Empty<Patch>())
            .Concat(info.Finalizers?.ToArray() ?? Array.Empty<Patch>());
        if (all.Any(p => !p.owner.StartsWith("UMMBridge.")))
        {
            Melon<Bridge>.Logger.Msg(
                $"Prefix_UMMPatch: blocked \"{original.DeclaringType?.Name}.{original.Name}\" — non-UMM owner");
            return false;
        }

        // All UMMBridge owners but migration failed (shouldn't happen) — proceed
        return true;
    }

    /// <summary>
    /// Check whether HarmonyX has ProxyPatch-created patches (owner starts
    /// with "UMMBridge.") for the given method.  If all owners are UMMBridge,
    /// migrate them to 0Harmony_UMM so patches coexist in one MonoMod domain.
    /// </summary>
    public static bool TryResolveConflict(MethodBase original)
    {
        Patches info;
        try { info = HarmonyLib.Harmony.GetPatchInfo(original); }
        catch { return false; }
        if (info == null) return false;

        var all = (info.Prefixes?.ToArray() ?? Array.Empty<Patch>())
            .Concat(info.Postfixes?.ToArray() ?? Array.Empty<Patch>())
            .Concat(info.Transpilers?.ToArray() ?? Array.Empty<Patch>())
            .Concat(info.Finalizers?.ToArray() ?? Array.Empty<Patch>())
            .ToList();

        if (all.Count == 0) return false;

        // Transpilers can't be migrated.  If ONLY transpilers exist, bail.
        var transpilerCount = info.Transpilers?.Count ?? 0;
        if (all.Count == transpilerCount) return false;

        var owners = all.Select(p => p.owner).Distinct().ToList();
        if (owners.Any(o => !o.StartsWith("UMMBridge.")))
            return false; // ML mod owns the method, can't migrate

        Melon<Bridge>.Logger.Msg(
            $"Migrating {all.Count} patch(es) from HarmonyX to 0Harmony_UMM for " +
            $"\"{original.DeclaringType?.Name}.{original.Name}\"");

        try
        {
            MigratePatches(original, info.Prefixes?.ToArray() ?? Array.Empty<Patch>(),
                info.Postfixes?.ToArray() ?? Array.Empty<Patch>(),
                info.Finalizers?.ToArray() ?? Array.Empty<Patch>());
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning(
                $"TryResolveConflict: migration failed for \"{original.DeclaringType?.Name}.{original.Name}\": {ex.Message}");
            return false;
        }
        return true;
    }

    private static void MigratePatches(MethodBase original,
        Patch[] prefixes, Patch[] postfixes, Patch[] finalizers)
    {
        try
        {
            var ummHarmony = AssemblyInterceptor._ummHarmony;
            if (ummHarmony == null) return;

            var ummHarmonyType = ummHarmony.GetType("HarmonyLib.Harmony");
            var ummHMType = ummHarmony.GetType("HarmonyLib.HarmonyMethod");
            var newHarmony = Activator.CreateInstance(ummHarmonyType, "UMMBridge.Migrated");

            var hmCtor = ummHMType.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMethod = ummHarmonyType.GetMethod("Patch", new[] {
                typeof(MethodBase), ummHMType, ummHMType, ummHMType, ummHMType
            });

            // Remove from HarmonyX first
            foreach (var p in prefixes.Concat(postfixes).Concat(finalizers))
                new HarmonyLib.Harmony(p.owner).Unpatch(original, p.PatchMethod);

            // Re-register on 0Harmony_UMM
            foreach (var p in prefixes)
            {
                var hm = hmCtor.Invoke(new object[] { p.PatchMethod });
                SetHmFields(hm, p.priority, p.debug, p.before, p.after);
                patchMethod.Invoke(newHarmony, new object[] { original, hm, null, null, null });
            }
            foreach (var p in postfixes)
            {
                var hm = hmCtor.Invoke(new object[] { p.PatchMethod });
                SetHmFields(hm, p.priority, p.debug, p.before, p.after);
                patchMethod.Invoke(newHarmony, new object[] { original, null, hm, null, null });
            }
            foreach (var p in finalizers)
            {
                var hm = hmCtor.Invoke(new object[] { p.PatchMethod });
                SetHmFields(hm, p.priority, p.debug, p.before, p.after);
                patchMethod.Invoke(newHarmony, new object[] { original, null, null, null, hm });
            }

            PatchedMethods[original] = true;
        }
        catch (Exception ex)
        {
            Melon<Bridge>.Logger.Warning(
                $"MigratePatches failed for \"{original.DeclaringType?.Name}.{original.Name}\": {ex.Message}");
            throw;
        }
    }

    private static void SetHmFields(object hmObj, int priority, bool debug,
        string[] before, string[] after)
    {
        var t = hmObj.GetType();
        t.GetField("priority")?.SetValue(hmObj, priority);
        t.GetField("debug")?.SetValue(hmObj, debug);
        t.GetField("before")?.SetValue(hmObj, before);
        t.GetField("after")?.SetValue(hmObj, after);
    }

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
    /// in UMM mod assemblies.  Per-call routing:
    ///   - transpiler == null → translate HarmonyMethod to HarmonyX and apply
    ///     through HarmonyX (single MonoMod for non-transpiler patches).
    ///   - transpiler != null → delegate back to 0Harmony_UMM native Patch()
    ///     (transpiler calling convention is incompatible with HarmonyX).
    /// </summary>
    public static MethodInfo ProxyPatch(
        object harmonyIgnored,  // 0Harmony_UMM's Harmony instance
        MethodBase original,
        object prefix,
        object postfix,
        object transpiler,
        object finalizer)
    {
        // If already natively patched, skip to avoid dual-MonoMod conflict
        if (PatchedMethods.TryGetValue(original, out var fromNative) && fromNative)
        {
            Melon<Bridge>.Logger.Warning(
                $"Skip ProxyPatch for \"{original.DeclaringType?.Name}.{original.Name}\" — already natively detoured");
            return null;
        }

        // ── Transpiler path ────────────────────────────────────────────
        // Calling convention incompatible with HarmonyX — delegate to
        // 0Harmony_UMM's native Patch() via reflection.
        if (transpiler != null)
        {
            try
            {
                var harmonyType = harmonyIgnored.GetType();
                var hmType = harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod");
                var nativePatch = harmonyType.GetMethod("Patch", new[] {
                    typeof(MethodBase), hmType, hmType, hmType, hmType
                });
                if (nativePatch != null)
                {
                    var nativeResult = (MethodInfo)nativePatch.Invoke(harmonyIgnored, new object[] {
                        original, prefix, postfix, transpiler, finalizer
                    });
                    if (nativeResult != null)
                                                Melon<Bridge>.Logger.Msg("[DBG] PatchedMethods SET " + original.Name + "=true (source=ProxyPatch native transpiler)");
                        PatchedMethods[original] = true; // tracked as native
                    return nativeResult;
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning(
                    $"ProxyPatch native fallback failed for \"{original.DeclaringType?.Name}.{original.Name}\": {ex.Message}");
            }
            return null;
        }

        // ── Non-transpiler path ────────────────────────────────────────
        // Translate to HarmonyX HarmonyMethod and apply through HarmonyX.
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

        var result = proc.Patch();
        PatchedMethods[original] = false; // track as ProxyPatch
        return result;
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
