using System;
using FrameWork;
using FrameWork.UISystem.UIElements;
using Game.Views.Bottom;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 右上角村名人数点击补丁 —— 点击村名人数或图标直接打开村民名册。
    ///
    /// 【问题背景】
    /// 右上角 ViewResourceBar 的「Population（村名人数）」ResourceItem 没有 CButton 组件，
    /// 因此不可点击。玩家想查看村民名册需要从其他入口（如角色界面）进入，体验不顺畅。
    ///
    /// 【解决方案】
    /// 在 ViewResourceBar.Awake 后置补丁中为 Population 的 GameObject 动态添加
    /// UIInteractionBehaviour 和 CButton 组件，注册点击事件打开 UIElement.TaiwuVillagers。
    ///
    /// 【为什么选 Awake 而不是其他时机？】
    ///   Awake 在界面初始化时执行一次，此时 ResourceItem 的组件已就绪。
    ///   Refresh 会被频繁调用（每次数值更新都触），不必要重复添加组件。
    /// </summary>
    [HarmonyPatch]
    internal static class PopulationClickPatch
    {
        #region 反射缓存

        /// <summary>
        /// ViewResourceBar.population —— 村名人数 ResourceItem（private 序列化字段）。
        /// 用于获取 Population GameObject，在其上添加点击组件。
        /// </summary>
        private static AccessTools.FieldRef<ViewResourceBar, ResourceItem>? _refPopulation;

        /// <summary>
        /// 建立反射缓存。在 ModMain.Initialize 中调用。
        /// </summary>
        internal static void Init()
        {
            try
            {
                _refPopulation = AccessTools.FieldRefAccess<ViewResourceBar, ResourceItem>("population");
                Debug.Log($"[{ModMain.LogTag}] 人口点击反射缓存：population={_refPopulation != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 人口点击反射缓存初始化异常: {ex.Message}");
            }
        }

        #endregion

        #region Harmony Patch

        /// <summary>
        /// ViewResourceBar.Awake 后置补丁。
        ///
        /// 【触发时机】ViewResourceBar 初始化时，Awake 执行完成后。
        /// 此时所有子控件的 Awake（包括 ResourceItem 的）已经执行过。
        ///
        /// 【执行逻辑】
        ///   1. 检查设置开关，关闭则跳过
        ///   2. 获取 population ResourceItem
        ///   3. 如果已有 CButton 则跳过（避免重复添加）
        ///   4. 添加 UIInteractionBehaviour + CButton
        ///   5. 注册点击事件 → UIManager.Instance.MaskUI(UIElement.TaiwuVillagers)
        /// </summary>
        [HarmonyPatch(typeof(ViewResourceBar), "Awake")]
        [HarmonyPostfix]
        internal static void ViewResourceBar_Awake_Postfix(ViewResourceBar __instance)
        {
            if (!ModMain.GetSettingBool("PopulationClick", true)) return;
            if (_refPopulation == null) return;

            try
            {
                var population = _refPopulation(__instance);
                if (population == null) return;

                // 已有点击组件，跳过（可能是其他 MOD 或后续版本已添加）
                if (population.GetComponent<CButton>() != null) return;

                // 添加交互组件（匹配 BaseResource 的组件模式）
                population.gameObject.AddComponent<UIInteractionBehaviour>();
                var btn = population.gameObject.AddComponent<CButton>();
                btn.transition = Selectable.Transition.None;
                btn.navigation = new Navigation { mode = Navigation.Mode.None };

                // 注册点击事件：打开村民名册
                btn.onClick.AddListener(() =>
                {
                    UIManager.Instance.MaskUI(UIElement.TaiwuVillagers);
                });

                ModMain.LogDebug("已为右上角村名人数添加点击打开名册功能");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 人口点击补丁异常: {ex.Message}");
            }
        }

        #endregion
    }
}
