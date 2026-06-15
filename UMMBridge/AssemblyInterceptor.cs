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
        private static Assembly _ummHarmony;
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
            AppDomain.CurrentDomain.TypeResolve += OnTypeResolve;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnTypeResolve(object sender, ResolveEventArgs args)
        {
            var name = args.Name.Split(',')[0].Trim();
            if (name == "HarmonyLib.HarmonyPatchCategory")
            {
                Melon<Bridge>.Logger.Msg("TypeResolve: served HarmonyPatchCategory stub");
                return typeof(HarmonyPatchCategory).Assembly;
            }
            return null;
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

        private static void VerifyPatchCategoryStub()
        {
            try
            {
                var resolved = Type.GetType("HarmonyLib.HarmonyPatchCategory, 0Harmony", false);
                if (resolved != null)
                    Melon<Bridge>.Logger.Msg("HarmonyPatchCategory stub verified (TypeResolve active)");
                else
                    Melon<Bridge>.Logger.Warning("HarmonyPatchCategory stub not reachable via TypeResolve");
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning($"HarmonyPatchCategory stub test failed: {ex.Message}");
            }
        }

        // ── Initialization ──────────────────────────────────────────────

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            VerifyPatchCategoryStub();

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

            try
            {
                var h = new HarmonyLib.Harmony("UMMBridge.AssemblyInterceptor");
                var loadFrom = AccessTools.Method(typeof(Assembly), "LoadFrom", new[] { typeof(string) });
                var prefix = AccessTools.Method(typeof(AssemblyInterceptor), nameof(Prefix_LoadFrom));

                var unpatchAll = AccessTools.Method(typeof(HarmonyLib.Harmony), "UnpatchAll", new[] { typeof(string) });
                var prefixUnpatch = AccessTools.Method(typeof(AssemblyInterceptor), nameof(Prefix_UnpatchAll));

                if (loadFrom != null && prefix != null)
                {
                    h.Patch(loadFrom, new HarmonyMethod(prefix));
                    if (unpatchAll != null && prefixUnpatch != null)
                        h.Patch(unpatchAll, new HarmonyMethod(prefixUnpatch));
                    Melon<Bridge>.Logger.Msg(
                        "Assembly.LoadFrom + UnpatchAll interceptors active");
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

        private static bool Prefix_LoadFrom(ref string assemblyFile, ref Assembly __result)
        {
            if (!assemblyFile.Contains(ModsDirName))
                return true;

            RegisterModDirectory(assemblyFile);
            ScanHarmonyPatchCategories(assemblyFile);

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

        // ── Mono.Cecil IL rewriting ──────────────────────────────────────

        private static byte[] PatchAssemblyFile(string filePath)
        {
            byte[] originalBytes;
            try { originalBytes = File.ReadAllBytes(filePath); }
            catch { return null; }

            using (var ms = new MemoryStream(originalBytes))
            using (var asmDef = AssemblyDefinition.ReadAssembly(ms))
            {
                bool modified = false;
                var harmonyRef = asmDef.MainModule.AssemblyReferences
                    .FirstOrDefault(r => r.Name == "0Harmony");

                if (harmonyRef != null)
                {
                    // All UMM mods share a single Harmony 2.3.x runtime
                    // (0Harmony_UMM) to prevent cross-runtime conflicts.
                    // HarmonyX is only used by MelonLoader itself.
                    harmonyRef.Name = "0Harmony_UMM";
                    harmonyRef.PublicKeyToken = Array.Empty<byte>();
                    modified = true;
                    Melon<Bridge>.Logger.Msg(
                        $"{Path.GetFileName(filePath)} → 0Harmony_UMM");
                }

                // Import Bridge.GetAssemblyLocation for Cecil rewriting
                MethodReference getLocationHelper = null;
                try
                {
                    var helper = typeof(Bridge).GetMethod(
                        nameof(Bridge.GetAssemblyLocation),
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(Assembly) }, null);
                    if (helper != null)
                        getLocationHelper = asmDef.MainModule.ImportReference(helper);
                }
                catch { }

                foreach (var type in asmDef.MainModule.GetAllTypes())
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        modified |= RewriteHardcodedPaths(method);
                        modified |= RewriteHarmonyLegacyMethods(method);
                        if (getLocationHelper != null)
                            modified |= RewriteAssemblyGetLocation(method, getLocationHelper);
                    }

                if (!modified) return null;

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                Melon<Bridge>.Logger.Msg($"Patched assembly: {Path.GetFileName(filePath)}");
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Replace calls to Assembly.get_Location() with
        /// Bridge.GetAssemblyLocation(Assembly) so that byte-loaded
        /// UMM mods can recover their original file paths at runtime.
        /// HarmonyX's patching of this virtual getter is unreliable
        /// (see: HarmonyX detour limitations).
        /// </summary>
        private static bool RewriteAssemblyGetLocation(MethodDefinition method, MethodReference helper)
        {
            bool modified = false;
            foreach (var inst in method.Body.Instructions)
            {
                if ((inst.OpCode != OpCodes.Call && inst.OpCode != OpCodes.Callvirt) ||
                    inst.Operand is not MethodReference mr ||
                    mr.Name != "get_Location" ||
                    mr.DeclaringType.FullName != "System.Reflection.Assembly")
                    continue;

                // get_Location is a virtual instance getter → callvirt;
                // our helper is a static method → call.
                inst.OpCode = OpCodes.Call;
                inst.Operand = helper;
                modified = true;
            }
            return modified;
        }

        private static bool RewriteHardcodedPaths(MethodDefinition method)
        {
            bool modified = false;
            foreach (var inst in method.Body.Instructions)
            {
                if (inst.OpCode != OpCodes.Ldstr || inst.Operand is not string s)
                    continue;

                string replaced = null;
                if (s == "Mods")
                    replaced = ModsDirName;
                else if (s.EndsWith("/Mods"))
                    replaced = s.Substring(0, s.Length - 4) + ModsDirName;
                else if (s.EndsWith("\\Mods"))
                    replaced = s.Substring(0, s.Length - 4) + ModsDirName;
                else if (s.EndsWith("/Mods/"))
                    replaced = s.Substring(0, s.Length - 5) + ModsDirName + "/";
                else if (s.EndsWith("\\Mods\\"))
                    replaced = s.Substring(0, s.Length - 5) + ModsDirName + "\\";
                else if (s.StartsWith("Mods/"))
                    replaced = ModsDirName + "/" + s.Substring(5);
                else if (s.StartsWith("Mods\\"))
                    replaced = ModsDirName + "\\" + s.Substring(5);
                else if (s.Contains("/Mods/"))
                    replaced = s.Replace("/Mods/", "/" + ModsDirName + "/");
                else if (s.Contains("\\Mods\\"))
                    replaced = s.Replace("\\Mods\\", "\\" + ModsDirName + "\\");

                if (replaced != null && replaced != s)
                {
                    inst.Operand = replaced;
                    modified = true;
                }
            }
            return modified;
        }

        /// <summary>
        /// Rewrite calls to Harmony 2.3.x methods that don't exist in HarmonyX.
        /// Currently: PatchAllUncategorized → PatchAll (same signature).
        /// </summary>
        private static bool RewriteHarmonyLegacyMethods(MethodDefinition method)
        {
            bool modified = false;
            if (!method.HasBody) return false;

            foreach (var inst in method.Body.Instructions)
            {
                if (inst.Operand is MethodReference mr &&
                    mr.DeclaringType.FullName == "HarmonyLib.Harmony" &&
                    mr.Name == "PatchAllUncategorized")
                {
                    mr.Name = "PatchAll";
                    modified = true;
                }
            }
            return modified;
        }
    }
}
