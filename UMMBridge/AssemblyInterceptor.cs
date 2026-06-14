using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

// ── Type stubs for missing runtime types ────────────────────────────

namespace HarmonyLib
{
    /// <summary>
    /// Stub for HarmonyLib.HarmonyPatchCategory that exists in some Harmony builds
    /// but not in MelonLoader's fork. Provided via AppDomain.TypeResolve so that
    /// mod assemblies carrying [HarmonyPatchCategory] attributes don't crash when
    /// GetCustomAttributes() tries to instantiate them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    internal class HarmonyPatchCategory : Attribute
    {
        public string Category { get; }
        public HarmonyPatchCategory(string category) { Category = category; }
    }
}

// ── Assembly interceptor ────────────────────────────────────────────

namespace UMMBridge
{
    internal static class AssemblyInterceptor
    {
        private static bool _initialized;
        private static readonly bool _hasPatchAllUncategorized;

        static AssemblyInterceptor()
        {
            _hasPatchAllUncategorized = typeof(HarmonyLib.Harmony).GetMethod(
                "PatchAllUncategorized",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(Assembly) }, null) != null;

            AppDomain.CurrentDomain.TypeResolve += OnTypeResolve;
        }

        private static Assembly OnTypeResolve(object sender, ResolveEventArgs args)
        {
            var name = args.Name.Split(',')[0].Trim();
            if (name == "HarmonyLib.HarmonyPatchCategory")
            {
                Melon<Bridge>.Logger.Msg("TypeResolve: served HarmonyPatchCategory stub");
                return typeof(HarmonyLib.HarmonyPatchCategory).Assembly;
            }
            return null;
        }

        /// <summary>
        /// Verify that the HarmonyPatchCategory stub is reachable via TypeResolve.
        /// The runtime Harmony lacks this type, so GetType("HarmonyLib.HarmonyPatchCategory, 0Harmony")
        /// should trigger TypeResolve, which returns our stub.
        /// </summary>
        private static void VerifyPatchCategoryStub()
        {
            try
            {
                // Force a TypeResolve event by requesting the type from the 0Harmony assembly
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

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // Verify TypeResolve stub works
            VerifyPatchCategoryStub();

            if (_hasPatchAllUncategorized)
                return;

            try
            {
                var harmony = new HarmonyLib.Harmony("UMMBridge.AssemblyInterceptor");
                var loadFrom = typeof(Assembly).GetMethod(
                    "LoadFrom",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                var prefix = typeof(AssemblyInterceptor).GetMethod(
                    nameof(Prefix_LoadFrom),
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (loadFrom != null && prefix != null)
                {
                    harmony.Patch(loadFrom, new HarmonyMethod(prefix));
                    Melon<Bridge>.Logger.Msg(
                        "Assembly.LoadFrom interceptor active — patching PatchAllUncategorized calls in mod assemblies");
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning($"Assembly.LoadFrom interceptor failed: {ex.Message}");
            }
        }

        // ── Harmony Prefix on Assembly.LoadFrom(string) ─────────────────

        private static readonly System.Collections.Generic.Dictionary<string, byte[]> _patchedCache = new();

        private static bool Prefix_LoadFrom(ref string assemblyFile, ref System.Reflection.Assembly __result)
        {
            if (!assemblyFile.Contains("UMMMods"))
                return true;

            if (_patchedCache.TryGetValue(assemblyFile, out var cachedBytes))
            {
                __result = System.Reflection.Assembly.Load(cachedBytes);
                return false;
            }

            var patched = PatchAssemblyFile(assemblyFile);
            if (patched == null)
                return true;

            _patchedCache[assemblyFile] = patched;
            __result = System.Reflection.Assembly.Load(patched);
            return false;
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
                if (!asmDef.MainModule.AssemblyReferences.Any(r => r.Name == "0Harmony"))
                    return null;

                bool modified = false;
                foreach (var type in asmDef.MainModule.GetAllTypes())
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        modified |= RewriteMethodCalls(method, asmDef.MainModule);
                    }

                if (!modified) return null;

                using var outMs = new MemoryStream();
                asmDef.Write(outMs);
                Melon<Bridge>.Logger.Msg($"Patched assembly: {Path.GetFileName(filePath)}");
                return outMs.ToArray();
            }
        }

        private static bool RewriteMethodCalls(MethodDefinition method, ModuleDefinition module)
        {
            if (!method.HasBody) return false;
            var proc = method.Body.GetILProcessor();
            var instructions = method.Body.Instructions;
            bool modified = false;

            for (int i = 0; i < instructions.Count; i++)
            {
                var inst = instructions[i];
                if (inst.OpCode != OpCodes.Call && inst.OpCode != OpCodes.Callvirt)
                    continue;
                if (inst.Operand is not MethodReference mr ||
                    mr.DeclaringType.FullName != "HarmonyLib.Harmony")
                    continue;

                if (mr.Name == "PatchAllUncategorized")
                {
                    inst.Operand = MakePatchAllRef(mr, module, mr.Parameters.Count >= 1);
                    modified = true;
                }
                else if (mr.Name == "PatchCategory")
                {
                    if (mr.Parameters.Count >= 2)
                    {
                        // Stack: [this, string, Assembly]. Need: [this, Assembly].
                        // Save Assembly via local, pop string, restore Assembly.
                        var asmVar = new VariableDefinition(module.ImportReference(typeof(Assembly)));
                        method.Body.Variables.Add(asmVar);
                        proc.InsertBefore(inst, proc.Create(OpCodes.Stloc, asmVar));
                        proc.InsertBefore(inst, proc.Create(OpCodes.Pop));
                        proc.InsertBefore(inst, proc.Create(OpCodes.Ldloc, asmVar));
                        inst.Operand = MakePatchAllRef(mr, module, true);
                    }
                    else
                    {
                        // Stack: [this, string]. Need: [this].
                        proc.InsertBefore(inst, proc.Create(OpCodes.Pop));
                        inst.Operand = MakePatchAllRef(mr, module, false);
                    }
                    modified = true;
                }
            }
            return modified;
        }

        private static MethodReference MakePatchAllRef(
            MethodReference original, ModuleDefinition module, bool withAssemblyParam)
        {
            var patchAll = new MethodReference(
                "PatchAll", module.TypeSystem.Void, original.DeclaringType)
            {
                HasThis = original.HasThis,
                ExplicitThis = original.ExplicitThis,
                CallingConvention = original.CallingConvention,
            };
            if (withAssemblyParam)
                patchAll.Parameters.Add(
                    new ParameterDefinition(module.ImportReference(typeof(Assembly))));
            return module.ImportReference(patchAll);
        }
    }
}
