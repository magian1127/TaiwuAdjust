using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameWork;
using FrameWork.UISystem.UIElements;
using Game.Views.Combat;
using GameData.Domains.Character;
using GameData.Domains.Character.Display;
using GameData.Domains.Combat;
using GameData.Serializer;
using GameData.Utilities;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 战斗准备界面「内力震慑」按钮补丁。
    ///
    /// 【功能说明】
    /// 在战斗准备界面（ViewCombatBegin）「查看人物」按钮左侧，动态创建「内力震慑」按钮。
    /// 当主角精纯高于敌方，且自身丹田内力足够支付（敌方真气/10，向上取整最少 1）时，按钮可点：
    ///   - 扣除主角丹田内力（CurrNeili）= 敌方真气 / 10（向上取整，最少 1）
    ///   - 直接进入战斗，并强制敌人战败（走原版结算流程，正常获得战利品/经验）
    ///
    /// 【术语对应】（角色面板）
    ///   丹田内力 = CurrNeili（面板"丹田内力"数字，如主角 1325）
    ///   真气 = NeiliAllocation.GetTotal()（面板"真气上限"数字，4 项分配摧破/轻灵/护体/奇窍之和）
    ///   精纯 = ConsummateLevel（sbyte，0~9 点）
    ///
    /// 【触发条件】
    ///   ① selfConsummate &gt; enemyConsummate（主角精纯高于对方）
    ///   ② selfCurrNeili &gt;= cost（cost = 敌方真气/10 向上取整，最少 1）
    ///   不满足时按钮灰色禁用（参考 HealButtonPatch 的视觉风格）
    ///
    /// 【数据来源】
    ///   双方精纯/丹田内力/真气均通过 CharacterDomainMethod.AsyncCall.GetCharacterDisplayDataForNeiliPage
    ///   异步查询真实人物数据（CharacterDisplayDataForNeiliPage），不用 UI monitor 派生缓存，
    ///   确保数值与存档一致。
    ///
    /// 【完整逻辑链】
    ///   进入战斗准备界面（ViewCombatBegin.Awake）
    ///     ↓
    ///   ① Awake postfix → 创建「内力震慑」按钮，初始灰色
    ///     ↓
    ///   ② UpdateNeiliTypeAndAllocation postfix → 发起异步查询双方真实数据 → 回调刷新按钮状态
    ///     ↓
    ///   ③ 玩家点击「内力震慑」→ OnShockButtonClick
    ///     扣真气（GmCmd_SetCurrNeili，底层只写 CurrNeili 不碰 MaxNeili）
    ///     → 置 _shockPending=true → 反射调用 ShowCombatUi 进入战斗
    ///     ↓
    ///   ④ ViewCombat.OnCombatBeginReady postfix → 战斗就绪，检查 _shockPending
    ///     调用 GmCmd_ForceEnemyDefeat 强制敌人战败 → 战斗正常进入 CombatOver 结算
    /// </summary>
    [HarmonyPatch]
    internal static class NeiliShockPatch
    {
        #region 反射缓存

        /// <summary>
        /// ViewCombatBegin.ShowCombatUi —— 私有方法，原版"开始战斗"流程入口。
        /// 点击震慑按钮后反射调用，复用原版转场到 StateCombat 的完整逻辑。
        /// </summary>
        private static MethodInfo? _miShowCombatUi;

        /// <summary>
        /// 每个 ViewCombatBegin 实例 → 它的「内力震慑」按钮 CButton。
        /// 用于避免重复创建，以及刷新状态时定位按钮。
        /// </summary>
        private static readonly Dictionary<ViewCombatBegin, CButton> _buttons = new();

        /// <summary>
        /// 缓存的最新的双方真实人物数据（从后端 GetCharacterDisplayDataForNeiliPage 查询）。
        /// 异步查询回调写入，状态刷新与点击逻辑读取。包含真实的 CurrNeili/MaxNeili/ConsummateLevel。
        /// </summary>
        private static CharacterDisplayDataForNeiliPage? _selfData;
        private static CharacterDisplayDataForNeiliPage? _enemyData;

        /// <summary>
        /// 正在进行的查询去重标志，避免重复发起异步查询。
        /// </summary>
        private static bool _querying = false;

        #endregion

        #region 跨界面状态

        /// <summary>
        /// 「内力震慑」待强制胜利标志。
        /// 点击震慑按钮时置 true，进入战斗后 ViewCombat.OnCombatBeginReady postfix 检测到则调用
        /// GmCmd_ForceEnemyDefeat 并清回 false。仅标记最近一次通过震慑按钮发起的战斗。
        /// </summary>
        internal static bool _shockPending = false;

        #endregion

        #region 初始化

        /// <summary>
        /// 建立反射缓存。在 ModMain.Initialize() 中调用。
        /// </summary>
        internal static void Init()
        {
            try
            {
                _miShowCombatUi = AccessTools.Method(typeof(ViewCombatBegin), "ShowCombatUi");
                Debug.Log($"[{ModMain.LogTag}] 内力震慑反射缓存：showCombatUi={_miShowCombatUi != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 内力震慑反射缓存初始化异常: {ex.Message}");
            }
        }

        #endregion

        #region Harmony Patches

        /// <summary>
        /// ViewCombatBegin.Awake 的 postfix。
        ///
        /// 【触发时机】战斗准备界面初始化时（每个实例一次，由 _awakeDone 守卫）。
        /// 此时 StartBtnRoot / StartCombatBtn 等控件已就绪，可安全挂载自定义按钮。
        /// </summary>
        [HarmonyPatch(typeof(ViewCombatBegin), "Awake")]
        [HarmonyPostfix]
        internal static void CombatBegin_Awake_Postfix(ViewCombatBegin __instance)
        {
            ModMain.LogDebug("CombatBegin_Awake_Postfix 触发");
            if (!ModMain.GetSettingBool("NeiliShock", true)) return;
            if (__instance == null) return;
            if (_buttons.ContainsKey(__instance)) return;   // 已创建过，跳过

            // 新战斗：清空上一场残留的人物数据缓存
            _selfData = null;
            _enemyData = null;
            _querying = false;

            try
            {
                CreateShockButton(__instance);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 内力震慑按钮创建异常: {ex.Message}");
            }
        }

        /// <summary>
        /// ViewCombatBegin.UpdateNeiliTypeAndAllocation 的 postfix。
        ///
        /// 【触发时机】双方内力数据刷新时（数据就绪、内力变化、精纯变化都会触发）。
        /// 此时双方队伍信息已就绪，发起异步查询获取双方真实人物数据，
        /// 回调里刷新震慑按钮可点状态。
        ///
        /// 【为什么用异步查询而不是直接读 monitor？】
        ///   EquipCombatSkillMonitor 的 CurrNeili 是 UI 监听的派生缓存，实测与真实存档值可能不同步；
        ///   GetCharacterDisplayDataForNeiliPage 直接从 Character 数据域查真实值，最可靠。
        /// </summary>
        [HarmonyPatch(typeof(ViewCombatBegin), "UpdateNeiliTypeAndAllocation")]
        [HarmonyPostfix]
        internal static void CombatBegin_UpdateNeili_Postfix(ViewCombatBegin __instance)
        {
            if (!ModMain.GetSettingBool("NeiliShock", true)) return;
            if (__instance == null) return;
            if (!_buttons.ContainsKey(__instance)) return;
            RequestCharacterData(__instance);
        }

        /// <summary>
        /// ViewCombat.OnCombatBeginReady 的 postfix。
        ///
        /// 【触发时机】战斗数据就绪、即将正式开战时（CombatDomainMethod.Call.StartCombat 调用之后）。
        /// 若本次战斗是「内力震慑」发起的（_shockPending），立即调用 GmCmd_ForceEnemyDefeat
        /// 强制敌人战败，战斗随后正常进入 CombatOver 结算流程。
        /// </summary>
        [HarmonyPatch(typeof(Game.Views.Combat.ViewCombat), "OnCombatBeginReady")]
        [HarmonyPostfix]
        internal static void ViewCombat_OnCombatBeginReady_Postfix()
        {
            if (!_shockPending) return;
            _shockPending = false;
            try
            {
                CombatDomainMethod.Call.GmCmd_ForceEnemyDefeat();
                Debug.Log($"[{ModMain.LogTag}] 内力震慑：已强制敌人战败");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 内力震慑强制胜利异常: {ex.Message}");
            }
        }

        #endregion

        #region 按钮创建

        /// <summary>
        /// 反射读取 ViewCombatBegin 的私有字段（引用类型版本）。
        /// 用于获取 StartCombatBtn、selfCharInfo 等控件引用。
        /// </summary>
        private static T? GetField<T>(ViewCombatBegin view, string fieldName) where T : class
        {
            return typeof(ViewCombatBegin)
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
        /// 从战斗准备界面取一个可用的 TMP 当字体模板。
        /// 优先在 StartBtnRoot 容器里找，否则在界面下找第一个有效 TMP。
        /// </summary>
        private static TextMeshProUGUI? FindFontTemplate(Transform? container, ViewCombatBegin view)
        {
            if (container != null)
            {
                var found = container.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(IsValidFontSource);
                if (found != null) return found;
            }
            return (view as Component).GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(IsValidFontSource);
        }

        /// <summary>
        /// 创建「内力震慑」按钮。
        ///
        /// 【创建流程】
        ///   1. 找到 SelfInfo 下的「查看人物」按钮（BtnOpenCharMenu）→ 取其父容器作为挂载点
        ///   2. 找可用 TMP 字体模板 → 复制 font/material/spriteAsset（否则中文不显示）
        ///   3. 创建 GameObject + RectTransform + Image + CButton → 挂到 SelfInfo 容器
        ///   4. 定位：用世界坐标（GetWorldCorners）计算「查看人物」按钮的中心，
        ///      再向左偏移，转回父容器局部坐标。世界坐标定位对锚点差异免疫。
        ///   5. 创建子 TMP 文本「内力震慑」→ 复制字体模板
        ///   6. 绑定点击事件 → 初始灰色，等 RefreshButtonState 切真实状态
        /// </summary>
        private static void CreateShockButton(ViewCombatBegin view)
        {
            // 找 SelfInfo 下的同道列表容器（TeammateHolder），按钮作为它的子级
            // —— 坐标系简单可控，且自然排在队友列表下方
            var holderTr = (view as Component).transform.Find("Content/SelfInfo/TeammateHolder");
            if (holderTr == null)
            {
                Debug.Log($"[{ModMain.LogTag}] 内力震慑：找不到 SelfInfo/TeammateHolder");
                return;
            }
            var holderRt = holderTr as RectTransform;
            if (holderRt == null) { Debug.Log($"[{ModMain.LogTag}] 内力震慑：TeammateHolder 不是 RectTransform"); return; }

            var fontSrc = FindFontTemplate(holderRt, view);
            if (fontSrc == null) { Debug.Log($"[{ModMain.LogTag}] 内力震慑：找不到可用字体模板 TMP"); return; }

            var go = new GameObject("AdjustMod_NeiliShockButton", typeof(RectTransform), typeof(Image), typeof(CButton));
            go.transform.SetParent(holderRt, false);
            var rt = go.GetComponent<RectTransform>();
            // 顶部居中锚点 + 中心 pivot
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            // 紧凑固定尺寸（参考疗伤按钮）：120 宽 × 50 高
            float myW = 120f, myH = 50f;
            rt.sizeDelta = new Vector2(myW, myH);
            // 放在 TeammateHolder 顶部（队友列表上方），不挤占队友格子
            rt.anchoredPosition = new Vector2(0f, myH * 0.5f + 10f);
            go.transform.SetAsLastSibling();

            var img = go.GetComponent<Image>();
            img.color = new Color(0.16f, 0.12f, 0.08f, 0.86f);
            img.raycastTarget = true;

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

            // 子文本「内力震慑」——必须复制字体模板的 font/material/spriteAsset，否则中文不显示
            var txtGo = new GameObject("Text", typeof(RectTransform));
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero; txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = Vector2.zero; txtRt.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.text = "内力震慑";
            txt.alignment = TextAlignmentOptions.Center;
            txt.fontSize = 22;
            txt.color = Color.white;
            txt.raycastTarget = false;
            txt.font = fontSrc.font;
            txt.fontSharedMaterial = fontSrc.fontSharedMaterial;
            txt.spriteAsset = fontSrc.spriteAsset;

            btn.ClearAndAddListener((Action)(() => OnShockButtonClick(view)));
            _buttons[view] = btn;
            // 初始灰色，等 UpdateNeiliTypeAndAllocation postfix 切真实状态
            SetButtonVisual(btn, false);
            ModMain.LogDebug($"内力震慑按钮已创建（容器={holderRt.name}, 字体源={fontSrc.name}）");
        }

        #endregion

        #region 按钮状态管理

        /// <summary>
        /// 根据可点状态切换按钮视觉。
        ///
        /// 【可点状态】暖紫亮底（Color(0.40, 0.20, 0.50, 0.92)）+ 暖金色文字（Color(1, 0.92, 0.55)）
        /// 【不可点状态】深灰暗底（Color(0.12, 0.12, 0.12, 0.55)）+ 中灰文字（Color(0.45, 0.45, 0.45)）
        /// </summary>
        private static void SetButtonVisual(CButton btn, bool canShock)
        {
            if (btn == null) return;
            var img = btn.targetGraphic as Image;
            if (img != null)
            {
                img.color = canShock
                    ? new Color(0.40f, 0.20f, 0.50f, 0.92f)   // 可点：暖紫亮底
                    : new Color(0.12f, 0.12f, 0.12f, 0.55f);   // 不可点：深灰暗底
            }
            var txt = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (txt != null)
            {
                txt.color = canShock
                    ? new Color(1f, 0.92f, 0.55f)    // 可点：暖金色
                    : new Color(0.45f, 0.45f, 0.45f); // 不可点：中灰
            }
        }

        /// <summary>
        /// 异步查询双方主战角色的真实人物数据（精纯/当前真气/真气上限）。
        /// 用 GetCharacterDisplayDataForNeiliPage 从 Character 数据域查真实值，
        /// 回调里写入 _selfData/_enemyData 并刷新按钮状态。
        ///
        /// 【为什么不用 EquipCombatSkillMonitor？】
        ///   monitor 是 UI 监听的派生缓存，与真实存档值可能不同步，导致扣费计算错误。
        /// </summary>
        private static void RequestCharacterData(ViewCombatBegin view)
        {
            if (_querying) return;   // 去重，避免重复发起
            // SelfTeam/EnemyTeam 是 private 属性，反射读取
            var selfTeam = (IReadOnlyList<int>?)typeof(ViewCombatBegin)
                .GetProperty("SelfTeam", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(view);
            var enemyTeam = (IReadOnlyList<int>?)typeof(ViewCombatBegin)
                .GetProperty("EnemyTeam", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(view);
            if (selfTeam == null || enemyTeam == null || selfTeam.Count == 0 || enemyTeam.Count == 0) return;

            int selfCharId = selfTeam[0];
            int enemyCharId = enemyTeam[0];

            _querying = true;
            // 查己方
            CharacterDomainMethod.AsyncCall.GetCharacterDisplayDataForNeiliPage(view, selfCharId,
                (int offset, RawDataPool pool) =>
                {
                    try
                    {
                        var data = new CharacterDisplayDataForNeiliPage();
                        Serializer.Deserialize(pool, offset, ref data);
                        _selfData = data;
                        TryRefreshAfterQuery(view);
                    }
                    catch (Exception ex) { Debug.Log($"[{ModMain.LogTag}] 内力震慑查询己方异常: {ex.Message}"); _querying = false; }
                });
            // 查敌方
            CharacterDomainMethod.AsyncCall.GetCharacterDisplayDataForNeiliPage(view, enemyCharId,
                (int offset, RawDataPool pool) =>
                {
                    try
                    {
                        var data = new CharacterDisplayDataForNeiliPage();
                        Serializer.Deserialize(pool, offset, ref data);
                        _enemyData = data;
                        TryRefreshAfterQuery(view);
                    }
                    catch (Exception ex) { Debug.Log($"[{ModMain.LogTag}] 内力震慑查询敌方异常: {ex.Message}"); _querying = false; }
                });
        }

        /// <summary>
        /// 双方数据都查询到位后刷新按钮状态（两个回调各自调用，任一到达都尝试一次）。
        /// </summary>
        private static void TryRefreshAfterQuery(ViewCombatBegin view)
        {
            if (_selfData == null || _enemyData == null) return;
            _querying = false;
            if (_buttons.TryGetValue(view, out var btn) && btn != null)
                RefreshButtonState(btn);
        }

        /// <summary>
        /// 计算震慑消耗：敌方真气（NeiliAllocation 分配点数总和）/ 10，向上取整，最少 1。
        /// </summary>
        private static int CalcCost(int enemyZhenqiTotal)
        {
            if (enemyZhenqiTotal <= 0) return 1;
            return (enemyZhenqiTotal + 9) / 10;   // 向上取整
        }

        /// <summary>
        /// 取敌方真气（NeiliAllocation.GetTotal()）。NeiliAllocation 为 null 时返回 0。
        /// </summary>
        private static int GetEnemyZhenqi(CharacterDisplayDataForNeiliPage? enemyData)
        {
            return enemyData?.NeiliAllocation.GetTotal() ?? 0;
        }

        /// <summary>
        /// 重算震慑按钮可点状态（基于 _selfData/_enemyData 缓存）。
        ///
        /// 【可点条件】
        ///   ① selfConsummate &gt; enemyConsummate（主角精纯高于对方）
        ///   ② selfCurrNeili &gt;= cost（自身当前真气够支付，cost = 敌方真气/10 向上取整最少 1）
        /// </summary>
        private static void RefreshButtonState(CButton btn)
        {
            var selfData = _selfData;
            var enemyData = _enemyData;
            if (selfData == null || enemyData == null) { SetButtonVisual(btn, false); return; }

            sbyte selfConsummate = selfData.ConsummateLevel;
            sbyte enemyConsummate = enemyData.ConsummateLevel;
            int selfCurrNeili = selfData.CurrNeili;
            int enemyZhenqi = GetEnemyZhenqi(enemyData);
            int cost = CalcCost(enemyZhenqi);

            bool canShock = selfConsummate > enemyConsummate && selfCurrNeili >= cost;
            btn.interactable = canShock;
            SetButtonVisual(btn, canShock);
            ModMain.LogDebug($"内力震慑状态：精纯 {selfConsummate} vs {enemyConsummate}，我方丹田内力 {selfCurrNeili} vs 需付 {cost}（敌方真气 {enemyZhenqi}/10）→ canShock={canShock}");
        }

        #endregion

        #region 点击处理

        /// <summary>
        /// 点击「内力震慑」按钮的处理。
        ///
        /// 【执行流程】
        ///   1. 读取缓存的双方真实数据，再次校验条件（防止数据过期）
        ///   2. 扣除真气：GmCmd_SetCurrNeili(taiwuId, selfCurrNeili - cost)
        ///      底层 SetCurrNeili 只写 CurrNeili 字段，不碰 MaxNeili
        ///   3. 置 _shockPending=true（标记本次战斗要强制胜利）
        ///   4. 反射调用 ShowCombatUi —— 复用原版"开始战斗"流程转场到 StateCombat
        ///      战斗就绪后由 ViewCombat.OnCombatBeginReady postfix 强制敌人战败
        /// </summary>
        private static void OnShockButtonClick(ViewCombatBegin view)
        {
            try
            {
                if (_miShowCombatUi == null)
                {
                    Debug.Log($"[{ModMain.LogTag}] 内力震慑：反射缓存不完整，取消");
                    return;
                }

                var selfData = _selfData;
                var enemyData = _enemyData;
                if (selfData == null || enemyData == null)
                {
                    Debug.Log($"[{ModMain.LogTag}] 内力震慑：人物数据未就绪，取消");
                    return;
                }

                sbyte selfConsummate = selfData.ConsummateLevel;
                sbyte enemyConsummate = enemyData.ConsummateLevel;
                int selfCurrNeili = selfData.CurrNeili;
                int enemyZhenqi = GetEnemyZhenqi(enemyData);
                int cost = CalcCost(enemyZhenqi);

                // 再次校验（防数据过期）
                if (!(selfConsummate > enemyConsummate) || selfCurrNeili < cost)
                {
                    ModMain.LogDebug($"内力震慑：条件不满足，取消（精纯 {selfConsummate} vs {enemyConsummate}，丹田内力 {selfCurrNeili} < 需付 {cost}）");
                    return;
                }

                int taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId;
                int afterCost = selfCurrNeili - cost;

                // ① 扣丹田内力（GmCmd_SetCurrNeili 底层只写 CurrNeili 字段，不碰 MaxNeili/NeiliAllocation）
                GameData.Domains.Character.CharacterDomainMethod.Call.GmCmd_SetCurrNeili(taiwuId, afterCost);
                ModMain.LogDebug($"内力震慑：扣丹田内力 {cost}（{selfCurrNeili} → {afterCost}），敌方真气 {enemyZhenqi}，taiwuId={taiwuId}");

                // ② 标记本次战斗要强制胜利
                _shockPending = true;

                // ③ 反射调用 ShowCombatUi，复用原版开始战斗流程
                _miShowCombatUi.Invoke(view, null);
                Debug.Log($"[{ModMain.LogTag}] 内力震慑：已发起战斗，等待强制胜利");
            }
            catch (Exception ex)
            {
                _shockPending = false;
                Debug.Log($"[{ModMain.LogTag}] 内力震慑点击异常: {ex.Message}");
            }
        }

        #endregion
    }
}
