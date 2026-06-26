using System.Collections.Generic;
using Game.Components.Common;
using GameData.Domains.CombatSkill;
using HarmonyLib;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 突破自动选择总纲/篇章补丁 —— 选功法后自动选已读的总纲和正/逆练篇章。
    ///
    /// 【问题背景】
    /// 突破功法时，原版用 activationState（未激活状态）算选中 → 未突破的功法全部未激活 → 选中为空。
    /// 玩家需要手动逐个点击总纲和篇章，操作繁琐。
    ///
    /// 【解决方案】
    /// patch CombatSkillPanel.UpdateBreakPanel（选功法后刷新总纲/篇章面板的入口），
    /// 用 ReadingState（已读状态）替代 activationState 来自动补选。
    ///
    /// 【ReadingState 位掩码结构】
    ///   ushort 类型，共 15 个有效位，每页占 1 位：
    ///   - bit 0-4：总纲 5 页（outline）
    ///   - bit 5-9：正练篇章 5 页（normal，pageId 1-5）
    ///   - bit 10-14：逆练篇章 5 页（reverse，pageId 1-5）
    ///   通过 CombatSkillStateHelper.IsPageRead(readingState, internalIndex) 判断某页是否已读。
    ///
    /// 【完整逻辑链】
    ///   玩家在修行界面选择功法
    ///     ↓
    ///   CombatSkillPanel.Set → UpdateBreakPanel（刷新总纲/篇章面板）
    ///     ↓
    ///   原版 UpdateBreakPanel：用 activationState 计算选中 → 未突破时全为空
    ///     ↓
    ///   本 postfix（UpdateBreakPanel_Postfix）：
    ///     读取 ReadingState（已读）→ 总纲没选则随机选一个已读的 →
    ///     篇章没选则优先选正练已读，否则逆练已读
    ///     （已选的不覆盖，尊重玩家手动选择）
    ///
    /// 【选择优先级】
    ///   总纲：从已读的总纲中随机选一个（多个已读时随机性增加趣味）
    ///   篇章：pageId 1-5 逐个检查 → 优先选正练已读（bit 5-9）→ 否则选逆练已读（bit 10-14）
    ///   已选的总纲/篇章不覆盖（CToggleGroup.GetActiveIndex() >= 0 或 GetIsOnCount() > 0 表示已选）
    /// </summary>
    [HarmonyPatch]
    internal static class BreakSelectPatch
    {
        #region 反射缓存

        /// <summary>
        /// CombatSkillPanel._skillData —— 功法练习数据（CombatSkillPracticeDisplayData），
        /// 包含 CombatSkillDisplayData.ReadingState 位掩码，用于判断各页是否已读。
        /// </summary>
        private static AccessTools.FieldRef<CombatSkillPanel, CombatSkillPracticeDisplayData>? _refPracticeSkillData;

        /// <summary>
        /// 建立反射缓存。在 ModMain.Initialize() 中调用。
        /// </summary>
        internal static void Init()
        {
            try
            {
                _refPracticeSkillData = AccessTools.FieldRefAccess<CombatSkillPanel, CombatSkillPracticeDisplayData>("_skillData");
                Debug.Log($"[{ModMain.LogTag}] 突破自动选择反射缓存：skillData={_refPracticeSkillData != null}");
            }
            catch (System.Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 突破自动选择反射缓存初始化异常: {ex.Message}");
            }
        }

        #endregion

        #region Harmony Patch

        /// <summary>
        /// CombatSkillPanel.UpdateBreakPanel 的 postfix。
        ///
        /// 【触发时机】修行界面选功法后，CombatSkillPanel.Set 调用 UpdateBreakPanel 刷新总纲/篇章面板。
        /// 原版用 activationState（未激活）算选中 → 未突破时全为空。本 postfix 用 ReadingState（已读）补选。
        /// </summary>
        [HarmonyPatch(typeof(CombatSkillPanel), "UpdateBreakPanel")]
        [HarmonyPostfix]
        internal static void UpdateBreakPanel_Postfix(CombatSkillPanel __instance)
        {
            ModMain.LogDebug("UpdateBreakPanel_Postfix 触发");
            if (!ModMain.GetSettingBool("AutoBreakSelect", true)) return;
            if (__instance == null) return;
            if (_refPracticeSkillData == null) return;

            try
            {
                DoBreakAutofill(__instance);
            }
            catch (System.Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] UpdateBreakPanel postfix 异常: {ex.Message}");
            }
        }

        #endregion

        #region 核心算法

        /// <summary>
        /// 自动选择总纲和篇章。
        ///
        /// 【总纲选择】
        ///   遍历 outlinePageToggleGroup（CToggleGroup，单选）的 5 个总纲项，
        ///   用 CombatSkillStateHelper.IsPageRead 判断 bit0-4 是否已读，
        ///   收集已读总纲的索引列表 → 未选时随机选一个。
        ///
        /// 【篇章选择】
        ///   遍历 pageId 1-5，逐个检查：
        ///   - 正练已读（bit 5-9）→ 优先选正练侧的 toggle（索引 pageId-1）
        ///   - 否则逆练已读（bit 10-14）→ 选逆练侧的 toggle（索引 pageId+4）
        ///   用 SelectWithoutNotify 选中（不触发额外的 OnValueChanged 事件）。
        ///
        /// 【已选不覆盖】
        ///   总纲：CToggleGroup.GetActiveIndex() >= 0 表示已选 → 跳过
        ///   篇章：CToggleGroupMultiSelect.GetIsOnCount() > 0 表示已选 → 跳过
        ///   这保证了玩家手动选择的总纲/篇章不会被自动选择覆盖。
        /// </summary>
        private static void DoBreakAutofill(CombatSkillPanel panel)
        {
            var skillData = _refPracticeSkillData!(panel);
            if (skillData?.CombatSkillDisplayData == null) return;
            ushort readingState = skillData.CombatSkillDisplayData.ReadingState;

            var outlineTg = panel.outlinePageToggleGroup;   // CToggleGroup（单选）
            var otherTg = panel.otherPageToggleGroup;       // CToggleGroupMultiSelect（多选）

            // ---- 总纲：bit0-4，找已读的 ----
            var readOutlines = new List<sbyte>();
            for (sbyte i = 0; i < outlineTg.Count(); i++)
            {
                if (CombatSkillStateHelper.IsPageRead(readingState, CombatSkillStateHelper.GetOutlinePageInternalIndex(i)))
                    readOutlines.Add(i);
            }
            // 一个总纲都没读 → 直接结束，不碰篇章（无法确定选哪个总纲，篇章也无意义）
            if (readOutlines.Count == 0) return;

            // 总纲已选就不动（CToggleGroup.GetActiveIndex() >= 0 表示已选）
            if (outlineTg.GetActiveIndex() < 0)
            {
                // 多个已读时随机选一个，增加趣味性
                sbyte pick = readOutlines.Count == 1 ? readOutlines[0] : readOutlines[Random.Range(0, readOutlines.Count)];
                outlineTg.Set(pick);
                ModMain.LogDebug($"突破自动选总纲：index={pick}");
            }

            // ---- 篇章：pageId 1-5，正练 bit(5+i) 优先，否则逆练 bit(10+i) ----
            // 篇章已选就不动（CToggleGroupMultiSelect 任一已选即跳过）
            if (otherTg.GetIsOnCount() == 0)
            {
                for (byte pageId = 1; pageId <= 5; pageId++)
                {
                    // 正练页的 internalIndex：bit 5-9
                    byte directIdx = CombatSkillStateHelper.GetNormalPageInternalIndex(0, pageId);
                    // 逆练页的 internalIndex：bit 10-14
                    byte reverseIdx = CombatSkillStateHelper.GetNormalPageInternalIndex(1, pageId);
                    if (CombatSkillStateHelper.IsPageRead(readingState, directIdx))
                        otherTg.SelectWithoutNotify(pageId - 1);   // 正练侧 toggle 索引 0-4 对应 pageId 1-5
                    else if (CombatSkillStateHelper.IsPageRead(readingState, reverseIdx))
                        otherTg.SelectWithoutNotify(pageId + 4);   // 逆练侧 toggle 索引 5-9
                }
                ModMain.LogDebug("突破自动选篇章完成（优先正练）");
            }
        }
        #endregion
    }
}
