# Changelog

## v0.0.4

### 中文

- **双 Harmony 路由架构** — UMM 所有 Mod 固定使用 0Harmony_UMM（UMM 自带的 Harmony v2.3.6），MelonLoader 使用 HarmonyX，彻底消除两个 MonoMod 运行时在同一个方法上产生 detour 冲突的问题
- **Assembly 拦截器重构** — Hook Assembly.LoadFrom，通过 Harmony Prefix 在加载 UMM Mod 时进行 Mono.Cecil IL 改写
- **0Harmony → 0Harmony_UMM 重写** — 自动将 Mod 中对 0Harmony 的引用改为 0Harmony_UMM，确保使用 UMM 的 Harmony 运行时
- **Assembly.get_Location() 重写** — 替换为 Bridge.GetAssemblyLocation()，支持 ConditionalWeakTable + ThreadStatic 双路回退，让 byte-loaded 程序集能恢复原始文件路径
- **硬编码路径重写** — 自动将 Mod 中的 "Mods" 目录字符串替换为 "UMMMods"
- **PatchAllUncategorized → PatchAll 重写** — 兼容 Harmony 2.3.x 旧 API
- **HarmonyPatchCategory 跨运行时兼容** — 通过 TypeResolve 存根 + Cecil 扫描注册 Category，避免 HarmonyX 下 TypeLoadException
- **AssemblyResolve 缓存** — 避免重复加载程序集，线程安全
- **ModsDir 按钮** — UMM 窗口中新增 "Mods Dir" 按钮，一键打开 UMMMods 目录
- **已知限制** — 两个独立 MonoMod 运行时无法在同一方法上共存，0Harmony_UMM 的 detour 优先

### English

- **Dual-Harmony routing** — UMM mods always use 0Harmony_UMM (UMM's Harmony v2.3.6), MelonLoader uses HarmonyX, eliminating native detour conflicts between two MonoMod runtimes
- **Assembly interceptor refactored** — Hook Assembly.LoadFrom via Harmony prefix, applying Mono.Cecil IL rewriting when loading UMM mod assemblies
- **0Harmony → 0Harmony_UMM rewrite** — Redirect mod references from 0Harmony to 0Harmony_UMM at the assembly level
- **Assembly.get_Location() rewrite** — Replace with Bridge.GetAssemblyLocation(), backed by ConditionalWeakTable with ThreadStatic fallback for static constructors
- **Hardcoded path rewriting** — Automatically replace "Mods" strings with "UMMMods" in mod assemblies
- **PatchAllUncategorized → PatchAll rewrite** — Backward compatibility with Harmony 2.3.x API
- **Cross-runtime HarmonyPatchCategory** — TypeResolve stub + Cecil scanning to prevent TypeLoadException under HarmonyX
- **AssemblyResolve caching** — Thread-safe concurrent dictionary to prevent duplicate assembly loads
- **Mods Dir button** — New button in the UMM window to open the UMMMods directory
- **Known limitation** — Two independent MonoMod runtimes cannot share the same method's detour; 0Harmony_UMM detour takes priority

---

## v0.0.3

### 中文

- **Harmony 兼容层** — 运行时桥接 `PatchAllUncategorized` / `PatchCategory` 方法（通过 Harmony Prefix + Mono.Cecil IL 重写，在加载 UMM Mod 程序集时自动改写缺失的 Harmony 调用为 `PatchAll`）
- **HarmonyPatchCategory 存根** — 通过 `AppDomain.TypeResolve` 为携带 `[HarmonyPatchCategory]` 属性的 Mod 提供类型存根，避免 TypeLoadException
- **路径重定向** — 自动重写 UMM Mod 中硬编码的 `"Mods"` 目录字符串为 `"UMMMods"`（运行期 IL 改写），兼容不通过 `UnityModManager.modsPath` 获取路径的 Mod

### English

- **Harmony compatibility layer** — Runtime bridging for `PatchAllUncategorized` / `PatchCategory` methods via Harmony prefix + Mono.Cecil IL rewriting on assembly load
- **HarmonyPatchCategory stub** — Type stub via `AppDomain.TypeResolve` for mods using `[HarmonyPatchCategory]`, preventing TypeLoadException under HarmonyX
- **Path redirection** — Automatic rewriting of hardcoded `"Mods"` directory strings to `"UMMMods"` at the IL level
