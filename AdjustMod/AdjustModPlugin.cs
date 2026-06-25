using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FrameWork;
using GameData.Domains.Item;
using GameData.Domains.Item.Display;
using GameData.Domains.Mod;
using GameData.Serializer;
using GameData.Utilities;
using HarmonyLib;
using TaiwuModdingLib.Core.Plugin;
using TMPro;
using UnityEngine;

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

        private static string _modIdStr = "";

        /// <summary>日志前缀（固定可读，与 modIdStr 解耦）。modIdStr 是太吾生成的 "0_N"，序号会变且不可读。</summary>
        private const string _logTag = "AdjustMod";

        // ---- 反射缓存 ----
        private static FieldInfo _fiItemKey;
        private static FieldInfo _fiItemData;
        private static FieldInfo _fiCharId;
        private static FieldInfo _fiLayoutPage;             // TooltipBook.layoutPage
        private static FieldInfo _fiTextIncompleteState;    // TooltipBookPage.textIncompleteState（"完整/残缺"）

        // ---- 制造自动填充 反射缓存 ----
        // 当前游戏版本（1.0.20.x）的制造 UI 走 Game.Views.Make.MakeSubPageMake（不是老的 UI_Make）。
        // MakeSubPageMake 是 class，ResourceInts 是 struct。用 FieldRefAccess 拿 ref 才能原地改 struct 字段。
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, short> _refMaxMakeResourceTotalCount;   // 总物资上限
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts> _refMaxMakeResourceCountInts;   // 各材料上限
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts> _refCurMakeResourceCountInts;   // 当前投入量
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, GameData.Domains.Character.ResourceInts> _refLastMakeResourceCountInts;  // 上次投入量（影响下次默认值）
        private static AccessTools.FieldRef<Game.Views.Make.MakeSubPageMake, sbyte> _refMainRequiredResourceType;   // 主材料类型（上限最高的，如玉石）
        private static MethodInfo _miRefreshResourcePanel;   // MakeSubPageMake.RefreshResourcePanel() 刷新显示

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

            Debug.Log($"[{_logTag}] 已加载 - TooltipBook / TooltipBookPage / MakeSubPageMake patch 完成");
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

        // ==============================
        // Patch: TooltipBook.Refresh —— 检测 NPC 上下文，发起后端查询并缓存
        // ==============================
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

        // ==============================
        // Patch: TooltipBookPage.Refresh —— 原版渲染完每页后，追加该 NPC 的阅读状态
        // 参数 index 是页索引（0 起），用于定位 readState[index]
        // ==============================
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

        // ==============================
        // Patch: MakeSubPageMake.ResetResourceCount —— 选材料后自动按规则填满资源
        //
        // 当前游戏版本（1.0.20.x）的制造 UI 走 Game.Views.Make.MakeSubPageMake（不是老的 UI_Make）。
        // 选材料流程：OnItemClickMaterial → SelectMaterial → ResetResourceCount。
        // ResetResourceCount 设上限(_maxMakeResourceCountInts/_maxMakeResourceTotalCount)、找出主材料类型
        // (_mainRequiredResourceType=上限最高的，如玉石)、清零 _curMakeResourceCountInts。
        // 多材料时原版默认把 cur 填成 _lastMakeResourceCountInts（首次为 0）→ 资源区全 0。这里覆盖为按规则填满。
        // ==============================
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

        // ==============================
        // 异步查询后端
        // ==============================
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
