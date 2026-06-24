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

            Debug.Log($"[{ModIdStr}] 已加载 - TooltipBook / TooltipBookPage patch 完成");
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
