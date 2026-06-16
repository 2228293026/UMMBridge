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

- **Non-transpiler assemblies** — Cecil-rewritten via `ProxyPatch`, applied through HarmonyX
- **Transpiler assemblies** — Left untouched, run natively on `0Harmony_UMM`
- **Migration** — When a transpiler patch arrives at a method already owned by ProxyPatch (HarmonyX), the non-transpiler patches are migrated to `0Harmony_UMM` so all UMM patches share one MonoMod detour
- **Fallback** — When migration is impossible (ML mod owns the method), non-transpiler patches are routed to HarmonyX, transpiler patches are silently skipped

### Coexistence

| Patch type | UMM + ML on same method | Two UMM on same method |
|------------|------------------------|----------------------|
| Prefix / Postfix / Finalizer | ✅ Always works | ✅ Always works |
| Transpiler | ❌ Cross-runtime fails (CodeInstruction type mismatch) | ✅ Same runtime — works natively |

Cross-runtime transpiler conflict on the same method is a **known limitation**: the two `HarmonyLib.CodeInstruction` types belong to different assemblies and cannot be mixed.

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
