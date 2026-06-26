using System;
using System.Reflection;
using GameData.Domains.Character;
using HarmonyLib;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 制造自动填充资源补丁 —— 选好装备材料后自动将各类资源分配到上限。
    ///
    /// 【问题背景】
    /// 制造装备时，原版选好材料后默认把资源投入量设为 _lastMakeResourceCountInts（上次值），
    /// 首次为 0，玩家需要手动调整每种资源的投入量，操作繁琐。
    ///
    /// 【解决方案】
    /// patch MakeSubPageMake.ResetResourceCount（选材料后的资源重置入口），
    /// 在原版设完上限后，自动按规则分配各类资源到上限，省去手动调整。
    ///
    /// 【完整逻辑链】
    ///   玩家点击材料（OnItemClickMaterial → SelectMaterial → ResetResourceCount）
    ///     ↓
    ///   原版 ResetResourceCount：
    ///     设置上限（_maxMakeResourceCountInts / _maxMakeResourceTotalCount）
    ///     找出主材料类型（_mainRequiredResourceType = 上限最高的，如玉石）
    ///     清零 _curMakeResourceCountInts
    ///     ↓
    ///   本 postfix（ResetResourceCount_Postfix）：
    ///     读取上限 → 计算分配方案 → 写入 _curMakeResourceCountInts 和 _lastMakeResourceCountInts
    ///     → 调用 RefreshResourcePanel 刷新显示
    ///
    /// 【资源分配算法】
    ///   1. 统计有上限的材料种类数 → 只有 1 种时原版已自动填满，不干预
    ///   2. 非主材料（Wood/Metal/Fabric 等）各填到 min(类型上限, 剩余额度)
    ///   3. 剩余额度全给主材料（_mainRequiredResourceType，上限最高的，如玉石）
    ///   4. 同时写入 _lastMakeResourceCountInts（影响下次选同材料时的默认值）
    ///
    /// 【技术要点：struct 写回】
    ///   ResourceInts 是 struct（值类型），不能直接通过属性赋值修改原对象的字段。
    ///   必须用 AccessTools.FieldRefAccess 拿 ref 引用，才能原地修改 MakeSubPageMake 上的 struct 字段。
    ///   如果只拿值拷贝修改，改的是副本，原对象不受影响。
    /// </summary>
    [HarmonyPatch]
    internal static class CraftAutofillPatch
    {
        #region 反射缓存
        // ResourceInts 是 struct，必须用 FieldRefAccess 拿 ref 才能原地修改。
        // 所有缓存在 Init() 中一次性建立，patch 运行时检查 null 后安全使用。

        /// <summary>_maxMakeResourceTotalCount —— 总物资投入上限（short 类型，如 300）</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, short>? _refMaxMakeResourceTotalCount;

        /// <summary>_maxMakeResourceCountInts —— 各类材料的投入上限（ResourceInts struct，6 种材料各一个上限）</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, ResourceInts>? _refMaxMakeResourceCountInts;

        /// <summary>_curMakeResourceCountInts —— 当前投入量（ResourceInts struct，本 patch 的核心修改目标）</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, ResourceInts>? _refCurMakeResourceCountInts;

        /// <summary>_lastMakeResourceCountInts —— 上次投入量（ResourceInts struct，影响下次选同材料时的默认值）</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, ResourceInts>? _refLastMakeResourceCountInts;

        /// <summary>_mainRequiredResourceType —— 主材料类型索引（sbyte，如 3 = 玉石，是上限最高的材料类型）</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, sbyte>? _refMainRequiredResourceType;

        /// <summary>MakeSubPageMake.RefreshResourcePanel() —— 刷新资源面板显示的方法</summary>
        private static MethodInfo? _miRefreshResourcePanel;

        /// <summary>
        /// 建立反射缓存。在 ModMain.Initialize() 中调用。
        ///
        /// 目标类型 Game.Views.Make.MakeSubPageMake 是当前游戏版本（1.0.20.x）的制造面板组件，
        /// 替代了老版本的 UI_Make（已废弃，patch 老类不会触发）。
        /// </summary>
        internal static void Init()
        {
            try
            {
                _refMaxMakeResourceTotalCount = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, short>("_maxMakeResourceTotalCount");
                _refMaxMakeResourceCountInts = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, ResourceInts>("_maxMakeResourceCountInts");
                _refCurMakeResourceCountInts = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, ResourceInts>("_curMakeResourceCountInts");
                _refLastMakeResourceCountInts = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, ResourceInts>("_lastMakeResourceCountInts");
                _refMainRequiredResourceType = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, sbyte>("_mainRequiredResourceType");
                _miRefreshResourcePanel = AccessTools.Method(typeof(Game.Views.Make.MakeSubPageMake), "RefreshResourcePanel", Type.EmptyTypes);
                Debug.Log($"[{ModMain.LogTag}] 制造自动填充反射缓存：{_refCurMakeResourceCountInts != null}, {_miRefreshResourcePanel != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 制造自动填充反射缓存初始化异常: {ex.Message}");
            }
        }

        #endregion

        #region Harmony Patch

        /// <summary>
        /// MakeSubPageMake.ResetResourceCount 的 postfix。
        ///
        /// 【触发时机】玩家在制造面板选择材料后，原版调用 ResetResourceCount 重置资源投入量。
        /// 原版流程：设上限 → 找主材料类型 → 清零 cur → 用 last 填充 cur（首次 last=0 → 全 0）。
        /// 本 postfix 在原版完成后覆盖 cur 为自动分配结果。
        ///
        /// 【为什么 patch 而不是直接调用？】
        ///   ResetResourceCount 是原版的正常流程入口，patch 它能确保在正确的时机执行
        ///   （上限已设好、主材料类型已确定），不需要自己重复这些前置步骤。
        /// </summary>
        [HarmonyPatch(typeof(Game.Views.Make.MakeSubPageMake), "ResetResourceCount")]
        [HarmonyPostfix]
        internal static void ResetResourceCount_Postfix(Game.Views.Make.MakeSubPageMake __instance)
        {
            ModMain.LogDebug("ResetResourceCount_Postfix 触发");
            if (!ModMain.GetSettingBool("AutoFillCraftMaterial", true)) return;
            if (__instance == null) return;
            // 检查所有反射缓存是否可用，任一为 null 则跳过（初始化失败）
            if (_refCurMakeResourceCountInts == null || _refMaxMakeResourceCountInts == null
                || _refMaxMakeResourceTotalCount == null || _miRefreshResourcePanel == null
                || _refMainRequiredResourceType == null || _refLastMakeResourceCountInts == null) return;

            try
            {
                DoCraftAutofill(__instance);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] ResetResourceCount postfix 异常: {ex.Message}");
            }
        }

        #endregion

        #region 核心算法

        /// <summary>
        /// 自动填充核心逻辑。
        ///
        /// 【算法步骤】
        ///   1. 读取上限：maxTotal（总上限）、maxPerType（各类型上限）、mainType（主材料类型索引）
        ///   2. 统计有上限的材料种类数 → 只有 1 种时原版已自动填满，不干预
        ///   3. 分配非主材料：各填到 min(类型上限, 剩余额度)
        ///   4. 剩余额度全给主材料
        ///   5. 写入 cur（当前投入量）和 last（下次默认值）
        ///   6. 调用 RefreshResourcePanel 刷新显示
        ///
        /// 【ResourceInts 的 Get/Set 方法】
        ///   ResourceInts 是 struct，内部用 int[6] 存储 6 种材料的值。
        ///   Get(index) 返回指定类型的值，Set(index, value) 设置指定类型的值。
        ///   索引对应关系：0-5 分别对应 6 种材料类型（具体映射由游戏定义）。
        /// </summary>
        private static void DoCraftAutofill(Game.Views.Make.MakeSubPageMake make)
        {
            try
            {
                short maxTotal = _refMaxMakeResourceTotalCount!(make);
                ref var maxPerType = ref _refMaxMakeResourceCountInts!(make);
                ref var cur = ref _refCurMakeResourceCountInts!(make);
                ref var last = ref _refLastMakeResourceCountInts!(make);
                sbyte mainType = _refMainRequiredResourceType!(make);

                if (maxTotal <= 0) return;

                // 统计有上限的材料种类数；只有 1 种时原版已自动填满，不必干预
                int typeCount = 0;
                for (sbyte t = 0; t < 6; t++) if (maxPerType.Get(t) > 0) typeCount++;
                if (typeCount <= 1) return;

                var target = new int[6];
                int budget = maxTotal;

                // 步骤1：非主材料各填到 min(类型上限, 剩余额度)
                for (sbyte t = 0; t < 6; t++)
                {
                    if (t == mainType) continue;
                    int cap = maxPerType.Get(t);
                    if (cap <= 0 || budget <= 0) { target[t] = 0; continue; }
                    int take = Math.Min(cap, budget);
                    target[t] = take;
                    budget -= take;
                }

                // 步骤2：剩余额度全给主材料（上限最高的材料类型，如玉石）
                if (budget > 0)
                {
                    int cap = maxPerType.Get(mainType);
                    target[mainType] = cap > 0 ? Math.Min(cap, budget) : 0;
                }
                else target[mainType] = 0;

                // 写入 cur（当前投入量）和 last（下次选同材料时的默认值）
                for (int t = 0; t < 6; t++)
                {
                    cur.Set(t, target[t]);
                    last.Set(t, target[t]);
                }

                // 刷新资源面板显示（不调此方法的话 UI 不会更新）
                _miRefreshResourcePanel!.Invoke(make, null);

                ModMain.LogDebug($"制造自动填充：main={mainType}, 各类=[{target[0]},{target[1]},{target[2]},{target[3]},{target[4]},{target[5]}], 总额上限={maxTotal}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] DoCraftAutofill 异常: {ex.Message}");
            }
        }

        #endregion
    }
}
