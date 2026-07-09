using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Components.Common;
using GameData.Domains;
using GameData.Domains.Item;
using GameData.Domains.Item.Display;
using GameData.Domains.Mod;
using GameData.Serializer;
using GameData.Utilities;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// NPC 书籍阅读状态补丁 —— 在查看 NPC 背包书籍时显示该 NPC 的阅读完成状态。
    ///
    /// 【问题背景】
    /// 原版 TooltipBook.OnGetPageInfo 通过 GetSkillBookPagesInfo 取的阅读进度是太吾（主角）自己的，
    /// 即使查看的是 NPC 背包里的书也只显示太吾进度。玩家无法知道 NPC 是否读过某本书。
    ///
    /// 【解决方案：前后端协作 + 两层 patch】
    /// 本功能涉及两个进程的协作：
    ///   - 前端（Unity 进程）：patch TooltipBook / TooltipBookPage 的 UI 渲染，追加标签
    ///   - 后端（GameData.exe 进程）：通过 DomainManager.Character.GetCharBookReadState 查询真实阅读状态
    ///
    /// 【完整逻辑链（从用户悬停到文字显示）】
    ///
    /// 用户悬停 NPC 背包书籍
    ///   ↓
    /// ① TooltipBook.Refresh 触发（parent class 的渲染入口）
    ///   → postfix 检查：设置开关？主角？缓存命中？正在查询？
    ///   → 通过所有检查后，调用 QueryNpcBookReadState 发起异步后端查询
    ///   → 将 (npcCharId, bookId) 加入 PendingQueries 去重表
    ///   ↓
    /// ② 后端 DomainManager.Character.GetCharBookReadState 返回 bool[] readState
    ///   → 回调在主线程执行：将结果写入 ReadStateCache[npcCharId][bookId]
    ///   → 调用 ApplyTagsToTooltip 遍历所有 TooltipBookPage 补写标签
    ///   → 从 PendingQueries 移除去重键
    ///   ↓
    /// ③ TooltipBookPage.Refresh 触发（每页渲染的叶节点方法，参数 index = 页索引）
    ///   → postfix 检查：设置开关？NPC 上下文？缓存命中？
    ///   → 缓存命中 → 调用 ApplyTagToPage 在 textIncompleteState 后追加 "此人已读/未读"
    ///   → 缓存未命中（首次悬停时后端还没回来）→ 跳过，等②的回调补写
    ///
    /// 【为什么 patch 两层而不是一层？】
    ///   - TooltipBook.Refresh 是入口，用于发起查询（此时还没数据，只能发请求）
    ///   - TooltipBookPage.Refresh 是叶节点，用于追加标签（此时原版已设置好 textIncompleteState）
    ///   - 如果只 patch TooltipBook.Refresh，在回调里直接写所有页 → 页面重渲染时会被 OnGetPageInfo 覆盖
    ///   - patch 叶节点 TooltipBookPage.Refresh 确保每次重渲染都能重新追加，不会被覆盖
    ///
    /// 【缓存策略】
    ///   - 缓存键：(npcCharId, bookId)，与浮窗实例解耦（同一本书在不同浮窗间共享）
    ///   - 去重键：MakeQueryKey(npcCharId, bookId) 组合为 long，同一 (NPC, 书) 只查一次
    ///   - 缓存生命周期：查询回调写入 → Dispose() 清空
    /// </summary>
    [HarmonyPatch]
    internal static class BookReadStatusPatch
    {
        #region 反射缓存
        // 以下字段在 Init() 中一次性建立缓存，避免 patch 运行时重复反射。
        // 缓存为 null 表示初始化失败，patch 运行时会安全跳过（if (_fi == null) return）。

        /// <summary>TooltipItemBase._itemKey —— 物品键（ItemKey 结构体），用于识别具体哪本书</summary>
        private static FieldInfo? _fiItemKey;

        /// <summary>TooltipItemBase._itemData —— 物品显示数据（ItemDisplayData），包含 OwnerCharId（所属角色 ID）</summary>
        private static FieldInfo? _fiItemData;

        /// <summary>TooltipBook.layoutPage —— 页面布局容器（Transform），遍历其子节点获取所有 TooltipBookPage</summary>
        private static FieldInfo? _fiLayoutPage;

        /// <summary>TooltipBookPage.textIncompleteState —— "完整/残缺" 文本控件（TMP_Text），
        /// 原版在此显示书籍完整性，我们在其后追加 NPC 阅读状态标签</summary>
        private static FieldInfo? _fiTextIncompleteState;

        /// <summary>
        /// 建立反射缓存。在 ModMain.Initialize() 中调用，早于 harmony.PatchAll()。
        ///
        /// 目标类型说明：
        ///   - TooltipItemBase：所有物品浮窗的基类（含 _itemKey、_itemData 字段）
        ///   - TooltipBook：书籍浮窗（继承 TooltipItemBase），有 layoutPage 字段
        ///   - TooltipBookPage：书籍的单页渲染组件，有 textIncompleteState 字段
        ///   - 这些类型在 Game.Views.MouseTips.Item 命名空间下，是角色菜单的独立 tooltip 系统
        /// </summary>
        internal static void Init()
        {
            _fiItemKey = typeof(Game.Views.MouseTips.Item.Common.TooltipItemBase).GetField("_itemKey",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fiItemData = typeof(Game.Views.MouseTips.Item.Common.TooltipItemBase).GetField("_itemData",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fiLayoutPage = typeof(Game.Views.MouseTips.Item.TooltipBook).GetField("layoutPage",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fiTextIncompleteState = typeof(Game.Views.MouseTips.Item.TooltipBookPage).GetField("textIncompleteState",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Debug.Log($"[{ModMain.LogTag}] 书籍阅读状态反射缓存：" +
                $"itemKey={_fiItemKey != null}, itemData={_fiItemData != null}, " +
                $"layoutPage={_fiLayoutPage != null}, textIncompleteState={_fiTextIncompleteState != null}");
        }

        #endregion

        #region 第一层 Patch：TooltipBook.Refresh —— 触发后端查询

        /// <summary>
        /// TooltipBook.Refresh 的 postfix。当玩家悬停书籍时，此方法被调用。
        ///
        /// 【职责】检查是否需要查询 NPC 阅读状态，如需要则发起异步后端查询。
        /// 本 postfix 不负责显示标签——标签由第二层 TooltipBookPage.Refresh postfix 或查询回调补写。
        ///
        /// 【执行流程】
        ///   1. 读取设置开关 → 关闭则直接返回
        ///   2. 从 _itemData 取 OwnerCharId（物品所属角色）→ 主角则跳过（原版已显示）
        ///   3. 检查 ReadStateCache → 缓存命中则无需查询，直接返回
        ///   4. 检查 PendingQueries → 正在查询中则跳过，避免重复发起
        ///   5. 调用 QueryNpcBookReadState 发起异步查询
        /// </summary>
        [HarmonyPatch(typeof(Game.Views.MouseTips.Item.TooltipBook), nameof(Game.Views.MouseTips.Item.TooltipBook.Refresh))]
        [HarmonyPostfix]
        internal static void TooltipBook_Refresh_Postfix(Game.Views.MouseTips.Item.TooltipBook __instance)
        {
            if (!ModMain.GetSettingBool("BookReadStatus", true)) return;
            try
            {
                var itemDataObj = _fiItemData?.GetValue(__instance);
                var itemKeyObj = _fiItemKey?.GetValue(__instance);

                // OwnerCharId = 物品所属角色的 charId（不是查看者的 charId）
                // 注意：不用 _charId —— 原版从不给 TooltipBook._charId 赋值，恒为 0（死字段）
                int npcCharId = (itemDataObj is ItemDisplayData id) ? id.OwnerCharId : -1;

                if (itemKeyObj is not ItemKey bookKey) return;
                if (npcCharId <= 0) return;
                if (ModMain.IsTaiwu(npcCharId)) return;

                // 缓存命中：之前查过且数据还在，无需再次查询
                if (ModMain.ReadStateCache.TryGetValue(npcCharId, out var bookCache) &&
                    bookCache.ContainsKey(bookKey.Id))
                {
                    return;
                }

                // 去重（带超时自愈）：同一 (NPC, 书) 查询进行中则跳过，避免重复发起异步调用。
                // 先清理超过阈值的过期条目——实测某些书的 RPC 回调偶发不返回，
                // 若不清理，去重键会永久卡住，导致该书再也查不到、永远不显示标签。
                long queryKey = ModMain.MakeQueryKey(npcCharId, bookKey.Id);
                PurgeExpiredQueries();
                if (ModMain.PendingQueries.ContainsKey(queryKey))
                {
                    ModMain.LogDebug($"书籍阅读状态：查询进行中，跳过 npc={npcCharId} bookId={bookKey.Id}");
                    return;
                }
                ModMain.LogDebug($"书籍阅读状态：发起查询 npc={npcCharId} bookId={bookKey.Id} ({bookKey})");
                QueryNpcBookReadState(__instance, npcCharId, bookKey);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] TooltipBook Refresh postfix 异常: {ex.Message}");
            }
        }

        #endregion

        #region 第二层 Patch：TooltipBookPage.Refresh —— 追加 NPC 状态标签

        /// <summary>
        /// TooltipBookPage.Refresh 的 postfix。每页渲染完成后被调用（叶节点方法）。
        ///
        /// 【职责】在原版设置完 textIncompleteState（"完整/残缺"）之后，追加 NPC 阅读状态标签。
        /// 这是标签显示的主要入口——每次页面重渲染都会执行，确保标签不会被覆盖。
        ///
        /// 【为什么 patch 叶节点而非父类？】
        ///   - 原版渲染流程：TooltipBook.Refresh → OnGetPageInfo（异步取数据）→ TooltipBookPage.Refresh（逐页渲染）
        ///   - 如果 patch TooltipBook.Refresh 并在回调里写标签 → 页面重渲染时 OnGetPageInfo 会覆盖
        ///   - patch TooltipBookPage.Refresh（叶节点）确保每次重渲染都能重新追加，不被覆盖
        ///
        /// 【参数说明】
        ///   - index：页索引（0 起），对应 readState[index]，即该页在 readState 数组中的位置
        ///   - 其他 bool/sbyte 参数是原版的渲染控制参数，本 postfix 不使用
        ///
        /// 【时序问题】
        ///   首次悬停时，后端查询可能还没返回 → 缓存未命中 → 跳过。
        ///   等查询回调到达后，由 ApplyTagsToTooltip 直接遍历页面补写。
        ///   后续重渲染时缓存已命中，由本 postfix 正常追加。
        /// </summary>
        [HarmonyPatch(typeof(Game.Views.MouseTips.Item.TooltipBookPage), nameof(Game.Views.MouseTips.Item.TooltipBookPage.Refresh),
            new[] { typeof(bool), typeof(int), typeof(sbyte), typeof(sbyte), typeof(sbyte) })]
        [HarmonyPostfix]
        internal static void TooltipBookPage_Refresh_Postfix(Game.Views.MouseTips.Item.TooltipBookPage __instance, int index)
        {
            if (!ModMain.GetSettingBool("BookReadStatus", true)) return;
            try
            {
                // 沿父级链向上找 TooltipBook 组件（TooltipBookPage 是 TooltipBook 的子组件）
                var tooltipBook = FindParentTooltipBook(__instance.transform);
                if (tooltipBook == null) return;

                int npcCharId = ResolveNpcCharId(tooltipBook);
                var itemKeyObj = _fiItemKey?.GetValue(tooltipBook);
                if (itemKeyObj is not ItemKey bookKey) return;
                if (npcCharId <= 0) return;
                if (ModMain.IsTaiwu(npcCharId)) return;

                // 缓存未命中：后端查询还没回来，跳过。等回调到达后由 ApplyTagsToTooltip 补写。
                if (!ModMain.ReadStateCache.TryGetValue(npcCharId, out var bookCache) ||
                    !bookCache.TryGetValue(bookKey.Id, out var readState) || readState == null)
                {
                    return;
                }

                ApplyTagToPage(__instance, index, readState);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] TooltipBookPage Refresh postfix 异常: {ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 清理 PendingQueries 中超过超时阈值的条目。
        ///
        /// 实测某些书的后端 RPC 回调偶发不返回（疑似跨进程通道丢失），导致去重键永久卡住。
        /// 本方法在每次发起查询前调用，把超时未收到回调的键视为丢失并移除，
        /// 让下次悬停该书时能重新发起查询，实现自愈。
        /// </summary>
        private static void PurgeExpiredQueries()
        {
            if (ModMain.PendingQueries.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            // 由于遍历中删除，先收集过期键再移除，避免修改集合异常
            List<long>? expired = null;
            foreach (var kv in ModMain.PendingQueries)
            {
                if (now - kv.Value > ModMain.QueryTimeoutSeconds)
                {
                    expired ??= new List<long>();
                    expired.Add(kv.Key);
                }
            }
            if (expired != null)
            {
                foreach (long key in expired)
                {
                    ModMain.PendingQueries.Remove(key);
                }
                ModMain.LogDebug($"书籍阅读状态：清理 {expired.Count} 个超时未回调的查询（>{ModMain.QueryTimeoutSeconds}s）");
            }
        }

        /// <summary>
        /// 从 TooltipBook 实例解析当前查看的 NPC 角色 ID。
        ///
        /// 使用 _itemData.OwnerCharId（物品所属角色），而非 _charId 或 CharacterMenu.CurCharacterId：
        ///   - _charId：原版从不赋值，恒为 0（死字段）
        ///   - CharacterMenu.CurCharacterId：顶部角色滚动条选中者，与具体物品解耦，
        ///     在「查看不在队伍的人/列表第一项」时会错位取到列表第一个角色
        /// </summary>
        /// <returns>NPC 的 charId，解析失败返回 -1</returns>
        private static int ResolveNpcCharId(Component tooltipBook)
        {
            var itemDataObj = _fiItemData?.GetValue(tooltipBook);
            return (itemDataObj is ItemDisplayData id) ? id.OwnerCharId : -1;
        }

        /// <summary>
        /// 将单页的 NPC 阅读状态追加到 textIncompleteState 控件。
        ///
        /// 原版 textIncompleteState 显示"完整"或"残缺"，本方法在其后追加富文本标签：
        ///   - 已读：&lt;color=#88ccff&gt;此人已读&lt;/color&gt;（蓝色）
        ///   - 未读：&lt;color=#888888&gt;此人未读&lt;/color&gt;（灰色）
        ///
        /// 防重复检查：如果 text 已包含"此人"，说明之前已追加过，跳过。
        /// </summary>
        private static void ApplyTagToPage(Game.Views.MouseTips.Item.TooltipBookPage page, int index, bool[] readState)
        {
            if (page == null || index < 0 || index >= readState.Length) return;

            var textState = _fiTextIncompleteState?.GetValue(page) as TMP_Text;
            if (textState == null) return;

            string cur = textState.text;
            if (cur.Contains("此人")) return; // 已追加过，防重复

            textState.text = $"{cur} {NpcReadTag(readState[index])}";
        }

        /// <summary>
        /// 生成带颜色的 NPC 阅读状态富文本标签。
        /// 蓝色（#88ccff）= 已读，灰色（#888888）= 未读。
        /// </summary>
        private static string NpcReadTag(bool read) => read
            ? "<color=#88ccff>此人已读</color>"
            : "<color=#888888>此人未读</color>";

        /// <summary>
        /// 遍历 TooltipBook 的所有页，补写 NPC 阅读状态标签。
        ///
        /// 【调用时机】后端查询回调到达时（见 QueryNpcBookReadState 的 callback）。
        /// 此时游戏的 OnGetPageInfo 已渲染好页面，但那时缓存还没数据所以第二层 postfix 跳过了。
        /// 本方法直接遍历所有 TooltipBookPage 写入 NPC 状态，弥补首次悬停时的数据空窗期。
        ///
        /// 【与第二层 postfix 的关系】
        ///   - 本方法：首次悬停时后端回调到达后立即补写（一次性）
        ///   - 第二层 postfix：后续重渲染时正常追加（每次渲染都执行）
        ///   - 两者通过"已追加检查"（text.Contains("此人")）互斥，不会重复追加
        /// </summary>
        internal static void ApplyTagsToTooltip(Component tooltipBook, int npcCharId, int bookId)
        {
            try
            {
                if (!ModMain.ReadStateCache.TryGetValue(npcCharId, out var bookCache) ||
                    !bookCache.TryGetValue(bookId, out var readState) || readState == null)
                    return;

                var layoutPage = _fiLayoutPage?.GetValue(tooltipBook) as Transform;
                if (layoutPage == null) return;

                var pages = layoutPage.GetComponentsInChildren<Game.Views.MouseTips.Item.TooltipBookPage>(true);
                if (pages == null) return;

                // 不用 isActiveAndEnabled 过滤：首次悬停时本回调可能先于原版 OnGetPageInfo 激活页面，
                // 若跳过未激活页会导致「列表第一项不显示已读未读」。GetComponentsInChildren(true)
                // 已含未激活节点，直接写 textIncompleteState 无害（后续重新渲染会重新追加或保持）。
                for (int i = 0; i < pages.Length && i < readState.Length; i++)
                {
                    ApplyTagToPage(pages[i], i, readState);
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] ApplyTagsToTooltip 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 沿父级链向上查找 TooltipBook 组件。
        /// TooltipBookPage 是 TooltipBook 的子组件，需要向上查找才能获取父级的 _itemKey 和 _itemData。
        /// </summary>
        private static Game.Views.MouseTips.Item.TooltipBook? FindParentTooltipBook(Transform t)
        {
            while (t != null)
            {
                var book = t.GetComponent<Game.Views.MouseTips.Item.TooltipBook>();
                if (book != null) return book;
                t = t.parent;
            }
            return null;
        }

        #endregion

        #region 后端异步查询

        /// <summary>
        /// 向后端发起异步查询：获取指定 NPC 对指定书籍的阅读状态。
        ///
        /// 【前后端通信流程】
        ///   前端构造 SerializableModData 参数 → 调用 ModDomainMethod.AsyncCall →
        ///   后端 HandleGetNpcBookReadState 处理 → 返回 SerializableModData 结果 →
        ///   前端 callback 在主线程执行
        ///
        /// 【参数传递】
        ///   ItemKey 结构体（跨进程传输不安全）拆成 4 个 int 字段传递：
        ///   bookItemType(sbyte→int)、bookModState(byte→int)、bookTemplateId(short→int)、bookId(int)
        ///   后端接收后重建 ItemKey 再调用 DomainManager API。
        ///
        /// 【回调处理】
        ///   回调在主线程执行，可直接操作 UI。处理步骤：
        ///   1. 反序列化结果 → 提取 bool[] readState（每页是否已读）
        ///   2. 写入 ReadStateCache（后续重渲染时第二层 postfix 可直接读取）
        ///   3. 调用 ApplyTagsToTooltip 补写首次悬停时的数据空窗期
        ///   4. 从 PendingQueries 移除去重键（finally 块保证执行）
        /// </summary>
        /// <param name="tooltip">TooltipBook 实例，用作 IAsyncMethodRequestHandler（任何 UIBase 派生类都实现此接口）</param>
        /// <param name="npcCharId">NPC 的角色 ID（物品所属角色）</param>
        /// <param name="bookKey">书籍的 ItemKey</param>
        internal static void QueryNpcBookReadState(Component tooltip, int npcCharId, ItemKey bookKey)
        {
            long queryKey = ModMain.MakeQueryKey(npcCharId, bookKey.Id);
            // 记录发起时刻，供 PurgeExpiredQueries 判断是否超时
            ModMain.PendingQueries[queryKey] = Time.realtimeSinceStartup;

            // 构造参数：ItemKey 拆成 4 个 int 字段传递（跨进程安全）
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
                    if (resultCode < 0)
                    {
                        // resultCode < 0 通常表示后端调用失败/超时/方法未注册
                        Debug.Log($"[{ModMain.LogTag}] 书籍阅读状态回调失败：resultCode={resultCode} " +
                            $"npc={npcCharId} bookId={bookKey.Id}");
                        return;
                    }

                    SerializableModData? result = null;
                    SerializerHolder<SerializableModData>.Deserialize(resultPool, resultCode, ref result!);

                    if (result == null)
                    {
                        Debug.Log($"[{ModMain.LogTag}] 书籍阅读状态回调：反序列化结果为 null " +
                            $"npc={npcCharId} bookId={bookKey.Id}");
                        return;
                    }

                    if (!result.Get("success", out bool success) || !success)
                    {
                        result.Get("error", out string err);
                        Debug.Log($"[{ModMain.LogTag}] 书籍阅读状态回调：后端返回失败 " +
                            $"npc={npcCharId} bookId={bookKey.Id} error={err}");
                        return;
                    }

                    result.Get("pageCount", out int pageCount);
                    if (pageCount <= 0)
                    {
                        ModMain.LogDebug($"书籍阅读状态回调：pageCount=0 npc={npcCharId} bookId={bookKey.Id}");
                        return;
                    }

                    // 从结果中提取每页的阅读状态
                    var readState = new bool[pageCount];
                    for (int i = 0; i < pageCount; i++)
                        result.Get("p" + i.ToString(), out readState[i]);

                    // 写入缓存：后续重渲染时第二层 postfix 可直接读取
                    if (!ModMain.ReadStateCache.ContainsKey(npcCharId))
                        ModMain.ReadStateCache[npcCharId] = new Dictionary<int, bool[]>();
                    ModMain.ReadStateCache[npcCharId][bookKey.Id] = readState;

                    int readCount = 0;
                    for (int i = 0; i < readState.Length; i++) if (readState[i]) readCount++;
                    ModMain.LogDebug($"书籍阅读状态回调成功：npc={npcCharId} bookId={bookKey.Id} " +
                        $"pages={pageCount} 已读={readCount}");

                    // 补写首次悬停时的数据空窗期：
                    // 此时游戏的 OnGetPageInfo 已渲染好页面，但缓存还没数据所以第二层 postfix 跳过了。
                    // 直接遍历页面写入 NPC 状态，后续重渲染由第二层 postfix 接管。
                    if (tooltip != null && tooltip.gameObject != null)
                    {
                        ApplyTagsToTooltip(tooltip, npcCharId, bookKey.Id);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log($"[{ModMain.LogTag}] NPC 阅读状态回调异常: {ex.Message}");
                }
                finally
                {
                    ModMain.PendingQueries.Remove(queryKey);
                }
            };

            // 第一个参数 tooltip 需转为 IAsyncMethodRequestHandler，UIBase 派生类都实现了此接口
            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                (IAsyncMethodRequestHandler)tooltip,
                ModMain._modIdStr,
                "GetNpcBookReadState",
                param,
                callback
            );
        }

        #endregion
    }
}
