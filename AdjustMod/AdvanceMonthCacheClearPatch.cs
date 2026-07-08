using HarmonyLib;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 过月清缓存补丁 —— 过月动画隐藏时清空 NPC 书籍阅读状态缓存。
    ///
    /// 【为什么需要】BookReadStatusPatch 用 ReadStateCache 缓存 NPC 阅读状态，
    /// 缓存只在 mod 卸载时清空。但游戏过程中 NPC 会读书（过月时推进阅读进度），
    /// 不清缓存会导致玩家看到的还是旧状态。过月完成后清空，下次悬停重新查。
    ///
    /// 【patch 点】UI_Advance.HideAdvanceMonth 是过月动画隐藏的方法（private），
    /// 在此触发 UiEvents.OnAdvanceMonthAnimationComplete 事件，是过月结束的可靠标志。
    /// </summary>
    [HarmonyPatch(typeof(UI_Advance), "HideAdvanceMonth")]
    internal static class AdvanceMonthCacheClearPatch
    {
        [HarmonyPostfix]
        internal static void Postfix()
        {
            ModMain.ReadStateCache.Clear();
            ModMain.PendingQueries.Clear();
            ModMain.LogDebug("过月完成，已清空 NPC 阅读状态缓存");
        }
    }
}
