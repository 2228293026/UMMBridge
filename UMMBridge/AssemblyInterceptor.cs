using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

// ── Assembly interceptor ────────────────────────────────────────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        private static bool _initialized;
        internal static Assembly _ummHarmony;
        private static Assembly _harmonyX;

        /// <summary>
        /// Directory name under the game root where UMM mods reside.
        /// Used for path checks, Cecil IL rewriting, and runtime resolution.
        /// </summary>
        internal const string ModsDirName = "UMMMods";

        /// <summary>
        /// Full path to the UMM mods directory.
        /// Derived from ModsDirName at first access.
        /// </summary>
        internal static string ModsRootPath =>
            Path.Combine(MelonEnvironment.GameRootDirectory, ModsDirName);

        private static readonly Dictionary<string, string> _assemblyToDir = new();
        private static readonly Dictionary<string, Assembly> _assemblyCache = new();

        static AssemblyInterceptor()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = args.Name.Split(',')[0].Trim();

            if (name == "0Harmony_UMM" && _ummHarmony != null)
                return _ummHarmony;

            if (name == "0Harmony" && _harmonyX != null)
                return _harmonyX;

            lock (_assemblyToDir)
            {
                if (_assemblyCache.TryGetValue(name, out var cached))
                    return cached;

                if (_assemblyToDir.TryGetValue(name, out var dir))
                {
                    var path = Path.Combine(dir, name + ".dll");
                    if (File.Exists(path))
                    {
                        Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} from {dir}");
                        var asm = Assembly.LoadFrom(path);
                        _assemblyCache[name] = asm;
                        return asm;
                    }
                    return null;
                }
            }

            if (Directory.Exists(ModsRootPath))
            {
                try
                {
                    var found = Directory.GetFiles(ModsRootPath, name + ".dll", SearchOption.AllDirectories)
                                        .FirstOrDefault();
                    if (found != null)
                    {
                        var modDir = Path.GetDirectoryName(found);
                        lock (_assemblyToDir)
                        {
                            _assemblyToDir[name] = modDir;
                            var asm = Assembly.LoadFrom(found);
                            _assemblyCache[name] = asm;
                            Melon<Bridge>.Logger.Msg($"AssemblyResolve: served {name} (scanned from {modDir})");
                            return asm;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static void RegisterModDirectory(string modAssemblyPath)
        {
            var dir = Path.GetDirectoryName(modAssemblyPath);
            if (dir == null) return;

            lock (_assemblyToDir)
            {
                var mainName = Path.GetFileNameWithoutExtension(modAssemblyPath);
                if (!_assemblyToDir.ContainsKey(mainName))
                    _assemblyToDir[mainName] = dir;

                foreach (var dll in Directory.GetFiles(dir, "*.dll"))
                {
                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (!_assemblyToDir.ContainsKey(name))
                        _assemblyToDir[name] = dir;
                }
            }
        }

        // ── Initialization ──────────────────────────────────────────────

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _harmonyX = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "0Harmony" &&
                                     a.GetName().Version.Major >= 2);

            var ummHarmonyBytes = LoadUmmHarmonyAs_UMM();
            if (ummHarmonyBytes == null)
            {
                Melon<Bridge>.Logger.Warning(
                    "UMM 0Harmony.dll not found alongside UnityModManager.dll — " +
                    "mods using Harmony 2.x internals will NOT load.  Install/restore UnityModManager.");
                return;
            }
            _ummHarmony = Assembly.Load(ummHarmonyBytes);
            Melon<Bridge>.Logger.Msg($"Loaded UMM 0Harmony as 0Harmony_UMM v{_ummHarmony.GetName().Version}");

            // Hook 0Harmony_UMM's Harmony.Patch() via HarmonyX prefix.
            // When a transpiler call arrives, TryResolveConflict migrates any
            // ProxyPatch-created patches from HarmonyX to 0Harmony_UMM,
            // so both runtimes' UMM mods coexist on the same method.
            try
            {
                var ummHarmonyType = _ummHarmony.GetType("HarmonyLib.Harmony");
                if (ummHarmonyType != null)
                {
                    var patchMethod = ummHarmonyType.GetMethod("Patch", new[] {
                        typeof(MethodBase),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod"),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod"),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod"),
                        ummHarmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod")
                    });
                    if (patchMethod != null)
                    {
                        var bridgePrefix = typeof(Bridge).GetMethod(
                            nameof(Bridge.Prefix_UMMPatch),
                            BindingFlags.Public | BindingFlags.Static);
                        if (bridgePrefix != null)
                        {
                            var h = new HarmonyLib.Harmony("UMMBridge.UMMHook");
                            h.Patch(patchMethod, new HarmonyMethod(bridgePrefix));
                            Melon<Bridge>.Logger.Msg(
                                "0Harmony_UMM::Harmony.Patch() hooked — transpiler migration active");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning(
                    $"Failed to hook 0Harmony_UMM Harmony.Patch(): {ex.Message}");
            }

            try
            {
                var h = new HarmonyLib.Harmony("UMMBridge.AssemblyInterceptor");
                var loadFrom = AccessTools.Method(typeof(Assembly), "LoadFrom", new[] { typeof(string) });
                var prefix = AccessTools.Method(typeof(AssemblyInterceptor), nameof(Prefix_LoadFrom));

                if (loadFrom != null && prefix != null)
                {
                    h.Patch(loadFrom, new HarmonyMethod(prefix));
                    Melon<Bridge>.Logger.Msg(
                        "Assembly.LoadFrom interceptor active");
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning($"Interceptor initialization failed: {ex.Message}");
            }
        }

        private static byte[] LoadUmmHarmonyAs_UMM()
        {
            Assembly ummAssembly;
            try
            {
                ummAssembly = typeof(UnityModManagerNet.UnityModManager).Assembly;
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning(
                    $"UnityModManagerNet.UnityModManager type not found. UnityModManager may be missing: {ex.Message}");
                return null;
            }

            var ummDir = Path.GetDirectoryName(ummAssembly.Location);
            var path = Path.Combine(ummDir, "0Harmony.dll");
            if (!File.Exists(path)) return null;

            byte[] originalBytes;
            try { originalBytes = File.ReadAllBytes(path); }
            catch { return null; }

            using (var ms = new MemoryStream(originalBytes))
            using (var asmDef = AssemblyDefinition.ReadAssembly(ms))
            {
                var originalVersion = asmDef.Name.Version;
                asmDef.Name = new AssemblyNameDefinition("0Harmony_UMM", originalVersion);
                asmDef.Name.PublicKeyToken = Array.Empty<byte>();
                asmDef.Name.PublicKey = Array.Empty<byte>();

                // ── Only Cecil injection: PatchFunctions.UpdateWrapper() ──
                // CreateClassProcessor/PatchAll calls bypass Harmony.Patch()
                // and reach UpdateWrapper via ProcessPatchJob.  This injection
                // tries migration first, then falls back to RouteConflictUpdateWrapper.
                var patchFunctionsType = asmDef.MainModule.GetType("HarmonyLib.PatchFunctions");
                if (patchFunctionsType != null)
                {
                    var tracker3 = typeof(Bridge).GetMethod(
                        nameof(Bridge.IsMethodPatchedByProxy),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(MethodBase) }, null);
                    var resolveMethod = typeof(Bridge).GetMethod(
                        nameof(Bridge.TryResolveConflict),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(MethodBase) }, null);
                    var routeMethod = typeof(Bridge).GetMethod(
                        nameof(Bridge.RouteConflictUpdateWrapper),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(MethodBase), typeof(object) }, null);
                    if (tracker3 != null && resolveMethod != null && routeMethod != null)
                    {
                        var trackerRef3 = asmDef.MainModule.ImportReference(tracker3);
                        var resolveRef = asmDef.MainModule.ImportReference(resolveMethod);
                        var routeRef = asmDef.MainModule.ImportReference(routeMethod);
                        foreach (var m in patchFunctionsType.Methods)
                        {
                            if (m.Name != "UpdateWrapper" || !m.IsStatic) continue;
                            if (m.Parameters.Count < 2) continue;
                            if (m.Parameters[0].ParameterType.FullName != "System.Reflection.MethodBase")
                                continue;
                            if (!m.HasBody) continue;

                            var il = m.Body.GetILProcessor();
                            var first = m.Body.Instructions[0];

                            // ldarg.0 → IsMethodPatchedByProxy → brfalse FIRST
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[0]));
                            il.InsertBefore(first, il.Create(OpCodes.Call, trackerRef3));
                            il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));

                            // Conflict! Try migration → brtrue FIRST
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[0]));
                            il.InsertBefore(first, il.Create(OpCodes.Call, resolveRef));
                            il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));

                            // Migration failed → RouteConflictUpdateWrapper
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[0]));
                            il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[1]));
                            il.InsertBefore(first, il.Create(OpCodes.Call, routeRef));
                            il.InsertBefore(first, il.Create(OpCodes.Ret));
                        }
                    }
                }

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                return outMs.ToArray();
            }
        }

        // ── Harmony Prefix: LoadFrom ────────────────────────────────────

        /// <summary>
        /// Tracks the original file path for assemblies loaded via
        /// Assembly.Load(byte[]) so Bridge.GetAssemblyLocation() can
        /// recover the real path at runtime (used by Cecil-rewritten
        /// get_Location calls in patched mods).
        /// </summary>
        internal static readonly ConditionalWeakTable<Assembly, string> _assemblyOriginalPath = new();

        /// <summary>
        /// Set before Assembly.Load() so that static constructors
        /// can still resolve their original path via Bridge.GetAssemblyLocation()
        /// before the CWT entry is populated.
        /// </summary>
        [ThreadStatic]
        internal static string _currentLoadingPath;

        private static bool Prefix_LoadFrom(string assemblyFile, ref Assembly __result)
        {
            if (!assemblyFile.Contains(ModsDirName))
                return true;

            RegisterModDirectory(assemblyFile);

            var patched = PatchAssemblyFile(assemblyFile);
            if (patched == null)
                return true;

            _currentLoadingPath = assemblyFile;
            try
            {
                var asm = Assembly.Load(patched);
                _assemblyOriginalPath.Add(asm, assemblyFile);
                __result = asm;
                return false;
            }
            finally
            {
                _currentLoadingPath = null;
            }
        }

    }
}
