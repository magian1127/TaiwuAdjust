using System;
using System.Reflection;
using FrameWork;
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
///   - Alt 检测用边沿检测 CheckAltSinglePress（松开→按住才触发一次），避免
///     Windows 按键 auto-repeat 让状态反复翻转。_altWasDown 每帧最多清零一次，
///     防止百晓册高频 Init 间 Input.GetKey 间歇返回 false 误清边沿。
///   - 两个 Alt 监控点：
///     1. UpdateDetail Prefix（tooltip 固定后 Update 每帧调用 UpdateDetail）。
///     2. Init Postfix（百晓册未固定时 tooltip 被反复 Init，Init 比 UpdateDetail 更频繁）。
///   - Init Postfix 还负责把 Init 内 rootDetail.SetActive(false) 关掉的 detail 面板
///     立即拉回 _detailMode，消除闪烁过渡帧。
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
        /// <summary>上一帧 Alt 是否处于按住状态——做边沿检测，避免某些环境下
        /// Windows 按键自动重复 (auto-repeat) 让 GetKeyDown 反复返回 true。</summary>
        private static bool _altWasDown = false;
        /// <summary>最近一次更新 _altWasDown=false 的帧号——确保每帧最多清零一次，
        /// 避免同一帧内多次 Init 之间 Input.GetKey 短暂返回 false 误清边沿。</summary>
        private static int _lastAltCheckFrame = -1;

        /// <summary>
        /// 统一的 Alt 单次按下检测：做"从松开到按住"的边沿检测，
        /// 杜绝 Windows 按键 auto-repeat 让 GetKeyDown/getKey 反复触发的问题。
        ///
        /// 关键：_altWasDown 每帧最多更新一次（用 _lastAltCheckFrame 防一帧多次 Init 误清），
        /// 防止百晓册高频 Init 调用时 Input.GetKey 间歇性返回 false 导致边沿被误重置。
        /// </summary>
        internal static bool CheckAltSinglePress()
        {
            int frame = Time.frameCount;
            if (frame == _lastToggleFrame) return false;

            bool altDown = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

            // 边沿检测：只在"上一帧没按、这一帧按了"时触发
            if (altDown && !_altWasDown)
            {
                _altWasDown = true;
                _lastToggleFrame = frame;
                return true;
            }

            // 每帧最多更新一次 _altWasDown = false：
            // 只有当这一帧还没做过 Alt 状态检查、且当前 Alt 确实松开时才清零，
            // 避免百晓册同一帧内多次 Init 之间 Input.GetKey 短暂返回 false 误清边沿。
            if (!altDown && _lastAltCheckFrame != frame)
            {
                _altWasDown = false;
                _lastAltCheckFrame = frame;
            }
            return false;
        }

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
        /// patch Init Postfix：原版 Init 末尾会 rootDetail.SetActive(false)，百晓册未固定时
        /// tooltip 会被反复 Init（每秒数次），每次都把详情面板关掉，造成按 Alt "闪一下变回来"。
        /// 在此 Postfix 立即把 rootDetail.activeSelf 对齐到 _detailMode，消除闪烁过渡帧。
        /// 同时在此检测 Alt——百晓册未固定时 Init 比 UpdateDetail 调用更频繁，是更可靠的监控点。
        /// </summary>
        [HarmonyPatch(typeof(TooltipItemBase), "Init")]
        [HarmonyPostfix]
        internal static void Init_Postfix(TooltipItemBase __instance)
        {
            if (!ModMain.GetSettingBool("EquipDetailDefault", true)) return;
            if (_fRootDetail == null || _fIsDetail == null || _fDisableDetail == null) return;
            try
            {
                // 在 Init 后也检测 Alt，弥补百晓册未固定时 UpdateDetail 不被调用的情况
                if (CheckAltSinglePress())
                {
                    _detailMode = !_detailMode;
                    ModMain.LogDebug($"物品浮窗 Alt 切换(Init)：detailMode={_detailMode}, frame={Time.frameCount}");
                }

                bool disableDetail = (bool)_fDisableDetail.GetValue(__instance)!;
                if (disableDetail) return;

                _fIsDetail.SetValue(__instance, _detailMode);
                var rootDetail = _fRootDetail.GetValue(__instance) as GameObject;
                if (rootDetail != null && rootDetail.activeSelf != _detailMode)
                {
                    rootDetail.SetActive(_detailMode);
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] Init postfix 异常: {ex.Message}");
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
                if (CheckAltSinglePress())
                {
                    _detailMode = !_detailMode;
                    ModMain.LogDebug($"物品浮窗 Alt 切换：detailMode={_detailMode}, frame={Time.frameCount}");
                }

                // 应用当前模式
                bool curIsDetail = (bool)_fIsDetail.GetValue(__instance)!;
                if (curIsDetail != _detailMode)
                {
                    _fIsDetail.SetValue(__instance, _detailMode);
                }
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
