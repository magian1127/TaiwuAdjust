using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameWork;
using FrameWork.UISystem.UIElements;
using GameData.Domains.Map;
using GameData.Serializer;
using GameData.Utilities;
using Game.Views.SkillBreak;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 突破界面疗伤按钮补丁 —— 在走格子界面动态创建疗伤按钮。
    ///
    /// 【问题背景】
    /// 实际突破（走格子）界面没有疗伤按钮，主角受伤后需要退出突破再治疗，体验割裂。
    ///
    /// 【解决方案】
    /// patch ViewCharacterMenuSkillBreakPlate 的 InitRefers 和 RefreshWithPlate，
    /// 动态创建「疗伤」按钮并挂在关闭按钮左侧。有伤可治时按钮亮色可点，无伤时灰色不可点。
    ///
    /// 【完整逻辑链】
    ///   进入突破界面（ViewCharacterMenuSkillBreakPlate 初始化）
    ///     ↓
    ///   ① InitRefers 触发 → 本 postfix 创建「疗伤」按钮
    ///     挂在 buttonClose 同父容器、复制 TMP 字体、初始状态为灰色（不可点）
    ///     ↓
    ///   ② RefreshWithPlate 触发 → 本 postfix 调用 SimulateAndRefresh
    ///     异步模拟疗伤消耗 → 回调判断 HealEffect > 0 → 切换按钮可点/灰色
    ///     ↓
    ///   ③ 玩家点击疗伤 → OnHealButtonClick
    ///     调用 HealOnMap 治一次 → 再次调用 SimulateAndRefresh 刷新状态
    ///     ↓
    ///   ④ 突破状态变化（受伤/治愈/格子移动）→ RefreshWithPlate 再次触发 → 重复 ②
    ///
    /// 【按钮生命周期】
    ///   创建：InitRefers postfix（每个 ViewCharacterMenuSkillBreakPlate 实例创建一次）
    ///   刷新：RefreshWithPlate postfix（每次面板刷新时重新模拟可点状态）
    ///   复用：_healButtons 字典缓存每个实例的按钮，避免重复创建
    ///   销毁：随 ViewCharacterMenuSkillBreakPlate 实例一起被 GC
    ///
    /// 【TMP 字体必须复制的原因】
    ///   裸 AddComponent&lt;TextMeshProUGUI&gt;() 用默认字体，没有中文字形，
    ///   写入中文后什么都不显示（常被误判为"灰色框框 bug"）。
    ///   必须从界面已有的 TMP 标签复制 font / fontSharedMaterial / spriteAsset 三个属性。
    ///   见 docs/05-harmony-pitfalls.md 坑六。
    ///
    /// 【疗伤 API 说明】
    ///   HealOnMap：调用后端实际执行疗伤，消耗药材
    ///   SimulateHealCost：异步模拟疗伤消耗，返回 HealEffect（>0 = 有伤可治）
    ///   两者都通过 MapDomainMethod（Map 域的 Mod 方法）调用。
    /// </summary>
    [HarmonyPatch]
    internal static class HealButtonPatch
    {
        #region 反射缓存

        /// <summary>
        /// ViewCharacterMenuSkillBreakPlate._taiwuCharId —— 太吾（主角）的角色 ID。
        /// 疗伤时大夫=病患=太吾，此 ID 同时用作 payerId（付款人）和被治疗角色。
        /// </summary>
        private static AccessTools.FieldRef<ViewCharacterMenuSkillBreakPlate, int>? _refTaiwuCharId;

        /// <summary>
        /// 每个突破界面实例 → 它的疗伤按钮 CButton 的映射。
        /// 用于避免重复创建，以及在 RefreshWithPlate 时定位按钮刷新状态。
        /// </summary>
        private static readonly Dictionary<ViewCharacterMenuSkillBreakPlate, CButton> _healButtons = new();

        /// <summary>
        /// 建立反射缓存。在 ModMain.Initialize() 中调用。
        /// </summary>
        internal static void Init()
        {
            try
            {
                _refTaiwuCharId = AccessTools.FieldRefAccess<ViewCharacterMenuSkillBreakPlate, int>("_taiwuCharId");
                Debug.Log($"[{ModMain.LogTag}] 疗伤按钮反射缓存：taiwuCharId={_refTaiwuCharId != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 疗伤按钮反射缓存初始化异常: {ex.Message}");
            }
        }

        #endregion

        #region Harmony Patches

        /// <summary>
        /// ViewCharacterMenuSkillBreakPlate.InitRefers 的 postfix。
        ///
        /// 【触发时机】突破界面初始化时，InitRefers 负责加载界面控件引用。
        /// 本 postfix 在界面初始化完成后创建「疗伤」按钮。
        ///
        /// 【为什么 patch InitRefers？】
        ///   InitRefers 是界面生命周期中最早保证所有控件可用的时机，
        ///   此时 buttonClose 等控件已就绪，可以安全地挂载自定义按钮。
        /// </summary>
        [HarmonyPatch(typeof(ViewCharacterMenuSkillBreakPlate), "InitRefers")]
        [HarmonyPostfix]
        internal static void SkillBreakPlate_InitRefers_Postfix(ViewCharacterMenuSkillBreakPlate __instance)
        {
            ModMain.LogDebug("SkillBreakPlate_InitRefers_Postfix 触发");
            if (!ModMain.GetSettingBool("AutoHealButton", true)) return;
            if (__instance == null || _refTaiwuCharId == null) return;
            if (_healButtons.ContainsKey(__instance)) return;   // 已创建过，跳过

            try
            {
                CreateHealButton(__instance);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 疗伤按钮创建异常: {ex.Message}");
            }
        }

        /// <summary>
        /// ViewCharacterMenuSkillBreakPlate.RefreshWithPlate 的 postfix。
        ///
        /// 【触发时机】突破面板每次刷新（格子移动、状态变化等）都会调用 RefreshWithPlate。
        /// 本 postfix 重新模拟疗伤消耗，判断按钮应为可点/灰色状态。
        ///
        /// 【为什么需要每次刷新？】
        ///   突破过程中主角伤势会变化（受伤/治愈），按钮的可点状态需要实时更新。
        ///   SimulateHealCost 是异步的，回调到达后直接更新按钮视觉。
        /// </summary>
        [HarmonyPatch(typeof(ViewCharacterMenuSkillBreakPlate), "RefreshWithPlate",
            new[] { typeof(GameData.Domains.Taiwu.SkillBreakPlate), typeof(Action) })]
        [HarmonyPostfix]
        internal static void SkillBreakPlate_RefreshWithPlate_Postfix(ViewCharacterMenuSkillBreakPlate __instance)
        {
            ModMain.LogDebug("SkillBreakPlate_RefreshWithPlate_Postfix 触发");
            if (!ModMain.GetSettingBool("AutoHealButton", true)) return;
            if (__instance == null) return;
            if (!_healButtons.TryGetValue(__instance, out var btn) || btn == null) return;
            SimulateAndRefresh(__instance, btn);
        }

        #endregion

        #region 按钮创建

        /// <summary>
        /// 反射读取 ViewCharacterMenuSkillBreakPlate 的私有字段（引用类型版本）。
        /// 用于获取 buttonClose、maxPowerLabel 等控件引用。
        /// </summary>
        private static T? GetField<T>(ViewCharacterMenuSkillBreakPlate view, string fieldName) where T : class
        {
            return typeof(ViewCharacterMenuSkillBreakPlate)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(view) as T;
        }

        /// <summary>
        /// 判断一个 TMP 是否可用作字体模板（有 font 且有 material）。
        /// </summary>
        private static bool IsValidFontSource(TextMeshProUGUI txt)
        {
            return txt != null && txt.font != null && txt.fontSharedMaterial != null;
        }

        /// <summary>
        /// 从突破界面取一个可用的 TMP 当字体模板。
        /// 优先用 maxPowerLabel（界面已有的功率标签），否则在容器/界面里找第一个有效的 TMP。
        /// </summary>
        private static TextMeshProUGUI? FindFontTemplate(ViewCharacterMenuSkillBreakPlate view, Transform container)
        {
            var primary = GetField<TextMeshProUGUI>(view, "maxPowerLabel");
            if (primary != null && IsValidFontSource(primary)) return primary;
            if (container != null)
            {
                var found = container.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(IsValidFontSource);
                if (found != null) return found;
            }
            return (view as Component).GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(IsValidFontSource);
        }

        /// <summary>
        /// 创建「疗伤」按钮。
        ///
        /// 【创建流程】
        ///   1. 找到 buttonClose → 取其父容器作为按钮挂载点（确保坐标对齐）
        ///   2. 找到可用的 TMP 字体模板 → 复制 font/material/spriteAsset（否则中文不显示）
        ///   3. 创建 GameObject + RectTransform + Image + CButton → 挂到父容器
        ///   4. 定位：放在 buttonClose 左侧，中间留 220px 空白
        ///   5. 创建子 TMP 文本「疗伤」→ 复制字体模板
        ///   6. 绑定点击事件 → 初始状态为灰色（不可点），等 SimulateAndRefresh 刷新真实状态
        ///
        /// 【按钮样式】
        ///   背景：深色半透明矩形（Color(0.16, 0.12, 0.08, 0.86)）
        ///   文字：白色 22px TMP
        ///   可点状态：墨绿亮底 + 暖金色文字（由 SetHealButtonVisual 切换）
        ///   不可点状态：深灰暗底 + 中灰文字
        /// </summary>
        private static void CreateHealButton(ViewCharacterMenuSkillBreakPlate view)
        {
            // 父容器：buttonClose 同父（确保按钮和关闭按钮在同一层级，坐标对齐）
            var closeBtn = GetField<CButton>(view, "buttonClose");
            if (closeBtn == null) { Debug.Log($"[{ModMain.LogTag}] 疗伤按钮：找不到 buttonClose"); return; }
            var closeRt = closeBtn.GetComponent<RectTransform>();
            var containerRt = closeRt.parent;

            // 字体模板：从界面已有的 TMP 标签复制 font/material/spriteAsset
            // ★ 这是关键步骤——裸 AddComponent<TextMeshProUGUI> 用默认字体，没有中文字形
            var fontSrc = FindFontTemplate(view, containerRt);
            if (fontSrc == null) { Debug.Log($"[{ModMain.LogTag}] 疗伤按钮：找不到可用字体模板 TMP"); return; }

            // 纯色矩形按钮：GameObject + RectTransform + Image(纯色) + CButton
            var go = new GameObject("AdjustMod_HealButton", typeof(RectTransform), typeof(Image), typeof(CButton));
            go.transform.SetParent(containerRt, false);
            var rt = go.GetComponent<RectTransform>();
            // 锚点对齐关闭按钮（anchorMin/Max/Pivot 一致）
            rt.anchorMin = closeRt.anchorMin;
            rt.anchorMax = closeRt.anchorMax;
            rt.pivot = closeRt.pivot;
            // 定位：放在 buttonClose 左侧，中间留大量空白（向左偏移 220）
            float myW = 120f;
            rt.sizeDelta = new Vector2(myW, closeRt.sizeDelta.y);
            float offsetX = -(closeRt.sizeDelta.x * 0.5f + myW * 0.5f + 220f);
            rt.anchoredPosition = closeRt.anchoredPosition + new Vector2(offsetX, 0f);
            // 置顶渲染，避免被其他 UI 遮挡
            go.transform.SetAsLastSibling();

            var img = go.GetComponent<Image>();
            img.color = new Color(0.16f, 0.12f, 0.08f, 0.86f);   // 深色半透明背景
            img.raycastTarget = true;

            // CButton 配色（参考 MOD 的深色主题）
            var btn = go.GetComponent<CButton>();
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = new Color(0.16f, 0.12f, 0.08f, 0.86f);
            cb.highlightedColor = new Color(0.3f, 0.23f, 0.15f, 0.96f);
            cb.pressedColor = new Color(0.1f, 0.075f, 0.045f, 1f);
            cb.selectedColor = cb.highlightedColor;
            cb.disabledColor = new Color(0.1f, 0.1f, 0.1f, 0.45f);
            cb.fadeDuration = 0.08f;
            cb.colorMultiplier = 1f;
            btn.transition = Selectable.Transition.ColorTint;
            btn.colors = cb;
            btn.targetGraphic = img;
            btn.navigation = new Navigation { mode = Navigation.Mode.None };

            // 子文本「疗伤」——必须复制字体模板的 font/material/spriteAsset，否则中文渲染不出来
            var txtGo = new GameObject("Text", typeof(RectTransform));
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero; txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = Vector2.zero; txtRt.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.text = "疗伤";
            txt.alignment = TextAlignmentOptions.Center;
            txt.fontSize = 22;
            txt.color = Color.white;
            txt.raycastTarget = false;
            // ★ 关键：复制字体属性，否则中文不显示
            txt.font = fontSrc.font;
            txt.fontSharedMaterial = fontSrc.fontSharedMaterial;
            txt.spriteAsset = fontSrc.spriteAsset;

            btn.ClearAndAddListener((Action)(() => OnHealButtonClick(view, btn)));
            _healButtons[view] = btn;
            // 初始按「不可点」显示，等 SimulateAndRefresh 回来再按真实状态切
            SetHealButtonVisual(btn, false);
            ModMain.LogDebug($"疗伤按钮已创建（容器={containerRt.name}, 字体源={fontSrc.name}）");
        }

        #endregion

        #region 按钮状态管理

        /// <summary>
        /// 根据可点状态切换疗伤按钮视觉。
        ///
        /// 【可点状态】墨绿亮底（Color(0.24, 0.40, 0.26, 0.92)）+ 暖金色文字（Color(1, 0.92, 0.55)）
        /// 【不可点状态】深灰暗底（Color(0.12, 0.12, 0.12, 0.55)）+ 中灰文字（Color(0.45, 0.45, 0.45)）
        ///
        /// 不走 interactable 的淡入淡出（ColorTint 已配置），直接着色让状态切换更干脆。
        /// </summary>
        private static void SetHealButtonVisual(CButton btn, bool canHeal)
        {
            if (btn == null) return;
            var img = btn.targetGraphic as Image;
            if (img != null)
            {
                img.color = canHeal
                    ? new Color(0.24f, 0.40f, 0.26f, 0.92f)   // 可点：墨绿亮底
                    : new Color(0.12f, 0.12f, 0.12f, 0.55f);  // 不可点：深灰暗底
            }
            var txt = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (txt != null)
            {
                txt.color = canHeal
                    ? new Color(1f, 0.92f, 0.55f)   // 可点：暖金色
                    : new Color(0.45f, 0.45f, 0.45f); // 不可点：中灰
            }
        }

        #endregion

        #region 点击与异步模拟

        /// <summary>
        /// 点击疗伤按钮的处理。
        ///
        /// 【执行流程】
        ///   1. 调用 HealOnMap 执行实际疗伤（消耗药材，治一次伤势）
        ///   2. 调用 SimulateAndRefresh 重新模拟，刷新按钮可点/灰色状态
        ///
        /// 【HealOnMap 参数说明】
        ///   view.Element.GameDataListenerId：当前界面的 GameData 监听器 ID
        ///   0：伤势类型（typeInt=0，普通伤势）
        ///   taiwuId：大夫=病患=太吾主角
        ///   true：needPay=true（需要消耗药材）
        ///   taiwuId：payerId=太吾（付款人）
        ///   false：isExpensiveHeal=false（普通疗伤，不消耗额外金钱/恩义）
        /// </summary>
        private static void OnHealButtonClick(ViewCharacterMenuSkillBreakPlate view, CButton btn)
        {
            try
            {
                int taiwuId = _refTaiwuCharId!(view);
                MapDomainMethod.Call.HealOnMap(view.Element.GameDataListenerId, 0, taiwuId, taiwuId, true, taiwuId, false);
                ModMain.LogDebug($"疗伤点击：HealOnMap(taiwu={taiwuId})");
                // 治完后重新模拟刷新可点状态（有伤=可点，无伤=灰）
                SimulateAndRefresh(view, btn);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 疗伤点击异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步模拟疗伤消耗，判断按钮应为可点/灰色状态。
        ///
        /// 【执行流程】
        ///   1. 调用 SimulateHealCost（异步）→ 模拟疗伤消耗，返回 MapHealSimulateResult
        ///   2. 回调在主线程执行：检查 HealEffect > 0（有伤可治）
        ///   3. 更新 btn.interactable + SetHealButtonVisual 切换视觉状态
        ///
        /// 【为什么用异步模拟？】
        ///   HealOnMap 是同步的（实际执行疗伤），但判断"有伤可治"需要异步模拟，
        ///   因为需要查询后端的药材库存和伤势状态。SimulateHealCost 不执行实际疗伤，
        ///   只模拟结果，用于决定按钮是否可点。
        /// </summary>
        private static void SimulateAndRefresh(ViewCharacterMenuSkillBreakPlate view, CButton btn)
        {
            try
            {
                int taiwuId = _refTaiwuCharId!(view);
                // 参数与 HealOnMap 一致：typeInt=0（普通伤势），needPay=true，isExpensiveHeal=false
                MapDomainMethod.AsyncCall.SimulateHealCost(view, 0, taiwuId, taiwuId, true, false,
                    (int offset, RawDataPool pool) =>
                    {
                        try
                        {
                            MapHealSimulateResult result = default;
                            Serializer.Deserialize(pool, offset, ref result);
                            // HealEffect > 0 = 有伤可治（资源是否够由 HealOnMap 后端处理，这里只判断有伤）
                            bool canHeal = result.HealEffect > 0;
                            btn.interactable = canHeal;
                            SetHealButtonVisual(btn, canHeal);
                            ModMain.LogDebug($"疗伤模拟：HealEffect={result.HealEffect} canHeal={canHeal}");
                        }
                        catch (Exception ex) { Debug.Log($"[{ModMain.LogTag}] 疗伤模拟回调异常: {ex.Message}"); }
                    });
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] SimulateAndRefresh 异常: {ex.Message}");
            }
        }

        #endregion
    }
}
