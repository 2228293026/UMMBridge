# UMMBridge

Run [UnityModManager](https://github.com/newman55/unity-mod-manager) (UMM) mods under [MelonLoader](https://github.com/LavaGang/MelonLoader).

## Overview

UMMBridge loads UMM inside MelonLoader, transparently bridging the two mod ecosystems. It intercepts `UnityModManager.modsPath`, redirecting to `UMMMods/`, then boots UMM and its UI alongside MelonLoader's plugin pipeline.

## Dual-Harmony Architecture

UMM ships **0Harmony 2.3.6** (with its own MonoMod) while MelonLoader uses **HarmonyX** (a different MonoMod). Two MonoMod runtimes cannot write JMP detours on the same method entry — this is the core conflict UMMBridge resolves.

### Three-layer resolution

| Layer | Trigger | Behavior |
|-------|---------|----------|
| **Cecil rewriting** | Assembly load (`LoadFrom` interception) | Renames `0Harmony`→`0Harmony_UMM`, rewrites non-transpiler `Patch()` calls to `ProxyPatch` |
| **UpdateWrapper injection** | `CreateClassProcessor`/`PatchAll` path | IL injection at `PatchFunctions.UpdateWrapper()`: conflict detection → migration → fallback routing |
| **Harmony.Patch() prefix** | Direct `Harmony.Patch()` calls | HarmonyX prefix on `0Harmony_UMM.Harmony.Patch()`: intercepts transpiler calls, triggers migration |

### Patch routing

- **Non-transpiler assemblies** — Cecil-rewritten via `ProxyPatch`, applied through **HarmonyX**.
- **Transpiler assemblies** — Left untouched, run natively on **0Harmony_UMM**.
- **Migration** — When a transpiler patch arrives at a method already owned by ProxyPatch (HarmonyX), the *non-transpiler* patches that are already on HarmonyX are moved to 0Harmony_UMM, so that the transpiler and those non-transpiler patches share the same MonoMod detour. This works only if **all** patches on HarmonyX for that method are UMMBridge‑owned and contain no transpilers.
- **Fallback** — When migration is impossible (e.g., a MelonLoader mod owns the method, or the method already has a transpiler on HarmonyX), non‑transpiler patches are routed to HarmonyX, while transpiler patches are **silently skipped**.

### Coexistence

The following table summarizes what happens when two patches target the same method:

| Scenario | Patch Type | Result |
|----------|------------|--------|
| Both patches on **same runtime** (both HarmonyX or both 0Harmony_UMM) | Any (incl. transpilers) | ✅ **Always works** — multiple patches can stack on the same MonoMod. |
| One on HarmonyX, one on 0Harmony_UMM | Prefix/Postfix/Finalizer | ⚠️ **One wins** — whichever loads first takes effect; the second is blocked (or migrated if possible). |
| One on HarmonyX, one on 0Harmony_UMM | Transpiler | ❌ **Never works** — the transpiler patch is blocked because it cannot be migrated to HarmonyX and cannot coexist with a different MonoMod detour. |

**Key limitation:**  
Two MonoMod runtimes (HarmonyX and 0Harmony_UMM) **cannot** both write JMP detours on the same method. Therefore, any scenario where one patch comes from HarmonyX and the other from 0Harmony_UMM will result in **only the first loaded patch remaining effective**. The UMMBridge migration mechanism can only move *non‑transpiler* patches from HarmonyX to 0Harmony_UMM, but this is conditional and cannot resolve conflicts when the method already has a transpiler on either side.

### Limitations

- **Cross‑runtime transpiler conflict** — If a UMM mod with a transpiler tries to patch a method that is already patched (even with a simple prefix) by a MelonLoader mod (HarmonyX), the transpiler patch will be **silently ignored**. This is unavoidable due to the incompatible `CodeInstruction` types between the two runtimes.
- **Load‑order dependency** — To maximize compatibility, ensure that **transpiler‑containing UMM mods load before** any non‑transpiler mods that patch the same methods. This allows the transpiler to claim the method on 0Harmony_UMM, and later non‑transpiler patches can be added via migration.
- **No perfect coexistence** — There is no way to make a HarmonyX‑based patch and a 0Harmony_UMM‑based patch both apply to the same method simultaneously. The design prioritizes stability over universal coexistence.

## Usage

1. Drop the compiled DLL into ADOFAI's `Plugins/` directory
2. Place UMM mods into `UMMMods/` (created automatically on first launch)
3. Open the UMM window and click **Mods Dir** at the top-right

## Build


```
dotnet build UMMBridge\UMMBridge.csproj -c Release
```

Or open `UMMBridge.slnx` in Visual Studio 2022. The `Deploy` configuration outputs directly to the game's Plugins directory.

<img width="1282" height="752" alt="image" src="https://github.com/user-attachments/assets/04447190-7fb8-4254-8091-63512e2deda0" />
