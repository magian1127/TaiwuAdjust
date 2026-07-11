using System;
using System.Reflection;
using Game.Views.MouseTips.Item.Common;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 物品浮窗优化补丁 —— 默认显示详情 + Alt 单次键切换简详。
    ///
    /// 影响范围：所有继承自 <see cref="TooltipItemBase"/> 的浮窗，包括装备、书籍、材料等。
    /// 原版行为：按住 Alt 显示详细，松开回到简略。
    /// 本补丁改为：默认显示详细，按一次 Alt 切换简详。
    ///
    /// 实现说明：
    ///   - 把 Alt 检测放在 <see cref="TooltipItemBase.UpdateDetail"/> 的 Prefix 里，
    ///     利用原版 <see cref="TooltipItemBase.Update"/> 每帧对 UpdateDetail() 的调用
    ///     作为"监控点"。
    ///   - 用 <see cref="Time.frameCount"/> 记录最近一次切换的帧号，避免同一帧内
    ///     UpdateDetail 被多次调用导致 Alt 被重复触发。
    ///   - 不再因为 HasStick 而跳过 Alt 检测，确保被 stick 到 PermanentTips 的提示
    ///     （如百晓册、固定提示）也能响应 Alt。
    /// </summary>
    [HarmonyPatch]
    internal static class EquipTooltipPatch
    {
        // TooltipItemBase 的受保护/私有字段反射缓存
        private static FieldInfo? _fRootDetail;       // rootDetail（详细模式面板的根 GameObject）
        private static FieldInfo? _fIsDetail;         // IsDetail（是否处于详细模式）
        private static FieldInfo? _fDisableDetail;    // DisableDetail（是否禁用详情模式）
        private static FieldInfo? _fOperationArea;    // operationArea（底部操作区域，含热键提示）
        private static FieldInfo? _fHkDetail;         // operationArea.hotkeyDisplayDetail

        /// <summary>当前详细模式状态。true = 显示详细，false = 显示简略。</summary>
        private static bool _detailMode = true;
        /// <summary>最近一次 Alt 切换发生的帧号，用于防止同一帧内多次切换。</summary>
        private static int _lastToggleFrame = -1;

        /// <summary>建立反射缓存。在 ModMain.Initialize() 中调用。</summary>
        internal static void Init()
        {
            try
            {
                var t = typeof(TooltipItemBase);
                _fRootDetail = AccessTools.Field(t, "rootDetail");
                _fIsDetail = AccessTools.Field(t, "IsDetail");
                _fDisableDetail = AccessTools.Field(t, "DisableDetail");
                _fOperationArea = AccessTools.Field(t, "operationArea");
                _fHkDetail = AccessTools.Field(typeof(Game.Views.MouseTips.Common.TooltipOperationArea), "hotkeyDisplayDetail");

                Debug.Log($"[{ModMain.LogTag}] 装备浮窗反射缓存：" +
                          $"rootDetail={_fRootDetail != null}, " +
                          $"IsDetail={_fIsDetail != null}, " +
                          $"DisableDetail={_fDisableDetail != null}, " +
                          $"operationArea={_fOperationArea != null}, " +
                          $"hotkeyDisplayDetail={_fHkDetail != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 装备浮窗反射缓存初始化异常: {ex.Message}");
            }
        }

        /// <summary>
        /// patch UpdateDetail：默认显示详情，按 Alt 切换简详。
        /// </summary>
        [HarmonyPatch(typeof(TooltipItemBase), "UpdateDetail")]
        [HarmonyPrefix]
        internal static bool UpdateDetail_Prefix(TooltipItemBase __instance)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return true;
            if (_fRootDetail == null || _fIsDetail == null || _fDisableDetail == null)
                return true;

            try
            {
                bool disableDetail = (bool)_fDisableDetail.GetValue(__instance)!;
                if (disableDetail) return false;

                // 检测 Alt 单次按下，切换 _detailMode
                int frame = Time.frameCount;
                if (frame != _lastToggleFrame &&
                    (Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt)))
                {
                    _detailMode = !_detailMode;
                    _lastToggleFrame = frame;
                    ModMain.LogDebug($"物品浮窗 Alt 切换：detailMode={_detailMode}, frame={frame}");
                }

                // 应用当前模式
                _fIsDetail.SetValue(__instance, _detailMode);
                var rootDetail = _fRootDetail.GetValue(__instance) as GameObject;

                if (rootDetail != null && rootDetail.activeSelf != _detailMode)
                {
                    rootDetail.SetActive(_detailMode);
                    __instance.Refresh();
                }

                // rootDetail 激活后，把内部的 HorizontalLayoutGroup 改为 VerticalLayoutGroup
                if (_detailMode && rootDetail != null && rootDetail.activeSelf)
                {
                    AdjustLayout(rootDetail);
                }

                return false; // 跳过原版
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] UpdateDetail prefix 异常: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// patch UpdateHotKeyDetail：把原版「按住/松开 Alt」提示改成「按 Alt 切换简详」。
        /// 由于 Alt 现在是切换而非按住，原版提示文字会让玩家困惑。
        ///
        /// 用 Postfix 等原版生成完子控件后直接修改文本，结构最简单。
        /// </summary>
        [HarmonyPatch(typeof(TooltipItemBase), "UpdateHotKeyDetail")]
        [HarmonyPostfix]
        internal static void UpdateHotKeyDetail_Postfix(TooltipItemBase __instance)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return;
            if (_fOperationArea == null || _fHkDetail == null) return;

            try
            {
                var area = _fOperationArea.GetValue(__instance);
                if (area == null) return;

                var hkDetail = _fHkDetail.GetValue(area) as Component;
                if (hkDetail == null) return;

                var layoutField = AccessTools.Field(hkDetail.GetType(), "layout");
                var layout = layoutField?.GetValue(hkDetail) as RectTransform;
                if (layout == null) return;

                // 遍历所有直接子对象，根据原文本关键词替换，保留 Alt 热键图标
                foreach (Transform child in layout)
                {
                    var text = child.GetComponent<TMPro.TMP_Text>();
                    if (text == null) continue;

                    string t = text.text;
                    if (t.Contains("按住") || t.Contains("松开"))
                        text.text = "按";
                    else if (t.Contains("查看详细") || t.Contains("回到简略"))
                        text.text = "切换简详";
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] UpdateHotKeyDetail postfix 异常: {ex.Message}");
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
