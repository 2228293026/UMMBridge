# Changelog

## v0.0.3

- **Harmony 兼容层** — 运行时桥接 `PatchAllUncategorized` / `PatchCategory` 方法（通过 Harmony Prefix + Mono.Cecil IL 重写，在加载 UMM Mod 程序集时自动改写缺失的 Harmony 调用为 `PatchAll`）
- **HarmonyPatchCategory 存根** — 通过 `AppDomain.TypeResolve` 为携带 `[HarmonyPatchCategory]` 属性的 Mod 提供类型存根，避免 TypeLoadException
- **路径重定向** — 自动重写 UMM Mod 中硬编码的 `"Mods"` 目录字符串为 `"UMMMods"`（运行期 IL 改写），兼容不通过 `UnityModManager.modsPath` 获取路径的 Mod
