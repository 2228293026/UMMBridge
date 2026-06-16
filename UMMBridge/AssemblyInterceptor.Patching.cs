using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;

// ── Cecil IL rewriting for UMM mod assemblies ─────────────────────

namespace UMMBridge
{
    internal static partial class AssemblyInterceptor
    {
        /// <summary>
        /// Cecil-rewrite a UMM mod assembly: replace 0Harmony→0Harmony_UMM,
        /// optionally redirect Harmony.Patch() calls through ProxyPatch
        /// (when no transpiler is present), rewrite get_Location() calls
        /// and hardcoded "Mods" paths.
        /// </summary>
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
                    harmonyRef.Name = "0Harmony_UMM";
                    harmonyRef.PublicKeyToken = Array.Empty<byte>();
                    modified = true;

                    bool usesTranspiler = ModUsesTranspiler(asmDef);
                    if (!usesTranspiler)
                    {
                        // No transpiler → rewrite harmony.Patch() call sites
                        // to Bridge.ProxyPatch, so detours go through
                        // HarmonyX's MonoMod (single writer).
                        modified |= RewriteHarmonyPatchCalls(asmDef);
                        Melon<Bridge>.Logger.Msg(
                            $"{Path.GetFileName(filePath)} → 0Harmony_UMM (ProxyPatch)");
                    }
                    else
                    {
                        // Transpiler detected → keep 0Harmony_UMM's native
                        // Patch().  Its own MonoMod writes the detour.
                        // This is safe because transpiler mods don't patch
                        // the same methods as non-transpiler or ML mods.  If
                        // two MonoMod instances ever hit the same method that
                        // will need a different fix, but in practice they
                        // don't (ADOFAI mods each patch their own methods).
                        Melon<Bridge>.Logger.Msg(
                            $"{Path.GetFileName(filePath)} → 0Harmony_UMM (native, transpiler)");
                    }
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
    }
}
