using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Mono.Cecil;
using Mono.Cecil.Rocks;

// ── HarmonyPatchCategory stub ──────────────────────────────────────
// HarmonyX removed this attribute type. The stub lets HarmonyX's
// PatchAll() read [HarmonyPatchCategory] attributes without crashing.
// UMMBridge's UnpatchAll(string) hook consumes the category data.

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    internal class HarmonyPatchCategory : Attribute
    {
        public string Category { get; }
        public HarmonyPatchCategory(string category) { Category = category; }
    }
}

// ── Category tracking & UnpatchAll hook ────────────────────────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        /// <summary>
        /// Maps category name → (assembly simple name, full type name).
        /// Populated during Cecil assembly scanning; consumed by
        /// Prefix_UnpatchAll to restore Harmony 2.3.x category-based
        /// unpatching that HarmonyX lacks.
        /// </summary>
        private static readonly Dictionary<string, List<(string asmName, string typeName)>> _categoryPatchTypes = new();

        /// <summary>
        /// Scan an assembly with Mono.Cecil for types decorated with
        /// [HarmonyPatchCategory] and register them in _categoryPatchTypes.
        /// </summary>
        private static void ScanHarmonyPatchCategories(string filePath)
        {
            byte[] originalBytes;
            try { originalBytes = File.ReadAllBytes(filePath); }
            catch { return; }

            using (var ms = new MemoryStream(originalBytes))
            using (var asmDef = AssemblyDefinition.ReadAssembly(ms))
            {
                var asmName = asmDef.Name.Name;

                foreach (var type in asmDef.MainModule.GetAllTypes())
                {
                    var catAttr = type.CustomAttributes
                        .FirstOrDefault(a =>
                            a.AttributeType.FullName == "HarmonyLib.HarmonyPatchCategory");
                    if (catAttr == null) continue;

                    var category = catAttr.ConstructorArguments
                        .FirstOrDefault().Value as string;
                    if (string.IsNullOrEmpty(category)) continue;

                    lock (_categoryPatchTypes)
                    {
                        if (!_categoryPatchTypes.TryGetValue(category, out var list))
                        {
                            list = new List<(string, string)>();
                            _categoryPatchTypes[category] = list;
                        }
                        if (!list.Any(e => e.asmName == asmName && e.typeName == type.FullName))
                            list.Add((asmName, type.FullName));
                        Melon<Bridge>.Logger.Msg(
                            $"Category \"{category}\": {type.FullName} ({asmName})");
                    }
                }
            }
        }

        /// <summary>
        /// Prefix on Harmony.UnpatchAll(string) that adds category matching
        /// (original Harmony 2.3.x behaviour: the string parameter matches
        /// both Harmony owner ID and HarmonyPatchCategory).
        ///
        /// After the owner-ID matching by the original method, this hook
        /// resolves registered [HarmonyPatchCategory] types to their
        /// patched targets and unpatches them.
        /// </summary>
        private static bool Prefix_UnpatchAll(string harmonyID, HarmonyLib.Harmony __instance, ref bool __runOriginal)
        {
            __runOriginal = true;

            try
            {
                if (harmonyID == null) return true; // UnpatchAll(null) → skip category matching

                List<(string asmName, string typeName)> entries;
                lock (_categoryPatchTypes)
                {
                    if (!_categoryPatchTypes.TryGetValue(harmonyID, out entries))
                        return true;
                    entries = entries.ToList();
                }

                foreach (var (asmName, typeName) in entries)
                {
                    Type patchType = null;
                    try
                    {
                        var asm = AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(a =>
                                a.GetName().Name == asmName ||
                                a.GetName().Name == Path.GetFileNameWithoutExtension(asmName));
                        if (asm != null)
                            patchType = asm.GetType(typeName, false);
                    }
                    catch { continue; }

                    if (patchType == null) continue;

                    var hp = patchType.GetCustomAttribute<HarmonyPatch>();
                    if (hp?.info == null) continue;

                    var targetDeclaringType = hp.info.declaringType;
                    if (targetDeclaringType == null) continue;

                    MethodBase target = null;
                    try
                    {
                        if (hp.info.methodName != null)
                        {
                            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Static | BindingFlags.Instance |
                                        BindingFlags.DeclaredOnly;
                            target = hp.info.methodType switch
                            {
                                MethodType.Getter => targetDeclaringType.GetProperty(
                                    hp.info.methodName, flags)?.GetGetMethod(true),
                                MethodType.Setter => targetDeclaringType.GetProperty(
                                    hp.info.methodName, flags)?.GetSetMethod(true),
                                MethodType.Constructor => targetDeclaringType.GetConstructors(flags)
                                    .FirstOrDefault(),
                                _ => targetDeclaringType.GetMethod(hp.info.methodName, flags,
                                    null, hp.info.argumentTypes ?? Type.EmptyTypes, null),
                            };
                        }
                        else if (hp.info.methodType == MethodType.Constructor)
                        {
                            target = targetDeclaringType.GetConstructors(
                                BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                .FirstOrDefault();
                        }
                    }
                    catch { continue; }

                    if (target == null) continue;

                    __instance.Unpatch(target, HarmonyPatchType.All, __instance.Id);
                    Melon<Bridge>.Logger.Msg(
                        $"UnpatchAll(category=\"{harmonyID}\"): unpatched {targetDeclaringType.Name}.{target.Name}");
                }
            }
            catch (Exception ex)
            {
                Melon<Bridge>.Logger.Warning(
                    $"UnpatchAll(category=\"{harmonyID}\") hook error: {ex.Message}");
            }

            return true;
        }
    }
}
