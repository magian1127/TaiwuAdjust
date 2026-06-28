using System.Collections.Generic;
using GameData.Domains.Mod;
using HarmonyLib;
using TaiwuModdingLib.Core.Plugin;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 「综合调整」前端插件入口。协调 4 个独立功能的 Harmony patch 注册与生命周期管理。
    ///
    /// 本插件运行在 Unity 进程（Mono，netstandard2.1），负责 UI 交互和 Harmony 补丁。
    /// 需要查询游戏数据（如 NPC 阅读状态）时，通过异步 Mod 方法调用后端插件
    /// （AdjustModBackend，独立 GameData.exe 进程，net8.0），不能直接访问 DomainManager。
    ///
    /// 功能清单（每个功能有独立的 Setting 开关，玩家可在游戏内随时切换）：
    ///   1. NPC 书籍阅读状态 —— 悬停 NPC 背包书籍时显示"此人已读/未读"
    ///   2. 制造自动填充资源 —— 选好装备材料后自动分配各类资源到上限
    ///   3. 突破自动选择总纲 —— 自动选已读的总纲和正/逆练篇章
    ///   4. 突破界面疗伤按钮 —— 在走格子界面动态创建疗伤按钮
    ///   5. 地块NPC悬停默认互动 —— 地图 NPC 列表悬停即默认显示互动信息（原版需按住 Shift）
    ///   6. 扁平标签栏 —— 角色界面底部标签展开为单排扁平标签，去掉所有二级悬停菜单。
    ///      （人物→队伍/关押、武具→车马、造诣→技艺/武学、关系→族谱、见闻→秘闻）
    ///
    /// 插件生命周期：游戏加载 MOD 时调用 Initialize() → 运行期间响应玩家操作 → 卸载时调用 Dispose()。
    /// Harmony patch 在 Initialize() 中注册，通过 [HarmonyPatch] 特性自动发现。
    /// </summary>
    [PluginConfig("AdjustMod", "Magian", "1.0.0.4")]
    public class ModMain : TaiwuRemakePlugin
    {
        #region 全局共享状态

        /// <summary>
        /// NPC 阅读状态缓存：[npcCharId][bookId] → readState[]（每页是否已读的 bool 数组）。
        ///
        /// 以「角色 + 书」为缓存键，与 TooltipBook 浮窗实例解耦——同一页书在不同浮窗实例间共享缓存。
        /// 缓存写入时机：后端查询回调到达时（见 BookReadStatusPatch.QueryNpcBookReadState）。
        /// 缓存读取时机：TooltipBookPage.Refresh postfix 中，命中则直接追加标签。
        /// 缓存清理：Dispose() 时清空（MOD 卸载）。
        /// </summary>
        internal static readonly Dictionary<int, Dictionary<int, bool[]>> ReadStateCache = new();

        /// <summary>
        /// 正在进行的后端查询去重表：存储 (npcCharId, bookId) 组合的 long 键。
        ///
        /// 同一 (NPC, 书) 只允许发起一次后端查询。TooltipBook.Refresh 触发时先查此表，
        /// 已在查询中则跳过，避免重复发起异步调用。查询回调完成后由 finally 块移除。
        /// </summary>
        internal static readonly HashSet<long> PendingQueries = new();

        /// <summary>
        /// 太吾生成的 Mod 标识字符串（格式 "0_N"，N 随加载顺序变化，不可读）。
        ///
        /// 用途：Harmony 实例的唯一 ID、ModManager.GetSetting 的 modId 参数、
        /// 后端 DomainManager.Mod.AddModMethod 的 modIdStr 参数。
        /// 在 Initialize() 中从 ModIdStr 属性赋值。
        /// </summary>
        internal static string _modIdStr = "";

        /// <summary>
        /// 日志前缀常量，用于 Debug.Log 输出的统一标签（如 "[AdjustMod] ..."）。
        /// 与 _modIdStr 解耦：_modIdStr 是太吾生成的 "0_N" 格式，序号会变且不可读；
        /// LogTag 固定为可读字符串，便于在 Player.log 中搜索和识别。
        /// </summary>
        internal const string LogTag = "AdjustMod";

        #endregion

        #region 生命周期

        /// <summary>
        /// 插件初始化。游戏加载 MOD 时由太吾调用，是所有功能的起点。
        ///
        /// 执行顺序：
        ///   1. 缓存 ModIdStr → _modIdStr（后续 GetSetting / AddModMethod 需要）
        ///   2. 各功能模块 Init()：建立反射缓存（FieldInfo / FieldRefAccess / MethodInfo），
        ///      缓存在 patch 触发前一次性建立，避免运行时重复反射的性能开销
        ///   3. harmony.PatchAll()：扫描所有 [HarmonyPatch] 特性的类，注册 postfix/prefix
        ///   4. 输出启动日志，确认 MOD 加载成功
        ///
        /// 为什么先 Init() 再 PatchAll()：Init() 中的反射缓存是 patch 运行时的前置依赖，
        /// 如果缓存为 null，patch 会在运行时安全跳过（if (_ref == null) return），
        /// 但不会报错——这比 patch 挂上但运行时抛异常更安全。
        /// </summary>
        public override void Initialize()
        {
            _modIdStr = ModIdStr;

            // 各模块建立反射缓存（不注册 patch，只缓存 FieldInfo/MethodInfo）
            BookReadStatusPatch.Init();
            CraftAutofillPatch.Init();
            BreakSelectPatch.Init();
            HealButtonPatch.Init();
            MapBlockCharHoverPatch.Init();

            // 扫描所有 [HarmonyPatch] 特性的类，注册到 Harmony
            var harmony = new Harmony(ModIdStr);
            harmony.PatchAll();

            Debug.Log($"[{LogTag}] 已加载完成");
        }

        /// <summary>
        /// 插件卸载。游戏卸载 MOD 时调用。
        /// 清空阅读状态缓存和查询去重表，释放引用，避免内存泄漏。
        /// Harmony patch 由游戏进程自动回收，无需手动 UnpatchAll。
        /// </summary>
        public override void Dispose()
        {
            ReadStateCache.Clear();
            PendingQueries.Clear();
        }

        #endregion

        #region 公共工具方法（供各 Patch 模块调用）

        /// <summary>
        /// 读取 bool 类型的 Mod 设置项。
        ///
        /// 设计决策：每次 patch 触发时读，不缓存。原因：
        ///   - ModManager.GetSetting 是字典查找，开销可忽略
        ///   - 不缓存 = 玩家改设置后即时生效，无需重启或实现 OnModSettingUpdate 回调
        ///   - 读不到（key 不存在或游戏未注入）时返回 defaultValue，保证功能可用
        /// </summary>
        /// <param name="key">设置项键名，对应 Config.lua → DefaultSettings 中的 Key</param>
        /// <param name="defaultValue">读不到时的默认值</param>
        /// <returns>设置值，或 defaultValue</returns>
        internal static bool GetSettingBool(string key, bool defaultValue)
        {
            try
            {
                bool val = defaultValue;
                return ModManager.GetSetting(_modIdStr, key, ref val) ? val : defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// 调试日志：仅当「调试模式」设置开启时输出到 Player.log。
        ///
        /// 两级日志策略：
        ///   - Debug.Log（常开）：用于 MOD 加载确认、patch 注册确认等关键里程碑
        ///   - LogDebug（按需）：用于运行时明细（填充结果、查询过程、按钮状态等），
        ///     避免正常运行时日志过多，排查问题时再开启
        ///
        /// 日志位置：C:\Users\&lt;用户名&gt;\AppData\LocalLow\Conchship\The Scroll of Taiwu\Player.log
        /// </summary>
        /// <param name="msg">日志内容</param>
        internal static void LogDebug(string msg)
        {
            if (GetSettingBool("DebugMode", false))
                Debug.Log($"[{LogTag}] {msg}");
        }

        /// <summary>
        /// 将 (npcCharId, bookId) 组合为单个 long 值，用作 HashSet 的去重键。
        /// 高 32 位放 charId，低 32 位放 bookId（unsigned），保证唯一性。
        /// </summary>
        internal static long MakeQueryKey(int charId, int bookId) => ((long)charId << 32) | (uint)bookId;

        /// <summary>
        /// 判断指定 charId 是否为太吾（主角）。
        ///
        /// 主角的书籍阅读进度由原版 UI 直接显示，本插件只为 NPC 追加状态标签。
        /// 所有涉及 NPC 阅读状态的 patch 都需要先调用此方法排除主角，
        /// 避免重复显示（原版已有 + 插件追加 = 重复）。
        /// </summary>
        /// <returns>true = 是主角，应跳过插件处理</returns>
        internal static bool IsTaiwu(int charId)
        {
            try
            {
                return charId > 0 && charId == SingletonObject.getInstance<BasicGameData>().TaiwuCharId;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
