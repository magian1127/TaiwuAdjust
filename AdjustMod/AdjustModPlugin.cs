using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameWork;
using FrameWork.UISystem.UIElements;
using Game.Components.Common;
using GameData.Domains.CombatSkill;
using GameData.Domains.Item;
using GameData.Domains.Map;
using Game.Views.SkillBreak;
using GameData.Domains.Item.Display;
using GameData.Domains.Mod;
using GameData.Serializer;
using GameData.Utilities;
using HarmonyLib;
using TaiwuModdingLib.Core.Plugin;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 前端插件 - 在查看 NPC 背包的书籍时，显示该 NPC 的阅读完成状态。
    ///
    /// 角色「持有→同道队友」界面显示书籍的浮窗是 TooltipBook（继承 TooltipItemBase，
    /// 走角色菜单的独立 tooltip 系统，不经 TooltipManager）。
    ///
    /// 原版 TooltipBook.OnGetPageInfo 通过 GetSkillBookPagesInfo 取的进度是【太吾自己】的，
    /// 所以 NPC 背包的书也只显示太吾进度。本插件用后端 GetCharBookReadState(OwnerCharId, bookKey)
    /// 查该物品所属角色的真实阅读状态，并在每页状态文字后追加"此人已读/此人未读"。
    ///
    /// 时序：TooltipBook.Refresh 触发查询并缓存；TooltipBookPage.Refresh（每页渲染的叶节点方法）
    /// 在原版设置完 textIncompleteState 之后执行，追加 NPC 状态 —— 这样每次重新悬停、
    /// 页面重渲染后都能重新追加，不会被 OnGetPageInfo 覆盖。
    /// </summary>
    [PluginConfig("AdjustMod", "adjust", "1.0.0.0")]
    public class ModMain : TaiwuRemakePlugin
    {
        /// <summary>NPC 阅读状态缓存：[npcCharId][bookId] -> readState[]。以角色+书为键，与浮窗实例无关</summary>
        private static readonly Dictionary<int, Dictionary<int, bool[]>> _readStateCache = new();

        /// <summary>正在进行的查询去重：[(npcCharId, bookId)]，避免同一目标重复发起后端调用</summary>
        private static readonly HashSet<long> _pendingQueries = new();

        /// <summary>太吾生成的 modIdStr（"0_N" 格式，随加载顺序变化）。仅用于 Harmony Id / GetSetting / AddModMethod</summary>
        private static string _modIdStr = "";

        /// <summary>日志前缀（固定可读，与 modIdStr 解耦）。modIdStr 是太吾生成的 "0_N"，序号会变且不可读。</summary>
        private const string _logTag = "AdjustMod";

        // ---- 反射缓存 ----
        private static FieldInfo _fiItemKey;
        private static FieldInfo _fiItemData;
        private static FieldInfo _fiCharId;
        /// <summary>TooltipBook.layoutPage</summary>
        private static FieldInfo _fiLayoutPage;
        /// <summary>TooltipBookPage.textIncompleteState（"完整/残缺"）</summary>
        private static FieldInfo _fiTextIncompleteState;

        // ---- 制造自动填充 反射缓存 ----
        // 当前游戏版本（1.0.20.x）的制造 UI 走 Game.Views.Make.MakeSubPageMake（不是老的 UI_Make）。
        // MakeSubPageMake 是 class，ResourceInts 是 struct。用 FieldRefAccess 拿 ref 才能原地改 struct 字段。
        /// <summary>总物资上限</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, short> _refMaxMakeResourceTotalCount;
        /// <summary>各材料上限</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts> _refMaxMakeResourceCountInts;
        /// <summary>当前投入量</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts> _refCurMakeResourceCountInts;
        /// <summary>上次投入量（影响下次默认值）</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts> _refLastMakeResourceCountInts;
        /// <summary>主材料类型（上限最高的，如玉石）</summary>
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, sbyte> _refMainRequiredResourceType;
        /// <summary>MakeSubPageMake.RefreshResourcePanel() 刷新显示</summary>
        private static MethodInfo _miRefreshResourcePanel;

        // ---- 突破自动选择 反射缓存 ----
        // CombatSkillPanel（Game.Components.Common）是修行界面里选功法后的总纲/篇章面板。
        // _skillData.CombatSkillDisplayData.ReadingState（ushort 位掩码）= 各页是否已读：bit0-4 总纲, bit5-9 正练, bit10-14 逆练。
        // outlinePageToggleGroup(总纲,CToggleGroup) / otherPageToggleGroup(篇章,CToggleGroupMultiSelect) 是 public 字段。
        private static AccessTools.FieldRef<CombatSkillPanel, CombatSkillPracticeDisplayData> _refPracticeSkillData;

        // ---- 突破界面疗伤按钮 反射缓存 ----
        // ViewCharacterMenuSkillBreakPlate（Game.Views.SkillBreak）是走格子的实际突破界面。
        // patch InitRefers 创建「疗伤(N)」按钮，patch RefreshWithPlate 刷新 N。
        // _taiwuCharId = 大夫=病患=主角。buttonClose 用于克隆按钮样式 + 取父 canvas。
        private static AccessTools.FieldRef<ViewCharacterMenuSkillBreakPlate, int> _refTaiwuCharId;
        /// <summary>每个突破界面实例 → 它的疗伤按钮 CButton（避免重复创建）</summary>
        private static readonly Dictionary<ViewCharacterMenuSkillBreakPlate, CButton> _healButtons = new();

        /// <summary>插件初始化：建立反射缓存，注册全部 Harmony postfix（书籍状态/制造填充/突破选择/疗伤按钮）</summary>
        public override void Initialize()
        {
            _modIdStr = ModIdStr;

            _fiItemKey = typeof(Game.Views.MouseTips.Item.Common.TooltipItemBase).GetField("_itemKey",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fiItemData = typeof(Game.Views.MouseTips.Item.Common.TooltipItemBase).GetField("_itemData",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fiCharId = typeof(Game.Views.MouseTips.Item.Common.TooltipItemBase).GetField("_charId",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fiLayoutPage = typeof(Game.Views.MouseTips.Item.TooltipBook).GetField("layoutPage",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fiTextIncompleteState = typeof(Game.Views.MouseTips.Item.TooltipBookPage).GetField("textIncompleteState",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var harmony = new Harmony(ModIdStr);
            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Static;

            // 1) TooltipBook.Refresh（无参）：检测 NPC 上下文，发起后端查询
            var tbPostfix = typeof(ModMain).GetMethod(nameof(TooltipBook_Refresh_Postfix), BF, null,
                new[] { typeof(Game.Views.MouseTips.Item.TooltipBook) }, null);
            PatchMethod(harmony, typeof(Game.Views.MouseTips.Item.TooltipBook), "Refresh", Type.EmptyTypes, tbPostfix);

            // 2) TooltipBookPage.Refresh：每页渲染的叶节点，原版写完 textIncompleteState 后追加 NPC 状态
            var tbpPostfix = typeof(ModMain).GetMethod(nameof(TooltipBookPage_Refresh_Postfix), BF, null,
                new[] { typeof(Game.Views.MouseTips.Item.TooltipBookPage), typeof(int) }, null);
            PatchMethod(harmony, typeof(Game.Views.MouseTips.Item.TooltipBookPage), "Refresh",
                new[] { typeof(bool), typeof(int), typeof(sbyte), typeof(sbyte), typeof(sbyte) }, tbpPostfix);

            // ---- 制造自动填充：patch MakeSubPageMake.ResetResourceCount ----
            // 选材料走 OnItemClickMaterial → SelectMaterial → ResetResourceCount，后者设上限+清零 cur。
            // ResetResourceCount 跑完时上限已设好、cur 已清零，正是填充时机。多材料时原版默认填 0（全 0 bug 的根因）。
            InitCraftAutofillCaches();
            var makePostfix = typeof(ModMain).GetMethod(nameof(ResetResourceCount_Postfix), BF, null,
                new[] { typeof(Game.Views.Make.MakeSubPageMake) }, null);
            PatchMethod(harmony, typeof(Game.Views.Make.MakeSubPageMake), "ResetResourceCount",
                Type.EmptyTypes, makePostfix);

            // ---- 突破自动选择：patch CombatSkillPanel.UpdateBreakPlate ----
            // 修行界面选功法后 CombatSkillPanel.Set 会刷新总纲/篇章面板。UpdateBreakPanel 用 activationState(未激活)算选中 → 空。
            // 这里用 ReadingState(已读)自动补选：总纲没选则随机选已读的；篇章没选则优先正练。
            InitBreakAutofillCaches();
            var breakPostfix = typeof(ModMain).GetMethod(nameof(UpdateBreakPanel_Postfix), BF, null,
                new[] { typeof(CombatSkillPanel) }, null);
            PatchMethod(harmony, typeof(CombatSkillPanel), "UpdateBreakPanel",
                Type.EmptyTypes, breakPostfix);

            // ---- 突破界面疗伤按钮：patch InitRefers(创建按钮) + RefreshWithPlate(刷新N) ----
            InitHealButtonCaches();
            var healInitPostfix = typeof(ModMain).GetMethod(nameof(SkillBreakPlate_InitRefers_Postfix), BF, null,
                new[] { typeof(ViewCharacterMenuSkillBreakPlate) }, null);
            PatchMethod(harmony, typeof(ViewCharacterMenuSkillBreakPlate), "InitRefers",
                Type.EmptyTypes, healInitPostfix);
            var healRefreshPostfix = typeof(ModMain).GetMethod(nameof(SkillBreakPlate_RefreshWithPlate_Postfix), BF, null,
                new[] { typeof(ViewCharacterMenuSkillBreakPlate) }, null);
            PatchMethod(harmony, typeof(ViewCharacterMenuSkillBreakPlate), "RefreshWithPlate",
                new[] { typeof(GameData.Domains.Taiwu.SkillBreakPlate), typeof(Action) }, healRefreshPostfix);

            Debug.Log($"[{_logTag}] 已加载 - TooltipBook / TooltipBookPage / MakeSubPageMake / CombatSkillPanel / 疗伤按钮 patch 完成");
        }

        /// <summary>初始化制造自动填充用的反射缓存（字段 ref + 方法）</summary>
        private static void InitCraftAutofillCaches()
        {
            try
            {
                _refMaxMakeResourceTotalCount = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, short>("_maxMakeResourceTotalCount");
                _refMaxMakeResourceCountInts = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts>("_maxMakeResourceCountInts");
                _refCurMakeResourceCountInts = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts>("_curMakeResourceCountInts");
                _refLastMakeResourceCountInts = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts>("_lastMakeResourceCountInts");
                _refMainRequiredResourceType = AccessTools.FieldRefAccess<Game.Views.Make.MakeSubPageMake, sbyte>("_mainRequiredResourceType");
                _miRefreshResourcePanel = AccessTools.Method(typeof(Game.Views.Make.MakeSubPageMake), "RefreshResourcePanel", Type.EmptyTypes);
                Debug.Log($"[{_logTag}] 制造自动填充反射缓存：{_refCurMakeResourceCountInts != null}, {_miRefreshResourcePanel != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] 制造自动填充反射缓存初始化异常: {ex.Message}");
            }
        }

        /// <summary>初始化突破自动选择用的反射缓存</summary>
        private static void InitBreakAutofillCaches()
        {
            try
            {
                _refPracticeSkillData = AccessTools.FieldRefAccess<CombatSkillPanel, CombatSkillPracticeDisplayData>("_skillData");
                Debug.Log($"[{_logTag}] 突破自动选择反射缓存：skillData={_refPracticeSkillData != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] 突破自动选择反射缓存初始化异常: {ex.Message}");
            }
        }

        /// <summary>初始化突破界面疗伤按钮的反射缓存</summary>
        private static void InitHealButtonCaches()
        {
            try
            {
                _refTaiwuCharId = AccessTools.FieldRefAccess<ViewCharacterMenuSkillBreakPlate, int>("_taiwuCharId");
                Debug.Log($"[{_logTag}] 疗伤按钮反射缓存：taiwuCharId={_refTaiwuCharId != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] 疗伤按钮反射缓存初始化异常: {ex.Message}");
            }
        }

        /// <summary>通用 Harmony patch helper：按方法名+参数类型绑定指定重载，挂 postfix。找不到目标方法时打日志跳过</summary>
        private static void PatchMethod(Harmony harmony, Type type, string methodName, Type[] paramTypes, MethodInfo postfix)
        {
            try
            {
                var original = AccessTools.Method(type, methodName, paramTypes);
                if (original == null)
                {
                    Debug.Log($"[{_logTag}] 未找到 {type.Name}.{methodName}({string.Join(",", paramTypes.Select(p => p.Name))})");
                    return;
                }
                harmony.CreateProcessor(original).AddPostfix(postfix).Patch();
                Debug.Log($"[{_logTag}] 已 patch {type.Name}.{methodName}({string.Join(",", paramTypes.Select(p => p.Name))})");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] patch {type.Name}.{methodName} 异常: {ex.Message}");
            }
        }

        /// <summary>插件卸载：清空阅读状态缓存与进行中的查询去重表</summary>
        public override void Dispose()
        {
            _readStateCache.Clear();
            _pendingQueries.Clear();
        }

        // ==============================
        // 设置读取 / 调试日志（通用 helper）
        // ==============================

        /// <summary>
        /// 读取 bool 设置项。读不到（key 不存在或游戏未注入）时返回 defaultValue。
        /// 每次 patch 触发时读，不缓存——保证玩家改设置后即时生效，无需重启。
        /// </summary>
        private static bool GetSettingBool(string key, bool defaultValue)
        {
            try
            {
                bool val = defaultValue;
                return ModManager.GetSetting(_modIdStr, key, ref val) ? val : defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// 调试日志：仅当 DebugMode 设置开启时输出到 Player.log。
        /// 用于运行时明细（填充结果、查询过程等）；注册/加载类启动日志用 Debug.Log 常开。
        /// </summary>
        private static void LogDebug(string msg)
        {
            if (GetSettingBool("DebugMode", false))
                Debug.Log($"[{_logTag}] {msg}");
        }

        /// <summary>组合 (charId, bookId) 为单个 long 作为去重键</summary>
        private static long MakeQueryKey(int charId, int bookId) => ((long)charId << 32) | (uint)bookId;

        /// <summary>Patch: TooltipBook.Refresh —— 检测 NPC 上下文，发起后端查询并缓存</summary>
        private static void TooltipBook_Refresh_Postfix(Game.Views.MouseTips.Item.TooltipBook __instance)
        {
            if (!GetSettingBool("BookReadStatus", true)) return;
            try
            {
                var itemDataObj = _fiItemData?.GetValue(__instance);
                var itemKeyObj = _fiItemKey?.GetValue(__instance);

                // 用 _itemData.OwnerCharId（物品所属角色）。_charId 原版从不赋值，恒为 0（见 ResolveNpcCharId 注释）。
                int npcCharId = (itemDataObj is ItemDisplayData id) ? id.OwnerCharId : -1;

                if (itemKeyObj is not ItemKey bookKey) return;
                if (npcCharId <= 0) return;
                // 太吾（主角）的进度由原版显示，跳过
                if (IsTaiwu(npcCharId)) return;

                // 缓存命中则无需查询
                if (_readStateCache.TryGetValue(npcCharId, out var bookCache) &&
                    bookCache.ContainsKey(bookKey.Id))
                {
                    return;
                }

                // 同一 (NPC, 书) 只查一次
                long queryKey = MakeQueryKey(npcCharId, bookKey.Id);
                if (_pendingQueries.Contains(queryKey)) return;
                QueryNpcBookReadState(__instance, npcCharId, bookKey);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] TooltipBook Refresh postfix 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch: TooltipBookPage.Refresh —— 原版渲染完每页后，追加该 NPC 的阅读状态。
        /// 参数 index 是页索引（0 起），用于定位 readState[index]。
        /// </summary>
        private static void TooltipBookPage_Refresh_Postfix(Game.Views.MouseTips.Item.TooltipBookPage __instance, int index)
        {
            if (!GetSettingBool("BookReadStatus", true)) return;
            try
            {
                // 从父级链向上找 TooltipBook，取其 _itemKey / _itemData / _charId
                var tooltipBook = FindParentTooltipBook(__instance.transform);
                if (tooltipBook == null) return;

                int npcCharId = ResolveNpcCharId(tooltipBook);
                var itemKeyObj = _fiItemKey?.GetValue(tooltipBook);
                if (itemKeyObj is not ItemKey bookKey) return;
                if (npcCharId <= 0) return;
                // 太吾（主角）的进度由原版显示，跳过
                if (IsTaiwu(npcCharId)) return;

                // 缓存未命中（首次悬停时后端查询可能还没回来），跳过；
                // 查询回来后由 QueryNpcBookReadState 回调里的 ApplyTagsToTooltip 直接补写
                if (!_readStateCache.TryGetValue(npcCharId, out var bookCache) ||
                    !bookCache.TryGetValue(bookKey.Id, out var readState) || readState == null)
                {
                    return;
                }

                ApplyTagToPage(__instance, index, readState);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] TooltipBookPage Refresh postfix 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 TooltipBook 实例解析出当前查看的 NPC 角色 ID。
        /// 用 _itemData.OwnerCharId（物品所属角色）。
        /// 注意：不用 _charId——原版从不给 TooltipBook 的 _charId 赋值（恒为 0，死值）；
        /// 也不用 CharacterMenu.CurCharacterId——那是顶部角色滚动条选中者，与具体物品解耦，
        /// 在「查看不在队伍的人/列表第一项」时会错位取到列表第一个角色。
        /// </summary>
        private static int ResolveNpcCharId(Component tooltipBook)
        {
            var itemDataObj = _fiItemData?.GetValue(tooltipBook);
            return (itemDataObj is ItemDisplayData id) ? id.OwnerCharId : -1;
        }

        /// <summary>判断 charId 是否为太吾（主角）。主角进度由原版显示，本插件不处理</summary>
        private static bool IsTaiwu(int charId)
        {
            try
            {
                return charId > 0 && charId == SingletonObject.getInstance<BasicGameData>().TaiwuCharId;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把单页的 NPC 阅读状态追加到 textIncompleteState（"完整/残缺"控件）</summary>
        private static void ApplyTagToPage(Game.Views.MouseTips.Item.TooltipBookPage page, int index, bool[] readState)
        {
            if (page == null || index < 0 || index >= readState.Length) return;

            var textState = _fiTextIncompleteState?.GetValue(page) as TMP_Text;
            if (textState == null) return;

            string cur = textState.text;
            if (cur.Contains("此人")) return; // 已追加过，防重复

            textState.text = $"{cur} {NpcReadTag(readState[index])}";
        }

        /// <summary>生成带颜色的 NPC 阅读状态富文本标签：已读蓝色，未读灰色</summary>
        private static string NpcReadTag(bool read) => read
            ? "<color=#88ccff>此人已读</color>"
            : "<color=#888888>此人未读</color>";

        /// <summary>遍历 TooltipBook 的所有页，补写 NPC 阅读状态（用于后端数据返回后直接补写）</summary>
        private static void ApplyTagsToTooltip(Component tooltipBook, int npcCharId, int bookId)
        {
            try
            {
                if (!_readStateCache.TryGetValue(npcCharId, out var bookCache) ||
                    !bookCache.TryGetValue(bookId, out var readState) || readState == null)
                    return;

                var layoutPage = _fiLayoutPage?.GetValue(tooltipBook) as Transform;
                if (layoutPage == null) return;

                var pages = layoutPage.GetComponentsInChildren<Game.Views.MouseTips.Item.TooltipBookPage>(true);
                if (pages == null) return;

                // 不用 isActiveAndEnabled 过滤：首次悬停时本回调可能先于原版 OnGetPageInfo 激活页面，
                // 若跳过未激活页会导致「列表第一项不显示已读未读」。GetComponentsInChildren(true) 已含未激活节点，
                // 直接写 textIncompleteState 无害（后续重新渲染会重新追加或保持）。
                for (int i = 0; i < pages.Length && i < readState.Length; i++)
                {
                    ApplyTagToPage(pages[i], i, readState);
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] ApplyTagsToTooltip 异常: {ex.Message}");
            }
        }

        /// <summary>沿父级链向上查找 TooltipBook 组件</summary>
        private static Game.Views.MouseTips.Item.TooltipBook FindParentTooltipBook(Transform t)
        {
            while (t != null)
            {
                var book = t.GetComponent<Game.Views.MouseTips.Item.TooltipBook>();
                if (book != null) return book;
                t = t.parent;
            }
            return null;
        }

        /// <summary>
        /// Patch: MakeSubPageMake.ResetResourceCount —— 选材料后自动按规则填满资源。
        ///
        /// 当前游戏版本（1.0.20.x）的制造 UI 走 Game.Views.Make.MakeSubPageMake（不是老的 UI_Make）。
        /// 选材料流程：OnItemClickMaterial → SelectMaterial → ResetResourceCount。
        /// ResetResourceCount 设上限(_maxMakeResourceCountInts/_maxMakeResourceTotalCount)、找出主材料类型
        /// (_mainRequiredResourceType=上限最高的，如玉石)、清零 _curMakeResourceCountInts。
        /// 多材料时原版默认把 cur 填成 _lastMakeResourceCountInts（首次为 0）→ 资源区全 0。这里覆盖为按规则填满。
        /// </summary>
        private static void ResetResourceCount_Postfix(Game.Views.Make.MakeSubPageMake __instance)
        {
            if (!GetSettingBool("AutoFillCraftMaterial", true)) return;
            if (__instance == null) return;
            if (_refCurMakeResourceCountInts == null || _refMaxMakeResourceCountInts == null
                || _refMaxMakeResourceTotalCount == null || _miRefreshResourcePanel == null
                || _refMainRequiredResourceType == null || _refLastMakeResourceCountInts == null) return;

            try
            {
                DoCraftAutofill(__instance);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] ResetResourceCount postfix 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 自动填充核心：把非主材料(Wood/Metal/Fabric)填到各自类型上限，剩余额度全投主材料(_mainRequiredResourceType，如玉石)。
        ///
        /// 自己计算最终值并直接写入 _curMakeResourceCountInts 和 _lastMakeResourceCountInts（后者影响下次默认值），
        /// 然后调 RefreshResourcePanel 刷新显示。
        /// </summary>
        private static void DoCraftAutofill(Game.Views.Make.MakeSubPageMake make)
        {
            try
            {
                short maxTotal = _refMaxMakeResourceTotalCount(make);
                ref var maxPerType = ref _refMaxMakeResourceCountInts(make);
                ref var cur = ref _refCurMakeResourceCountInts(make);
                ref var last = ref _refLastMakeResourceCountInts(make);
                sbyte mainType = _refMainRequiredResourceType(make);

                if (maxTotal <= 0) return;

                // 统计有上限的材料种类数；只有 1 种时原版已自动填满，不必干预。
                int typeCount = 0;
                for (sbyte t = 0; t < 6; t++) if (maxPerType.Get(t) > 0) typeCount++;
                if (typeCount <= 1) return;

                var target = new int[6];
                int budget = maxTotal;

                // 步骤1：非主材料各填到 min(类型上限, 剩余额度)
                for (sbyte t = 0; t < 6; t++)
                {
                    if (t == mainType) continue;
                    int cap = maxPerType.Get(t);
                    if (cap <= 0 || budget <= 0) { target[t] = 0; continue; }
                    int take = Math.Min(cap, budget);
                    target[t] = take;
                    budget -= take;
                }

                // 步骤2：剩余额度全给主材料（_mainRequiredResourceType，上限最高的，如玉石）
                if (budget > 0)
                {
                    int cap = maxPerType.Get(mainType);
                    target[mainType] = cap > 0 ? Math.Min(cap, budget) : 0;
                }
                else target[mainType] = 0;

                // 写入 cur 和 last（last 决定下次选同材料时的默认值）
                for (int t = 0; t < 6; t++)
                {
                    cur.Set(t, target[t]);
                    last.Set(t, target[t]);
                }

                // 刷新显示
                _miRefreshResourcePanel.Invoke(make, null);

                LogDebug($"制造自动填充：main={mainType}, 各类=[{target[0]},{target[1]},{target[2]},{target[3]},{target[4]},{target[5]}], 总额上限={maxTotal}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] DoCraftAutofill 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch: CombatSkillPanel.UpdateBreakPanel —— 选功法后自动选总纲/正逆练篇章。
        ///
        /// 修行界面选功法后 CombatSkillPanel.Set 调 UpdateBreakPanel 刷新总纲/篇章。
        /// 原版未突破时用 activationState(未激活)算选中 → 空。这里用 ReadingState(已读)自动补选。
        /// 位掩码：bit0-4 总纲, bit5-9 正练, bit10-14 逆练。
        /// </summary>
        private static void UpdateBreakPanel_Postfix(CombatSkillPanel __instance)
        {
            if (!GetSettingBool("AutoBreakSelect", true)) return;
            if (__instance == null) return;
            if (_refPracticeSkillData == null) return;

            try
            {
                DoBreakAutofill(__instance);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] UpdateBreakPanel postfix 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 用 ReadingState(已读)自动选没选的总纲/篇章。规则：
        ///   总纲没选 → 从已读的总纲(bit0-4)随机选一个；一个都没读 → return（不碰篇章）
        ///   篇章没选 → 对 pageId 1-5 优先选正练已读(bit5-9)，否则逆练已读(bit10-14)
        /// 已选的总纲/篇章不覆盖（尊重玩家手动选择）。
        /// </summary>
        private static void DoBreakAutofill(CombatSkillPanel panel)
        {
            var skillData = _refPracticeSkillData(panel);
            if (skillData?.CombatSkillDisplayData == null) return;
            ushort readingState = skillData.CombatSkillDisplayData.ReadingState;

            var outlineTg = panel.outlinePageToggleGroup;   // CToggleGroup（单选）
            var otherTg = panel.otherPageToggleGroup;       // CToggleGroupMultiSelect（多选）

            // ---- 总纲：bit0-4，找已读的 ----
            var readOutlines = new List<sbyte>();
            for (sbyte i = 0; i < outlineTg.Count(); i++)
            {
                if (CombatSkillStateHelper.IsPageRead(readingState, CombatSkillStateHelper.GetOutlinePageInternalIndex(i)))
                    readOutlines.Add(i);
            }
            // 一个总纲都没读 → 直接结束，不碰篇章
            if (readOutlines.Count == 0) return;

            // 总纲已选就不动（CToggleGroup.GetActiveIndex() >= 0 表示已选）
            if (outlineTg.GetActiveIndex() < 0)
            {
                sbyte pick = readOutlines.Count == 1 ? readOutlines[0] : readOutlines[UnityEngine.Random.Range(0, readOutlines.Count)];
                outlineTg.Set(pick);
                LogDebug($"突破自动选总纲：index={pick}");
            }

            // ---- 篇章：pageId 1-5，正练 bit(5+i) 优先，否则逆练 bit(10+i) ----
            // 篇章已选就不动（CToggleGroupMultiSelect 任一已选即跳过）
            if (otherTg.GetIsOnCount() == 0)
            {
                for (byte pageId = 1; pageId <= 5; pageId++)
                {
                    byte directIdx = CombatSkillStateHelper.GetNormalPageInternalIndex(0, pageId);   // 正练 5-9
                    byte reverseIdx = CombatSkillStateHelper.GetNormalPageInternalIndex(1, pageId);  // 逆练 10-14
                    if (CombatSkillStateHelper.IsPageRead(readingState, directIdx))
                        otherTg.SelectWithoutNotify(pageId - 1);   // toggle 索引 0-4 对应 pageId 1-5（正练侧）
                    else if (CombatSkillStateHelper.IsPageRead(readingState, reverseIdx))
                        otherTg.SelectWithoutNotify(pageId + 4);   // 逆练侧索引 5-9
                }
                LogDebug("突破自动选篇章完成（优先正练）");
            }
        }

        /// <summary>
        /// Patch: ViewCharacterMenuSkillBreakPlate.InitRefers —— 突破界面加「疗伤」按钮（RefreshWithPlate 负责刷新可点状态）。
        ///
        /// 在实际突破（走格子）界面加一个「疗伤」按钮：能治（有伤可治）就可点，不能治则灰色。
        /// 大夫=病患=太吾主角。点击调 MapDomainMethod.Call.HealOnMap 治一次伤势(typeInt=0)，然后重新模拟刷新可点状态。
        /// 按钮挂在 buttonClose 同父容器、放右上角关闭按钮左侧（中间留白），TMP 字体从界面已有标签复制。
        /// </summary>
        private static void SkillBreakPlate_InitRefers_Postfix(ViewCharacterMenuSkillBreakPlate __instance)
        {
            if (!GetSettingBool("AutoHealButton", true)) return;
            if (__instance == null || _refTaiwuCharId == null) return;
            if (_healButtons.ContainsKey(__instance)) return;   // 已创建过

            try
            {
                CreateHealButton(__instance);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] 疗伤按钮创建异常: {ex.Message}");
            }
        }

        /// <summary>反射读取 ViewCharacterMenuSkillBreakPlate 的私有字段（值类型/引用类型通用）</summary>
        private static T GetField<T>(ViewCharacterMenuSkillBreakPlate view, string fieldName) where T : class
        {
            return typeof(ViewCharacterMenuSkillBreakPlate)
                .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(view) as T;
        }

        /// <summary>判定一个 TMP 是否可用作字体模板（有 font 且有 material）</summary>
        private static bool IsValidFontSource(TextMeshProUGUI txt)
        {
            return txt != null && txt.font != null && txt.fontSharedMaterial != null;
        }

        /// <summary>从突破界面取一个可用的 TMP 当字体模板：优先 maxPowerLabel，否则在容器/界面里找一个</summary>
        private static TextMeshProUGUI FindFontTemplate(ViewCharacterMenuSkillBreakPlate view, Transform container)
        {
            var primary = GetField<TextMeshProUGUI>(view, "maxPowerLabel");
            if (IsValidFontSource(primary)) return primary;
            if (container != null)
            {
                var found = container.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(IsValidFontSource);
                if (found != null) return found;
            }
            return (view as Component).GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(IsValidFontSource);
        }

        /// <summary>创建「疗伤」按钮：
        /// 挂在 buttonClose 同父容器、放右上角关闭按钮左侧（大幅留白）、纯色矩形、TMP 字体从界面已有标签复制</summary>
        private static void CreateHealButton(ViewCharacterMenuSkillBreakPlate view)
        {
            // 父容器：buttonClose 同父（确保按钮和关闭按钮在同一层级，坐标对齐）
            var closeBtn = GetField<CButton>(view, "buttonClose");
            if (closeBtn == null) { Debug.Log($"[{_logTag}] 疗伤按钮：找不到 buttonClose"); return; }
            var closeRt = closeBtn.GetComponent<RectTransform>();
            var containerRt = closeRt.parent;

            // 字体模板：从界面已有的 TMP 标签复制 font/material/spriteAsset（关键，否则中文是空）
            var fontSrc = FindFontTemplate(view, containerRt);
            if (fontSrc == null) { Debug.Log($"[{_logTag}] 疗伤按钮：找不到可用字体模板 TMP"); return; }

            // 纯色矩形按钮：GameObject + RectTransform + Image(纯色) + CButton
            var go = new GameObject("AdjustMod_HealButton", typeof(RectTransform), typeof(Image), typeof(CButton));
            go.transform.SetParent(containerRt, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = closeRt.anchorMin;
            rt.anchorMax = closeRt.anchorMax;
            rt.pivot = closeRt.pivot;
            // 放在 buttonClose 左侧，中间留大量空白（向左偏移 220）
            float myW = 120f;
            rt.sizeDelta = new Vector2(myW, closeRt.sizeDelta.y);
            float offsetX = -(closeRt.sizeDelta.x * 0.5f + myW * 0.5f + 220f);
            rt.anchoredPosition = closeRt.anchoredPosition + new Vector2(offsetX, 0f);
            // 置顶渲染，避免被遮挡
            go.transform.SetAsLastSibling();

            var img = go.GetComponent<Image>();
            img.color = new Color(0.16f, 0.12f, 0.08f, 0.86f);   // 参考 MOD 的深色半透明
            img.raycastTarget = true;

            // CButton 配色（照搬参考 MOD 的 ColorBlock）
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
            txt.font = fontSrc.font;
            txt.fontSharedMaterial = fontSrc.fontSharedMaterial;
            txt.spriteAsset = fontSrc.spriteAsset;

            btn.ClearAndAddListener((Action)(() => OnHealButtonClick(view, btn)));
            _healButtons[view] = btn;
            // 初始按「不可点」显示，等 SimulateAndRefresh 回来再按真实状态切
            SetHealButtonVisual(btn, false);
            LogDebug($"疗伤按钮已创建（容器={containerRt.name}, 字体源={fontSrc.name}）");
        }

        /// <summary>根据可点状态切换疗伤按钮视觉：可点=亮底+金色字，不可点=暗底+灰字（差异明显）</summary>
        private static void SetHealButtonVisual(CButton btn, bool canHeal)
        {
            if (btn == null) return;
            // 背景直接着色（不走 interactable 的淡入淡出，状态切换更干脆）
            var img = btn.targetGraphic as Image;
            if (img != null)
            {
                img.color = canHeal
                    ? new Color(0.24f, 0.40f, 0.26f, 0.92f)   // 可点：墨绿亮底
                    : new Color(0.12f, 0.12f, 0.12f, 0.55f);  // 不可点：深灰暗底
            }
            // 文字颜色跟着切（这是差异最明显的点）
            var txt = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (txt != null)
            {
                txt.color = canHeal
                    ? new Color(1f, 0.92f, 0.55f)   // 可点：暖金色
                    : new Color(0.45f, 0.45f, 0.45f); // 不可点：中灰
            }
        }

        /// <summary>点击疗伤：治一次，然后重新模拟刷新可点状态</summary>
        private static void OnHealButtonClick(ViewCharacterMenuSkillBreakPlate view, CButton btn)
        {
            try
            {
                int taiwuId = _refTaiwuCharId(view);
                // needPay=true, payerId=taiwuId, isExpensiveHeal=false（普通疗伤，消耗药材不消耗额外金钱/恩义）
                MapDomainMethod.Call.HealOnMap(view.Element.GameDataListenerId, 0, taiwuId, taiwuId, true, taiwuId, false);
                LogDebug($"疗伤点击：HealOnMap(taiwu={taiwuId})");
                // 治完后重新模拟刷新可点状态（有伤=可点，无伤=灰）
                SimulateAndRefresh(view, btn);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] 疗伤点击异常: {ex.Message}");
            }
        }

        /// <summary>RefreshWithPlate postfix：突破板刷新时，重新模拟决定疗伤按钮可点/灰</summary>
        private static void SkillBreakPlate_RefreshWithPlate_Postfix(ViewCharacterMenuSkillBreakPlate __instance)
        {
            if (!GetSettingBool("AutoHealButton", true)) return;
            if (__instance == null) return;
            if (!_healButtons.TryGetValue(__instance, out var btn) || btn == null) return;
            SimulateAndRefresh(__instance, btn);
        }

        /// <summary>异步模拟疗伤消耗：HealEffect > 0（有伤可治）则按钮可点，否则灰</summary>
        private static void SimulateAndRefresh(ViewCharacterMenuSkillBreakPlate view, CButton btn)
        {
            try
            {
                int taiwuId = _refTaiwuCharId(view);
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
                            // 可点/不可点视觉差异要明显：文字颜色 + 背景透明度都跟着切
                            SetHealButtonVisual(btn, canHeal);
                            LogDebug($"疗伤模拟：HealEffect={result.HealEffect} canHeal={canHeal}");
                        }
                        catch (Exception ex) { Debug.Log($"[{_logTag}] 疗伤模拟回调异常: {ex.Message}"); }
                    });
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_logTag}] SimulateAndRefresh 异常: {ex.Message}");
            }
        }

        /// <summary>异步查询后端：向 GameData 侧查某 NPC 对某书的阅读状态，回调里写缓存并补写 tooltip</summary>
        private static void QueryNpcBookReadState(Component tooltip, int npcCharId, ItemKey bookKey)
        {
            long queryKey = MakeQueryKey(npcCharId, bookKey.Id);
            _pendingQueries.Add(queryKey);

            var param = new SerializableModData();
            param.Set("npcCharId", npcCharId);
            param.Set("bookItemType", (int)bookKey.ItemType);
            param.Set("bookModState", (int)bookKey.ModificationState);
            param.Set("bookTemplateId", (int)bookKey.TemplateId);
            param.Set("bookId", bookKey.Id);

            AsyncMethodCallbackDelegate callback = (int resultCode, RawDataPool resultPool) =>
            {
                try
                {
                    if (resultCode >= 0)
                    {
                        SerializableModData? result = null;
                        SerializerHolder<SerializableModData>.Deserialize(resultPool, resultCode, ref result);
                        if (result != null &&
                            result.Get("success", out bool success) && success &&
                            result.Get("pageCount", out int pageCount) && pageCount > 0)
                        {
                            var readState = new bool[pageCount];
                            for (int i = 0; i < pageCount; i++)
                                result.Get("p" + i.ToString(), out readState[i]);

                            if (!_readStateCache.ContainsKey(npcCharId))
                                _readStateCache[npcCharId] = new Dictionary<int, bool[]>();
                            _readStateCache[npcCharId][bookKey.Id] = readState;
                        }
                    }

                    // 查询回来后直接补写：首次悬停时游戏的 OnGetPageInfo 已渲染好页面，
                    // 但那时缓存还没数据所以 postfix 跳过了。这里直接遍历页面写入 NPC 状态。
                    if (tooltip != null && tooltip.gameObject != null)
                    {
                        ApplyTagsToTooltip(tooltip, npcCharId, bookKey.Id);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log($"[{_logTag}] NPC 阅读状态回调异常: {ex.Message}");
                }
                finally
                {
                    _pendingQueries.Remove(queryKey);
                }
            };

            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                (IAsyncMethodRequestHandler)tooltip,
                _modIdStr,
                "GetNpcBookReadState",
                param,
                callback
            );
        }
    }
}
