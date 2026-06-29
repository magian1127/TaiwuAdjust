using System;
using System.Reflection;
using Game.Views.MouseTips.Item.Common;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 装备浮窗优化补丁 —— 整合详情默认显示 + 布局纵向调整。
    ///
    /// 【功能一】默认显示全部详情
    ///   原版 TooltipItemBase.UpdateDetail() 每帧检查 Alt 键切换 IsDetail。
    ///   本补丁跳过 Alt 检查，直接设 IsDetail=true 并激活 rootDetail。
    ///
    /// 【功能二】隐藏「按住Alt」提示文字
    ///   因为 Alt 不再切换详情，底部的热键提示反而让玩家困惑，直接隐藏。
    ///
    /// 【功能三】注解面板放到详细信息下方
    ///   从日志得知浮窗结构：
    ///   [0]MainPanel ─ 基础信息（左列）
    ///   [1]DetailPanel(rootDetail) ─ 内部并排放【详细信息】和【注解】
    ///   把 DetailPanel 的 HorizontalLayoutGroup 改为 VerticalLayoutGroup，
    ///   注解就自然在详细信息下面了。
    /// </summary>
    [HarmonyPatch]
    internal static class EquipTooltipPatch
    {
        // TooltipItemBase 的受保护/私有字段反射缓存
        private static FieldInfo? _fRootDetail;       // rootDetail（详细模式面板的根 GameObject）
        private static FieldInfo? _fIsDetail;         // IsDetail（是否处于详细模式）
        private static FieldInfo? _fDisableDetail;    // DisableDetail（是否禁用详情模式）
        private static FieldInfo? _fIsInCompareUI;    // _isInCompareUI（是否在对比界面）
        private static FieldInfo? _fOperationArea;    // operationArea（底部操作区域，含热键提示）
        private static FieldInfo? _fHkDetail;         // operationArea.hotkeyDisplayDetail

        /// <summary>建立反射缓存。在 ModMain.Initialize() 中调用。</summary>
        internal static void Init()
        {
            try
            {
                var t = typeof(TooltipItemBase);
                _fRootDetail = AccessTools.Field(t, "rootDetail");
                _fIsDetail = AccessTools.Field(t, "IsDetail");
                _fDisableDetail = AccessTools.Field(t, "DisableDetail");
                _fIsInCompareUI = AccessTools.Field(t, "_isInCompareUI");
                _fOperationArea = AccessTools.Field(t, "operationArea");
                _fHkDetail = AccessTools.Field(typeof(Game.Views.MouseTips.Common.TooltipOperationArea), "hotkeyDisplayDetail");

                Debug.Log($"[{ModMain.LogTag}] 装备浮窗反射缓存：" +
                          $"rootDetail={_fRootDetail != null}, " +
                          $"IsDetail={_fIsDetail != null}, " +
                          $"DisableDetail={_fDisableDetail != null}, " +
                          $"_isInCompareUI={_fIsInCompareUI != null}, " +
                          $"operationArea={_fOperationArea != null}, " +
                          $"hotkeyDisplayDetail={_fHkDetail != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 装备浮窗反射缓存初始化异常: {ex.Message}");
            }
        }

        /// <summary>
        /// patch UpdateDetail：跳过 Alt 检查，默认显示全部详情。
        /// 激活 rootDetail 后顺便调整布局。
        /// </summary>
        [HarmonyPatch(typeof(TooltipItemBase), "UpdateDetail")]
        [HarmonyPrefix]
        internal static bool UpdateDetail_Prefix(TooltipItemBase __instance)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return true;
            if (_fRootDetail == null || _fIsDetail == null || _fDisableDetail == null || _fIsInCompareUI == null)
                return true;

            try
            {
                bool disableDetail = (bool)_fDisableDetail.GetValue(__instance)!;
                if (disableDetail) return false;

                bool hasStick = __instance.HasStick;
                bool isInCompareUI = (bool)_fIsInCompareUI.GetValue(__instance)!;
                if (hasStick && !isInCompareUI) return false;

                // 强制进入详情模式
                _fIsDetail.SetValue(__instance, true);
                var rootDetail = _fRootDetail.GetValue(__instance) as GameObject;

                if (rootDetail != null && !rootDetail.activeSelf)
                {
                    rootDetail.SetActive(true);
                    __instance.Refresh();
                    ModMain.LogDebug($"rootDetail激活 IsDetail=true");
                }

                // rootDetail 激活后，把内部的 HorizontalLayoutGroup 改为 VerticalLayoutGroup
                // AdjustLayout 内部自终止：HLG 被销毁后就不再执行
                if (rootDetail != null && rootDetail.activeSelf)
                {
                    AdjustLayout(rootDetail);
                }

                return false; // 跳过原版 Alt 检查
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] UpdateDetail prefix 异常: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// patch UpdateHotKeyDetail：隐藏底部「按住Alt查看详细」/「松开Alt回到简略」热键提示。
        /// </summary>
        [HarmonyPatch(typeof(TooltipItemBase), "UpdateHotKeyDetail")]
        [HarmonyPrefix]
        internal static bool UpdateHotKeyDetail_Prefix(TooltipItemBase __instance)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return true;
            if (_fOperationArea == null || _fHkDetail == null) return true;

            try
            {
                var area = _fOperationArea.GetValue(__instance);
                if (area == null) return false;

                // 只隐藏 hotkeyDisplayDetail（Alt提示），不碰 operationArea 本身，
                // 这样 G（锁定）等其他热键不受影响
                var hkDetail = _fHkDetail.GetValue(area) as Component;
                if (hkDetail != null)
                    hkDetail.gameObject.SetActive(false);

                return false;
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] UpdateHotKeyDetail prefix 异常: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 调整 tooltip 布局：
        /// 1. 根节点 HorizontalLayoutGroup → VerticalLayoutGroup（纵向排列）
        /// 2. 把注解面板从 rootDetail 搬出到根节点末尾（排在最下方）
        /// </summary>
        private static void AdjustLayout(GameObject rootDetail)
        {
            try
            {
                // 改 rootDetail 内部布局：HLG → VLG，让注解在详细信息下方
                // 根节点的 HLG 保持不变（MainPanel 在左，DetailPanel 在右）
                var hlg = rootDetail.GetComponent<HorizontalLayoutGroup>();
                if (hlg == null) return;

                ModMain.LogDebug($"rootDetail 内部布局：Horizontal→Vertical");
                UnityEngine.Object.DestroyImmediate(hlg);

                var vlg = rootDetail.AddComponent<VerticalLayoutGroup>();
                vlg.spacing = 2f;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;

                // 强制重建布局
                LayoutRebuilder.ForceRebuildLayoutImmediate(rootDetail.transform as RectTransform);
                for (int i = 0; i < rootDetail.transform.childCount; i++)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(
                        rootDetail.transform.GetChild(i) as RectTransform);

                ModMain.LogDebug($"布局调整完成");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 布局调整异常: {ex.Message}");
            }
        }
    }
}
