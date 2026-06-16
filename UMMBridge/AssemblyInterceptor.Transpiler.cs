using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

// ── Transpiler detection + Harmony.Patch() call rewriting ──────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        /// <summary>
        /// Check if an assembly uses Harmony transpilers (which require
        /// 0Harmony_UMM's native Patch() — HarmonyX's transpiler calling
        /// convention is incompatible).  Detection: [HarmonyTranspiler]
        /// attribute plus IL signature heuristic as safety net.
        /// </summary>
        private static bool ModUsesTranspiler(AssemblyDefinition asmDef)
        {
            foreach (var type in asmDef.MainModule.GetAllTypes())
                foreach (var method in type.Methods)
                {
                    if (method.CustomAttributes.Any(a =>
                            a.AttributeType.Name == "HarmonyTranspiler"))
                        return true;

                    // Safety net: return type + param both contain
                    // "IEnumerable" and "CodeInstruction" in their
                    // FullName → strongly suggests IEnumerable<CodeInstruction>
                    // or IEnumerator<CodeInstruction>.  The triple AND is tight
                    // enough that false positives are effectively zero.
                    if (method.ReturnType.FullName.Contains("IEnumerable") &&
                        method.ReturnType.FullName.Contains("CodeInstruction") &&
                        method.Parameters.Any(p =>
                            p.ParameterType.FullName.Contains("IEnumerable") &&
                            p.ParameterType.FullName.Contains("CodeInstruction")))
                        return true;
                }
            return false;
        }

        /// <summary>
        /// Rewrite every Harmony.Patch() call within an assembly to
        /// Bridge.ProxyPatch(), so detours go through HarmonyX's MonoMod
        /// instead of 0Harmony_UMM's.  Used for mods without transpilers.
        /// </summary>
        private static bool RewriteHarmonyPatchCalls(AssemblyDefinition asmDef)
        {
            var proxyPatch = typeof(Bridge).GetMethod(
                nameof(Bridge.ProxyPatch),
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(object), typeof(MethodBase), typeof(object),
                        typeof(object), typeof(object), typeof(object) },
                null);
            if (proxyPatch == null) return false;

            var proxyRef = asmDef.MainModule.ImportReference(proxyPatch);
            bool modified = false;

            foreach (var type in asmDef.MainModule.GetAllTypes())
                foreach (var method in type.Methods)
                    modified |= RewriteHarmonyPatchCalls(method, proxyRef);

            return modified;
        }

        private static bool RewriteHarmonyPatchCalls(MethodDefinition method, MethodReference proxyRef)
        {
            if (!method.HasBody) return false;

            // Collect call sites FIRST (snapshot), then modify.  Iterating the
            // original collection and calling InsertBefore/InsertAfter would
            // invalidate the enumerator (InvalidOperationException).
            var targets = method.Body.Instructions
                .Where(inst =>
                {
                    if (inst.OpCode != OpCodes.Call && inst.OpCode != OpCodes.Callvirt)
                        return false;
                    if (inst.Operand is not MethodReference mr) return false;
                    if (mr.DeclaringType.FullName != "HarmonyLib.Harmony") return false;
                    if (mr.Name != "Patch") return false;
                    if (mr.Parameters.Count < 1) return false;
                    return mr.Parameters[0].ParameterType.FullName == "System.Reflection.MethodBase";
                })
                .ToList();

            if (targets.Count == 0) return false;

            var il = method.Body.GetILProcessor();

            foreach (var inst in targets)
            {
                var mr = (MethodReference)inst.Operand;

                // Pad missing optional params (transpiler, finalizer) with null
                if (mr.Parameters.Count < 5)
                {
                    il.InsertBefore(inst, il.Create(OpCodes.Ldnull));
                    il.InsertBefore(inst, il.Create(OpCodes.Ldnull));
                }

                // Handle return type mismatch: pop MethodInfo, push null PatchResult
                var popInst = il.Create(OpCodes.Pop);
                var nullInst = il.Create(OpCodes.Ldnull);
                il.InsertAfter(inst, popInst);
                il.InsertAfter(popInst, nullInst);

                // Replace call target: callvirt → static call
                inst.OpCode = OpCodes.Call;
                inst.Operand = proxyRef;
            }

            return true;
        }
    }
}
