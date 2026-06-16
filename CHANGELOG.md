# Changelog

## v0.0.5

### 中文

- **跨运行时类型兼容** — 对 Creplay 执行 IL 重写，将 `sortedKeyQueue_instance` 类型及所有 `isinst PriorityQueue` 改为 `object`，绕过 CLR 类型身份检查；在 `ProxyPatch` 中增加 `NeedsMethodRedirect`，将泛型方法补丁重定向到游戏已加载的实例
- **程序集加载策略** — 改用 `LoadFrom` 临时文件加载，保留加载上下文，避免泛型类型分裂；`AssemblyResolve` 拦截 `Assembly-CSharp` 等核心程序集，直接返回已加载实例，防止重复加载
- **冲突迁移（Migration）** — 当 transpiler 补丁到达已被 ProxyPatch 占用的方法时，自动将 HarmonyX 上的非 transpiler 补丁迁移到 0Harmony_UMM，使同一运行时可叠加所有补丁
- **三层冲突解决** — UpdateWrapper IL 注入（CreateClassProcessor 路径）、Harmony.Patch() 前缀（直接 Patch 路径）、ProxyPatch 路由（Cecil 重写），三层兜底无死角
- **InvokeNativePatch 提取** — 统一原生补丁入口，解决加载顺序导致非 transpiler 补丁被静默丢弃的问题
- **MigratePatches 异常安全** — TryResolveConflict 层 catch，迁移失败走路由回退，不再崩模载
- **Creplay 消息显示** — 补丁 `TriggerMessage`，在游戏左上角显示消息，使用 `Time.unscaledTime` 不受暂停影响
- **Assembly.Read 异常保护** — 非 .NET 程序集跳过报错，不再抛 BadImageFormatException
- **已知限制** — 跨运行时 transpiler 补同一函数必失其一（CodeInstruction 类型属于不同程序集）

---

### English

- **Cross-runtime type compatibility** — IL-rewrite Creplay to change `sortedKeyQueue_instance` field type and all `isinst PriorityQueue` to `object`, bypassing CLR type identity checks; add `NeedsMethodRedirect` in `ProxyPatch` to redirect generic method patches to the game's actual instance
- **Assembly loading strategy** — Switch to `LoadFrom` via temp file to preserve load context, preventing generic type splitting; intercept `Assembly-CSharp` in `AssemblyResolve` to reuse already-loaded instances, avoiding duplicate loads
- **Conflict migration** — When a transpiler patch arrives at a method already claimed by ProxyPatch, non-transpiler patches on HarmonyX are automatically migrated to 0Harmony_UMM, allowing a single runtime to host all patches
- **Three-layer resolution** — UpdateWrapper IL injection (CreateClassProcessor path), Harmony.Patch() prefix (direct Patch path), ProxyPatch routing (Cecil rewrite) — no gap
- **InvokeNativePatch extracted** — Unified native entry point, fixing silent drop of non-transpiler patches due to load order
- **MigratePatches exception safety** — Caught in TryResolveConflict, routing fallback on failure, no crash
- **Creplay message display** — Patch `TriggerMessage` to show on-screen in top-left corner using unscaled time (unaffected by pause)
- **Assembly.Read guard** — Skip non-.NET assemblies with warning instead of BadImageFormatException
- **Known limitation** — Cross-runtime transpiler patches on the same method cannot coexist (CodeInstruction types belong to different assemblies)
---



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
