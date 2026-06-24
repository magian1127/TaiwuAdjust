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

        // ---- 反射缓存 ----
        private static FieldInfo _fiItemKey;
        private static FieldInfo _fiItemData;
        private static FieldInfo _fiCharId;
        private static FieldInfo _fiLayoutPage;             // TooltipBook.layoutPage
        private static FieldInfo _fiTextIncompleteState;    // TooltipBookPage.textIncompleteState（"完整/残缺"）

        // ---- 制造自动填充 反射缓存 ----
        // UI_Make 是 class，ResourceInts 是 struct。用 FieldRefAccess 拿 ref 才能原地改 struct 字段。
        private static AccessTools.FieldRef<UI_Make, short> _refMaxMakeResourceTotalCount;   // 总物资上限
        private static AccessTools.FieldRef<UI_Make, GameData.Domains.Character.ResourceInts> _refMaxMakeResourceCountInts;   // 各材料上限
        private static AccessTools.FieldRef<UI_Make, GameData.Domains.Character.ResourceInts> _refCurMakeResourceCountInts;   // 当前投入量
        private static MethodInfo _miCheckMakeCondition;    // UI_Make.CheckMakeCondition(bool, Action)

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

            // ---- 制造自动填充：反射缓存 + patch ----
            InitCraftAutofillCaches();
            var makePostfix = typeof(ModMain).GetMethod(nameof(SelectMakeItemSubType_Postfix), BF, null,
                new[] { typeof(UI_Make) }, null);
            PatchMethod(harmony, typeof(UI_Make), "SelectMakeItemSubType",
                new[] { typeof(int), typeof(bool) }, makePostfix);

            Debug.Log($"[{ModIdStr}] 已加载 - TooltipBook / TooltipBookPage / UI_Make patch 完成");
        }

        /// <summary>初始化制造自动填充用的反射缓存（字段 ref + 方法）</summary>
        private static void InitCraftAutofillCaches()
        {
            try
            {
                _refMaxMakeResourceTotalCount = AccessTools.FieldRefAccess<UI_Make, short>("_maxMakeResourceTotalCount");
                _refMaxMakeResourceCountInts = AccessTools.FieldRefAccess<UI_Make, GameData.Domains.Character.ResourceInts>("_maxMakeResourceCountInts");
                _refCurMakeResourceCountInts = AccessTools.FieldRefAccess<UI_Make, GameData.Domains.Character.ResourceInts>("_curMakeResourceCountInts");
                _miCheckMakeCondition = AccessTools.Method(typeof(UI_Make), "CheckMakeCondition",
                    new[] { typeof(bool), typeof(Action) });
                Debug.Log($"[{_modIdStr}] 制造自动填充反射缓存：{_refCurMakeResourceCountInts != null}, {_miCheckMakeCondition != null}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_modIdStr}] 制造自动填充反射缓存初始化异常: {ex.Message}");
            }
        }

        private static void PatchMethod(Harmony harmony, Type type, string methodName, Type[] paramTypes, MethodInfo postfix)
        {
            try
            {
                var original = AccessTools.Method(type, methodName, paramTypes);
                if (original == null)
                {
                    Debug.Log($"[{_modIdStr}] 未找到 {type.Name}.{methodName}({string.Join(",", paramTypes.Select(p => p.Name))})");
                    return;
                }
                harmony.CreateProcessor(original).AddPostfix(postfix).Patch();
                Debug.Log($"[{_modIdStr}] 已 patch {type.Name}.{methodName}({string.Join(",", paramTypes.Select(p => p.Name))})");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_modIdStr}] patch {type.Name}.{methodName} 异常: {ex.Message}");
            }
        }

        public override void Dispose()
        {
            _readStateCache.Clear();
            _pendingQueries.Clear();
        }

        /// <summary>组合 (charId, bookId) 为单个 long 作为去重键</summary>
        private static long MakeQueryKey(int charId, int bookId) => ((long)charId << 32) | (uint)bookId;

        // ==============================
        // Patch: TooltipBook.Refresh —— 检测 NPC 上下文，发起后端查询并缓存
        // ==============================
        private static void TooltipBook_Refresh_Postfix(Game.Views.MouseTips.Item.TooltipBook __instance)
        {
            try
            {
                var itemDataObj = _fiItemData?.GetValue(__instance);
                var itemKeyObj = _fiItemKey?.GetValue(__instance);
                int charId = _fiCharId != null ? (_fiCharId.GetValue(__instance) as int? ?? -1) : -1;

                int ownerCharId = (itemDataObj is ItemDisplayData id) ? id.OwnerCharId : -1;
                // NPC 角色优先取 _charId，其次 OwnerCharId
                int npcCharId = charId > 0 ? charId : ownerCharId;

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
                Debug.Log($"[{_modIdStr}] TooltipBook Refresh postfix 异常: {ex.Message}");
            }
        }

        // ==============================
        // Patch: TooltipBookPage.Refresh —— 原版渲染完每页后，追加该 NPC 的阅读状态
        // 参数 index 是页索引（0 起），用于定位 readState[index]
        // ==============================
        private static void TooltipBookPage_Refresh_Postfix(Game.Views.MouseTips.Item.TooltipBookPage __instance, int index)
        {
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
                Debug.Log($"[{_modIdStr}] TooltipBookPage Refresh postfix 异常: {ex.Message}");
            }
        }

        /// <summary>从 TooltipBook 实例解析出当前查看的 NPC 角色 ID</summary>
        private static int ResolveNpcCharId(Component tooltipBook)
        {
            var itemDataObj = _fiItemData?.GetValue(tooltipBook);
            int charId = _fiCharId != null ? (_fiCharId.GetValue(tooltipBook) as int? ?? -1) : -1;
            int ownerCharId = (itemDataObj is ItemDisplayData id) ? id.OwnerCharId : -1;
            return charId > 0 ? charId : ownerCharId;
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

                for (int i = 0; i < pages.Length && i < readState.Length; i++)
                {
                    if (!pages[i].isActiveAndEnabled) continue;
                    ApplyTagToPage(pages[i], i, readState);
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_modIdStr}] ApplyTagsToTooltip 异常: {ex.Message}");
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
        // Patch: UI_Make.SelectMakeItemSubType —— 制造面板选好装备子类型后，自动按规则填满材料
        // ==============================
        private static void SelectMakeItemSubType_Postfix(UI_Make __instance)
        {
            if (__instance == null) return;
            if (_refCurMakeResourceCountInts == null || _refMaxMakeResourceCountInts == null
                || _refMaxMakeResourceTotalCount == null || _miCheckMakeCondition == null) return;

            try
            {
                // 原方法末尾用 CheckMakeCondition(needRefreshMakeResult:true, ()=>{ AutoFillResource(); }) 异步触发官方填充。
                // 这里再挂一个回调：在官方 AutoFillResource(只填 Food/Herb) 跑完、且上限/数据就绪后，补填 Wood/Metal/Jade/Fabric。
                // 不直接同步写——因为原方法的异步 RefreshMakeResult 还没回来，数据未定。
                ScheduleCraftAutofill(__instance);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_modIdStr}] SelectMakeItemSubType postfix 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 挂一个延迟回调执行自动填充。通过调用游戏的 CheckMakeCondition(true, callback)，
        /// 让 callback 在 RefreshMakeResult + OnCheckMakeCondition 完成后执行（此时数据/官方 AutoFill 都已就绪）。
        /// </summary>
        private static void ScheduleCraftAutofill(UI_Make make)
        {
            try
            {
                _miCheckMakeCondition.Invoke(make, new object[] { true, (Action)(() => DoCraftAutofill(make)) });
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_modIdStr}] ScheduleCraftAutofill 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 自动填充核心：把 Wood/Metal/Fabric/Jade 按规则填满，Food/Herb 保留官方 AutoFill 的结果。
        ///
        /// 规则：先把上限低的材料(Wood/Metal/Fabric)填到各自类型上限，剩余额度全投 Jade(上限最高的那个)。
        /// 自己计算最终值并直接写入字段，不依赖游戏的总额钳制或隐藏削减逻辑。
        /// 仓库不够时：实际可填量会被后续 CheckMakeCondition 的 UI 显示标红，但字段值仍按目标设；
        /// 游戏提交制造时会用 CheckMakeResource 校验，不够则不让确认——这是可接受的(等同玩家手动填超量)。
        /// </summary>
        private static void DoCraftAutofill(UI_Make make)
        {
            try
            {
                short maxTotal = _refMaxMakeResourceTotalCount(make);
                ref var maxPerType = ref _refMaxMakeResourceCountInts(make);
                ref var cur = ref _refCurMakeResourceCountInts(make);

                if (maxTotal <= 0) return;

                // 材料索引：0=Food 1=Wood 2=Metal 3=Jade 4=Fabric 5=Herb
                // 我们处理 1/2/4，再处理 3(Jade)。0/5(Food/Herb) 保留官方 AutoFill 的值。
                // 先收集 Food/Herb 已投入量（官方 AutoFill 填的），它们占用总额度。
                int reserved = cur.Get(0) + cur.Get(5);
                int budget = maxTotal - reserved;   // 可分配给 Wood/Metal/Jade/Fabric 的额度

                // 目标值数组，初始为 Food/Herb 当前值，其余清 0
                var target = new int[6];
                target[0] = cur.Get(0);
                target[5] = cur.Get(5);

                // 步骤1：先填低上限的 Wood(1)/Metal(2)/Fabric(4)，各填到 min(类型上限, 剩余额度)
                int[] order = { 1, 2, 4 };   // Wood, Metal, Fabric
                foreach (int t in order)
                {
                    int cap = maxPerType.Get(t);
                    if (cap <= 0 || budget <= 0) { target[t] = 0; continue; }
                    int take = Math.Min(cap, budget);
                    target[t] = take;
                    budget -= take;
                }

                // 步骤2：剩余额度全给 Jade(3)（上限最高的那个）
                {
                    int cap = maxPerType.Get(3);
                    if (cap > 0 && budget > 0)
                    {
                        target[3] = Math.Min(cap, budget);
                        budget -= target[3];
                    }
                    else target[3] = 0;
                }

                // 写入字段（通过 ref 原地改 struct）
                for (int t = 0; t < 6; t++)
                    cur.Set(t, target[t]);

                // 刷新 UI 显示 + 制造结果预览。needRefreshMakeResult:true 重算成品预览。
                _miCheckMakeCondition.Invoke(make, new object[] { true, null });

                Debug.Log($"[{_modIdStr}] 制造自动填充：W{target[1]} M{target[2]} J{target[3]} Fb{target[4]} (Food{target[0]}/Herb{target[5]}), 总额上限={maxTotal}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[{_modIdStr}] DoCraftAutofill 异常: {ex.Message}");
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
                    Debug.Log($"[{_modIdStr}] NPC 阅读状态回调异常: {ex.Message}");
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
