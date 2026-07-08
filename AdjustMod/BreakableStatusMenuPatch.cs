using System.Collections.Generic;
using System.Linq;
using Game.Components.SortAndFilter;
using Game.Components.SortAndFilter.CombatSkill.Simplified;
using GameData.Domains.CombatSkill;
using HarmonyLib;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 给突破/修行界面筛选面板的「功法状态」菜单追加「可突破」选项。
    ///
    /// 【背景】
    /// 突破选功法时，可突破的功法（已读总纲 + 已读满足条件篇章 + 未突破）混在长列表里不好找。
    /// 旧方案是置顶排序，但与原版 UpdateSortAndFilter 结尾的 ScrollTo 动画冲突导致滚动跳动（已删）。
    /// 新方案：在现有「功法状态」菜单（全部/已突破/未突破/已运功/未运功/独创心法）里加「可突破」，
    /// 玩家勾选后列表只剩可突破功法，完全走原生筛选路径，零副作用。
    ///
    /// 【patch 目标】Game.Components.SortAndFilter.CombatSkill.Simplified.CombatSkillStatusMenu
    ///   原版选项 index 0-4：已突破/未突破/已运功/未运功/独创心法(峨眉加成)
    ///   追加 index 5：可突破
    ///
    /// 【patch 两个方法】
    ///   1. GetMenuItemConfigs 的 Postfix：在返回列表末尾追加「可突破」选项
    ///   2. IsDataMatch 的 Postfix：扩展 Or 逻辑，index 5 被选中时追加可突破判定
    /// </summary>
    [HarmonyPatch(typeof(CombatSkillStatusMenu))]
    internal static class BreakableStatusMenuPatch
    {
        /// <summary>「可突破」选项在菜单中的 index（原版 0-4 之后第一个空闲位）。</summary>
        private const int BreakableIndex = 5;

        /// <summary>
        /// GetMenuItemConfigs 的 Postfix：在原版 5 个选项后追加「可突破」。
        ///
        /// 原版返回 new List{ 已突破, 未突破, 已运功, 未运功, 独创心法 }。
        /// 我们在 __result 末尾 Add 一个「可突破」FilterDropdownItemConfig。
        /// 直接传中文字符串，StringKey 有 implicit operator (string) 走 DirectString 路径。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(CombatSkillStatusMenu.GetMenuItemConfigs))]
        internal static void GetMenuItemConfigs_Postfix(ref List<FilterDropdownItemConfig> __result)
        {
            if (__result == null) __result = new List<FilterDropdownItemConfig>();
            __result.Add(new FilterDropdownItemConfig("可突破"));
        }

        /// <summary>
        /// IsDataMatch 的 Postfix：扩展 Or 逻辑处理 index 5（可突破）。
        ///
        /// 【Or 语义】原方法遍历 selectedIndices，任一选项匹配即返回 true，全部不匹配返回 false。
        /// 我们在原方法结果上追加判定：
        ///   - 若原方法已 true（其他选项命中）→ 保持 true，不动
        ///   - 若原方法 false，且玩家勾了「可突破」(index 5) 且功法可突破 → 改为 true
        /// 这保证了与原版 Or 逻辑的一致性：勾多个选项时，任一命中即保留。
        ///
        /// 【参数】原方法签名 IsDataMatch(CombatSkillDisplayDataForList data, IReadOnlyCollection&lt;int&gt; selectedIndices)。
        /// Postfix 通过同名参数（__2 即第二个参数，或用参数名）获取 selectedIndices 和 data。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(CombatSkillStatusMenu.IsDataMatch))]
        internal static void IsDataMatch_Postfix(
            CombatSkillDisplayDataForList data,
            IReadOnlyCollection<int> selectedIndices,
            ref bool __result)
        {
            // 原方法已命中其他选项，无需介入
            if (__result) return;
            // 玩家未勾选「可突破」，不介入
            if (selectedIndices == null || !selectedIndices.Contains(BreakableIndex)) return;

            __result = CanBreakSkill(data);
        }

        /// <summary>
        /// 可突破判定。与 PracticeCombatSkillItem.Set 中的判定逻辑完全一致：
        ///   1. !IsBrokenOut(ActivationState) —— 尚未突破
        ///   2. HasReadOutlinePages(ReadingState) —— 已读至少一个总纲
        ///   3. IsReadNormalPagesMeetConditionOfBreakout(ReadingState) —— 5 个篇章位各至少读了正/逆一篇
        ///   4. !Revoked —— 功法未被废弃
        /// </summary>
        private static bool CanBreakSkill(CombatSkillDisplayDataForList data)
        {
            return !CombatSkillStateHelper.IsBrokenOut(data.ActivationState)
                && CombatSkillStateHelper.HasReadOutlinePages(data.ReadingState)
                && CombatSkillStateHelper.IsReadNormalPagesMeetConditionOfBreakout(data.ReadingState)
                && !data.Revoked;
        }
    }
}
