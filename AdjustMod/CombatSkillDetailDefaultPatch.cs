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
    ///
    /// 实现说明：
    ///   - 把 Alt 检测放在 <see cref="TooltipCombatSkill.IsDetailMode"/> 的 Prefix 里，
    ///     利用原版 <see cref="TooltipCombatSkill.Update"/> 每帧对 IsDetailMode() 的调用
    ///     作为"监控点"，而不是自己额外挂一个 Update Prefix。
    ///   - 用 <see cref="Time.frameCount"/> 记录最近一次切换的帧号，避免同一帧内
    ///     IsDetailMode() 被多次调用（Update / RefreshDetailMode / 各 RefreshXxx）
    ///     导致 Alt 被重复触发。
    ///   - IsDetailMode Prefix 直接返回自定义状态并跳过原版，因此即使 tooltip 被
    ///     stick 到 PermanentTips（百晓册常见情况），只要 Update 仍在运行就能响应 Alt。
    /// </summary>
    [HarmonyPatch]
    internal static class CombatSkillTooltipPatch
    {
        /// <summary>pressTipLeftText 反射缓存。</summary>
        private static FieldInfo? _fPressTipLeftText;
        /// <summary>pressTipRightText 反射缓存。</summary>
        private static FieldInfo? _fPressTipRightText;

        /// <summary>当前详细模式状态。true = 显示详细，false = 显示简略。</summary>
        private static bool _detailMode = true;
        /// <summary>最近一次 Alt 切换发生的帧号，用于防止同一帧内多次切换。</summary>
        private static int _lastToggleFrame = -1;

        internal static void Init()
        {
            var t = typeof(TooltipCombatSkill);
            _fPressTipLeftText = AccessTools.Field(t, "pressTipLeftText");
            _fPressTipRightText = AccessTools.Field(t, "pressTipRightText");
            Debug.Log($"[{ModMain.LogTag}] 功法浮窗反射缓存：" +
                      $"left={_fPressTipLeftText != null} " +
                      $"right={_fPressTipRightText != null}");
        }

        /// <summary>
        /// 用 _detailMode 字段取代 Alt 键状态，控制详细/简略模式切换。
        /// 在方法内部检测 Alt 单次按下，切换 _detailMode。
        /// </summary>
        [HarmonyPatch(typeof(TooltipCombatSkill), "IsDetailMode")]
        [HarmonyPrefix]
        private static bool IsDetailMode_Prefix(ref bool __result)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return true;

            int frame = Time.frameCount;
            if (frame != _lastToggleFrame &&
                (Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt)))
            {
                _detailMode = !_detailMode;
                _lastToggleFrame = frame;
                ModMain.LogDebug($"功法浮窗 Alt 切换：detailMode={_detailMode}, frame={frame}");
            }

            __result = _detailMode;
            return false; // 跳过原版的 TipsCommandKit.ViewDetailInfo.Check
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
