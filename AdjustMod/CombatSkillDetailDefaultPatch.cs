using System.Reflection;
using Game.Views.MouseTips;
using HarmonyLib;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 功法浮窗默认显示详情 + Alt 单次键切换简详。
    ///
    /// 原版行为：按住 Alt 显示详细，松开回到简略。
    /// 本补丁改为：默认显示详细，按一次 Alt 切换简详，提示栏显示"按 Alt 切换简详"。
    /// </summary>
    [HarmonyPatch]
    internal static class CombatSkillTooltipPatch
    {
        /// <summary>pressTipLeftText 反射缓存。</summary>
        private static FieldInfo? _fPressTipLeftText;
        /// <summary>pressTipRightText 反射缓存。</summary>
        private static FieldInfo? _fPressTipRightText;
        /// <summary>RefreshDetailMode 反射缓存。</summary>
        private static MethodInfo? _mRefreshDetailMode;

        /// <summary>当前详细模式状态。true = 显示详细，false = 显示简略。</summary>
        private static bool _detailMode = true;

        internal static void Init()
        {
            var t = typeof(TooltipCombatSkill);
            _fPressTipLeftText = AccessTools.Field(t, "pressTipLeftText");
            _fPressTipRightText = AccessTools.Field(t, "pressTipRightText");
            _mRefreshDetailMode = AccessTools.Method(t, "RefreshDetailMode");
            Debug.Log($"[{ModMain.LogTag}] 功法浮窗反射缓存：" +
                      $"left={_fPressTipLeftText != null} " +
                      $"right={_fPressTipRightText != null} " +
                      $"refresh={_mRefreshDetailMode != null}");
        }

        /// <summary>
        /// 用 _detailMode 字段取代 Alt 键状态，控制详细/简略模式切换。
        /// </summary>
        [HarmonyPatch(typeof(TooltipCombatSkill), "IsDetailMode")]
        [HarmonyPostfix]
        private static void IsDetailMode_Postfix(ref bool __result)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return;
            __result = _detailMode;
        }

        /// <summary>
        /// 检测 Alt 单次按下，切换 _detailMode 并立即刷新浮窗。
        ///
        /// Prefix 在原文的 Alt 检测之前运行，避免原文的 isAltDown 与我们的
        /// 模式状态冲突。切换后立即调 RefreshDetailMode 更新 UI。
        /// </summary>
        [HarmonyPatch(typeof(TooltipCombatSkill), "Update")]
        [HarmonyPrefix]
        private static void Update_Prefix(TooltipCombatSkill __instance)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return;

            if (Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt))
            {
                _detailMode = !_detailMode;
                _mRefreshDetailMode?.Invoke(__instance, null);
            }
        }

        /// <summary>
        /// 自定义底部提示文本："按 Alt 切换简详"，取代原文的"按住/松开 Alt 查看/回到简略"。
        /// </summary>
        [HarmonyPatch(typeof(TooltipCombatSkill), "RefreshPressTip")]
        [HarmonyPrefix]
        private static bool RefreshPressTip_Prefix(TooltipCombatSkill __instance)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return true;

            if (_fPressTipLeftText?.GetValue(__instance) is TMPro.TMP_Text left)
                left.text = "按";
            if (_fPressTipRightText?.GetValue(__instance) is TMPro.TMP_Text right)
                right.text = "切换简详";

            return false;
        }
    }
}
