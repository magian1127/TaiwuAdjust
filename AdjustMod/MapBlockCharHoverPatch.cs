using System;
using System.Reflection;
using GameData.Utilities;
using Game.Views.MouseTips;
using HarmonyLib;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 地图 NPC 列表悬停信息补丁 —— 让鼠标移上去默认显示「互动信息」（等同原版按住 Shift）。
    ///
    /// 【原版状态机】（MouseTipCharacterOnMapBlock）
    ///   Update 每帧根据按键算目标状态，调 set_State 切换：
    ///     默认 Simple，按住 Alt→Detail，按住 Shift→Interaction
    ///   set_State 内部按状态：设置 alt/shift 两个 HotkeyDisplay 的提示文字 + 显隐
    ///   两键互斥（进 Detail 后 Shift 不响应，反之亦然）
    ///
    /// 【本补丁做法】—— 两个 patch 配合
    ///   ① patch Update（Prefix）：改「目标状态计算」——有互动的 NPC 默认 Interaction 而非 Simple；
    ///     按 Alt 仍切 Detail。算出目标后，把目标状态塞给原版 set_State（复用原版的状态写入和面板刷新）。
    ///   ② patch set_State（Prefix）：改「提示文字」——
    ///     Interaction 态：alt 显示「按 Alt 查看详细」，shift 关掉（互动已是默认，无需提示）
    ///     Detail 态：alt 显示「松开 Alt 回到简略」，shift 关掉
    ///     Simple 态（无互动 NPC）：alt 显示「按 Alt 查看详细」，shift 位显示「尚未认识无交互信息」
    ///     （原版 Simple 态：alt=按Alt详细，shift=按Shift互动；无互动时 shift 隐藏）
    ///
    /// 【为什么这样最稳？】
    ///   状态写入、面板显隐、提示设置全用原版 set_State 的代码路径，只是 Prefix 改它的输入参数
    ///   （目标状态）和它内部对 HotkeyDisplay 的操作。不自己反射写字段、不自己调 RefreshPageState，
    ///   避免之前「反射写字段不生效」「as 类型转换失败」等坑。
    /// </summary>
    [HarmonyPatch]
    internal static class MapBlockCharHoverPatch
    {
        #region State 枚举值常量（dnSpy 核对：Hidden=-1, Simple=0, Detail=1, Interaction=2）

        private const int Hidden = -1;
        private const int Simple = 0;
        private const int Detail = 1;
        private const int Interaction = 2;

        #endregion

        #region 反射缓存

        // MouseTipCharacterOnMapBlock._state（读当前状态）
        private static FieldInfo? _fState;
        // ._canShowInteraction（该 NPC 是否有可显示的互动选项）
        private static FieldInfo? _fCanShowInteraction;
        // .simpleDelta / .sizeDelta（定位宽度）
        private static FieldInfo? _fSimpleDelta;
        private static FieldInfo? _fSizeDelta;
        // MouseTipBase.TipFixedPosX（定位）
        private static FieldInfo? _fTipFixedPosX;
        // ★ UIBase.Element（字段不是属性）
        private static FieldInfo? _fElement;
        // .RefreshPageState（面板显隐）
        private static MethodInfo? _mRefreshPageState;
        // State 嵌套枚举 Type（从 _state.FieldType 取，最可靠）
        private static Type? _tStateEnum;

        // ↓ set_State prefix 用：alt/shift 字段（HotkeyDisplay）+ 它们的 Refresh/RefreshInner 方法
        private static FieldInfo? _fAlt;
        private static FieldInfo? _fShift;
        private static FieldInfo? _fHkType;            // HotkeyDisplay.type（EHotKeyDisplayType）
        private static MethodInfo? _mHkRefreshShort;   // HotkeyDisplay.Refresh(short)
        private static MethodInfo? _mHkRefreshInner;   // HotkeyDisplay.RefreshInner(string, List)

        /// <summary>无交互 NPC 在 shift 提示位显示的自定义文字。</summary>
        private const string NoInteractionHint = "尚未照面无互动信息";

        /// <summary>建立反射缓存。在 ModMain.Initialize() 中调用。</summary>
        internal static void Init()
        {
            try
            {
                var t = typeof(MouseTipCharacterOnMapBlock);
                _fState = AccessTools.Field(t, "_state");
                _fCanShowInteraction = AccessTools.Field(t, "_canShowInteraction");
                _fSimpleDelta = AccessTools.Field(t, "simpleDelta");
                _fSizeDelta = AccessTools.Field(t, "sizeDelta");
                _fTipFixedPosX = AccessTools.Field(typeof(MouseTipBase), "TipFixedPosX");
                _fElement = AccessTools.Field(typeof(UIBase), "Element");
                _mRefreshPageState = AccessTools.Method(t, "RefreshPageState");
                _tStateEnum = _fState?.FieldType;
                _fAlt = AccessTools.Field(t, "alt");
                _fShift = AccessTools.Field(t, "shift");
                _fHkType = AccessTools.Field(typeof(Game.Components.Common.HotkeyDisplay), "type");
                _mHkRefreshShort = AccessTools.Method(typeof(Game.Components.Common.HotkeyDisplay), "Refresh",
                    new[] { typeof(short) });
                _mHkRefreshInner = AccessTools.Method(typeof(Game.Components.Common.HotkeyDisplay), "RefreshInner",
                    new[] { typeof(string), typeof(System.Collections.Generic.List<Config.HotkeyIndex>) });

                Debug.Log($"[{ModMain.LogTag}] 地块NPC悬停反射缓存：" +
                          $"state={_fState != null}, canInteract={_fCanShowInteraction != null}, " +
                          $"simpleDelta={_fSimpleDelta != null}, sizeDelta={_fSizeDelta != null}, " +
                          $"tipFixedPosX={_fTipFixedPosX != null}, element={_fElement != null}, " +
                          $"refreshPageState={_mRefreshPageState != null}, " +
                          $"alt={_fAlt != null}, shift={_fShift != null}, " +
                          $"hkRefreshShort={_mHkRefreshShort != null}, hkRefreshInner={_mHkRefreshInner != null}, " +
                          $"stateEnum={_tStateEnum?.FullName}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 地块NPC悬停反射缓存初始化异常: {ex.Message}");
            }
        }

        #endregion

        // 【诊断标志】排查阶段置 true，定位后改回 false。
        private const bool Diag = true;

        /// <summary>
        /// ① patch Update：改目标状态计算，调原版 set_State 切状态。
        /// </summary>
        [HarmonyPatch(typeof(MouseTipCharacterOnMapBlock), "Update")]
        [HarmonyPrefix]
        internal static bool Update_Prefix(MouseTipCharacterOnMapBlock __instance)
        {
            if (!ModMain.GetSettingBool("MapCharHoverInteraction", true)) return true;
            if (_fState == null || _fCanShowInteraction == null || _fTipFixedPosX == null ||
                _fElement == null || _mRefreshPageState == null || _tStateEnum == null) return true;

            try
            {
                var element = _fElement.GetValue(__instance) as UIElement;
                if (element == null) return true;

                int curState = (int)_fState.GetValue(__instance)!;
                if (curState == Hidden) return true;

                bool canShowInteraction = (bool)_fCanShowInteraction.GetValue(__instance)!;

                // 目标状态：按住 Alt→Detail；否则有互动→Interaction（默认互动），无互动→Simple
                bool altDown = CommonCommandKit.Alt.Check(element, holdCheck: true);
                int target = altDown ? Detail : (canShowInteraction ? Interaction : Simple);

                if (Diag) Debug.Log($"[{ModMain.LogTag}] [诊断U] cur={curState} target={target} canInteract={canShowInteraction} alt={altDown}");

                // 定位宽度（复刻原版 Update）
                float posX = (target == Detail)
                    ? (float)_fSizeDelta!.GetValue(__instance)!
                    : (float)_fSimpleDelta!.GetValue(__instance)!;
                _fTipFixedPosX.SetValue(__instance, posX);

                if (curState != target)
                {
                    SetStateViaOriginal(__instance, target);
                    if (Diag) Debug.Log($"[{ModMain.LogTag}] [诊断U] 切换 {curState}→{target}");
                }
                return false;   // 接管，跳过原版 Update
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 地块NPC Update prefix 异常: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// ③ patch OnDataReady（Postfix）：每次浮窗加载新 NPC 数据后触发。
        ///
        /// 原版 OnDataReady 末尾调 set_State(Simple)：
        ///   - 无交互 NPC：set_State(Simple) 把 shift 提示隐藏（_canShowInteraction=false 时 shift.SetActive(false)），
        ///     所以无交互 NPC 默认只显示 alt 提示，没有 shift 提示。
        /// 本 postfix 在此之后：对无交互 NPC，把 shift 提示位改成显示「尚未认识无交互信息」。
        /// 这样无需依赖 Update 的状态切换判断（无交互 NPC 状态恒为 Simple，Update 不会触发提示重设），
        /// 每次 OnDataReady（即每次悬停新 NPC）都重新注入一次，干净可靠。
        /// </summary>
        [HarmonyPatch(typeof(MouseTipCharacterOnMapBlock), "OnDataReady")]
        [HarmonyPostfix]
        internal static void OnDataReady_Postfix(MouseTipCharacterOnMapBlock __instance)
        {
            if (!ModMain.GetSettingBool("MapCharHoverInteraction", true)) return;
            if (_fCanShowInteraction == null) return;
            try
            {
                // 只处理无交互 NPC（有交互的走 Update 切 Interaction，提示由 ApplyHints 管）
                if ((bool)_fCanShowInteraction.GetValue(__instance)!) return;
                // 把 shift 提示位注入自定义文字
                object? shiftObj = _fShift?.GetValue(__instance);
                if (shiftObj != null)
                {
                    SetHotkeyCustomText(shiftObj, NoInteractionHint, true);
                    if (Diag) Debug.Log($"[{ModMain.LogTag}] [诊断D] OnDataReady 注入无交互提示");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] OnDataReady postfix 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 用反射调用原版 set_State（写 _state + 原版提示逻辑），再调 RefreshPageState 刷面板。
        /// 提示文字的最终调整由 set_State 的 Postfix 负责。
        /// </summary>
        private static void SetStateViaOriginal(MouseTipCharacterOnMapBlock instance, int target)
        {
            // 写 _state
            _fState!.SetValue(instance, Enum.ToObject(_tStateEnum!, target));
            // 调原版 RefreshPageState 刷面板显隐
            _mRefreshPageState!.Invoke(instance, null);
            // 调 ApplyHints 设提示（原版 set_State 的提示逻辑会被本补丁的语义覆盖）
            ApplyHints(instance, target, (bool)_fCanShowInteraction!.GetValue(instance)!);
        }

        /// <summary>
        /// ② 设置提示文字。对 alt/shift 两个 HotkeyDisplay 用反射调用（避免类型身份问题）。
        ///   Interaction / Simple：alt=按Alt详细(Detail)
        ///     有互动→shift 隐藏；无互动→shift 显示「尚未认识无交互信息」
        ///   Detail：alt=松开Alt回简略(CancelDetail)，shift 隐藏
        /// </summary>
        private static void ApplyHints(MouseTipCharacterOnMapBlock instance, int target, bool canShowInteraction)
        {
            if (_fAlt == null || _fShift == null || _mHkRefreshShort == null || _mHkRefreshInner == null) return;

            object? altObj = _fAlt.GetValue(instance);
            object? shiftObj = _fShift.GetValue(instance);
            if (Diag) Debug.Log($"[{ModMain.LogTag}] [诊断H] target={target} alt={(altObj != null)} shift={(shiftObj != null)}");

            if (target == Detail)
            {
                SetHotkey(altObj, (short)EHotKeyDisplayType.CancelDetail, true);
                SetHotkey(shiftObj, null, false);
                return;
            }

            // Interaction / Simple
            SetHotkey(altObj, (short)EHotKeyDisplayType.Detail, true);
            if (canShowInteraction)
            {
                SetHotkey(shiftObj, null, false);   // 互动已默认，无需 shift 提示
            }
            else
            {
                SetHotkeyCustomText(shiftObj, NoInteractionHint, true);  // 显示自定义文字
            }
        }

        /// <summary>设置 HotkeyDisplay 为配置表提示文字（templateId 非 null）或隐藏。</summary>
        private static void SetHotkey(object? hkObj, short? templateId, bool active)
        {
            if (hkObj == null) return;
            try
            {
                var comp = (Component)hkObj;
                if (templateId.HasValue)
                    _mHkRefreshShort!.Invoke(hkObj, new object[] { templateId.Value });
                comp.gameObject.SetActive(active);
            }
            catch (Exception ex) { if (Diag) Debug.Log($"[{ModMain.LogTag}] [诊断H] SetHotkey异常: {ex.Message}"); }
        }

        /// <summary>
        /// 设置 HotkeyDisplay 为自定义纯文字提示（RefreshInner，commands=null）。
        ///
        /// 【★ OnEnable 覆盖坑】HotkeyDisplay.OnEnable 在 gameObject 激活时检查 type 字段：
        ///   若 type != Count，会调 Refresh(type) 按配置表重渲染，覆盖掉 RefreshInner 刚写的内容。
        /// 原版 set_State(Simple) 给 shift 设的是 type=Interaction，所以 SetActive(true) 会触发
        /// Refresh(Interaction) 把我们的自定义文字刷回「按住 Shift...」。
        /// 解法：先把 type 字段反射设为 Count，SetActive(true) 时 OnEnable 的 if 不成立就不覆盖，
        /// 然后再调 RefreshInner 写自定义文字。
        /// </summary>
        private static void SetHotkeyCustomText(object? hkObj, string text, bool active)
        {
            if (hkObj == null) return;
            try
            {
                var comp = (Component)hkObj;
                // ① 把 type 设为 Count，阻止 SetActive 触发的 OnEnable 调 Refresh 覆盖
                if (_fHkType != null)
                {
                    short count = (short)EHotKeyDisplayType.Count;
                    _fHkType.SetValue(hkObj, Enum.ToObject(_fHkType.FieldType, count));
                }
                // ② 激活物体（type=Count 时 OnEnable 不会重渲染）
                comp.gameObject.SetActive(active);
                // ③ 写自定义文字
                if (active)
                    _mHkRefreshInner!.Invoke(hkObj, new object?[] { text, null });
                if (Diag) Debug.Log($"[{ModMain.LogTag}] [诊断H] 注入「{text}」 active={active} type已设Count");
            }
            catch (Exception ex) { if (Diag) Debug.Log($"[{ModMain.LogTag}] [诊断H] SetHotkeyCustomText异常: {ex.Message}"); }
        }
    }
}
