using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Config;
using GameData.Common;
using GameData.Domains;
using GameData.Domains.Building;
using GameData.Domains.Item;
using GameData.Domains.Map;
using GameData.Domains.Mod;
using GameData.Utilities;
using TaiwuModdingLib.Core.Plugin;

namespace AdjustModBackend
{
    /// <summary>
    /// 调整模块后端插件 - 提供 NPC 书籍阅读状态查询、心材已建造建筑数统计的 Mod 方法
    /// </summary>
    [PluginConfig("AdjustModBackend", "Magian", "1.0.0.0")]
    public class ModMain : TaiwuRemakePlugin
    {
        /// <summary>
        /// 借用记录在 SerializableModData 中的存储 key。
        /// 存档数据结构：key = "borrow_" + itemId，value = 借给的 npcCharId。
        /// 用 ModDomain.SetSerializableModData(isArchive=true) 随存档持久化，
        /// 卸载 MOD 不损坏存档（OnLoadWorld 非严格模式，缺数据返回空）。
        /// </summary>
        private const string BorrowDataKey = "BorrowRecords";

        /// <summary>
        /// 太吾生成的 Mod 标识字符串（"0_N" 格式）。在 Initialize 中缓存，供 static 方法使用。
        /// </summary>
        private static string _modIdStr = "";

        /// <summary>
        /// 插件初始化，注册 Mod 方法到 DomainManager
        /// </summary>
        public override void Initialize()
        {
            _modIdStr = ModIdStr;
            DomainManager.Mod.AddModMethod(
                ModIdStr,
                "GetNpcBookReadState",
                new Func<DataContext, SerializableModData, SerializableModData>(HandleGetNpcBookReadState)
            );
            DomainManager.Mod.AddModMethod(
                ModIdStr,
                "CountBuildingsRequiringMaterial",
                new Func<DataContext, SerializableModData, SerializableModData>(HandleCountBuildingsRequiringMaterial)
            );
            DomainManager.Mod.AddModMethod(
                ModIdStr,
                "BorrowTransferItem",
                new Func<DataContext, SerializableModData, SerializableModData>(HandleBorrowTransferItem)
            );
            DomainManager.Mod.AddModMethod(
                ModIdStr,
                "CheckBorrowedItem",
                new Func<DataContext, SerializableModData, SerializableModData>(HandleCheckBorrowedItem)
            );
            DomainManager.Mod.AddModMethod(
                ModIdStr,
                "ReturnBorrowedItem",
                new Func<DataContext, SerializableModData, SerializableModData>(HandleReturnBorrowedItem)
            );
            DomainManager.Mod.AddModMethod(
                ModIdStr,
                "GetAllBorrowedItemIds",
                new Func<DataContext, SerializableModData, SerializableModData>(HandleGetAllBorrowedItemIds)
            );
            // 过月结束时清理超 1 年的过期借用记录
            GameData.DomainEvents.Events.RegisterHandler_AdvanceMonthFinish(OnAdvanceMonthFinish);
        }

        /// <summary>
        /// 借用记录过期阈值（月）。超过此月数未取回的记录视为过期，过月时清除。
        /// 12 = 1 年。
        /// </summary>
        private const int BorrowExpiryMonths = 12;

        /// <summary>
        /// 过月结束回调。清理超过 1 年未取回的过期借用记录。
        /// </summary>
        private static void OnAdvanceMonthFinish(DataContext context)
        {
            try
            {
                PurgeExpiredBorrowRecords(context);
            }
            catch (Exception ex)
            {
                AdaptableLog.Warning("[AdjustModBackend] 过月清理借用记录异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 清理超过阈值月数的过期借用记录。
        /// 遍历所有 borrowdate_ 键，若当前日期 - 借出日期 > ExpiryMonths 则删除该 borrow_ 和 borrowdate_。
        /// </summary>
        private static void PurgeExpiredBorrowRecords(DataContext context)
        {
            var data = GetOrCreateBorrowData();
            int currDate = DomainManager.World.GetCurrDate();
            int cutoff = currDate - BorrowExpiryMonths;

            // 反射读取所有 int 键，区分 borrow_ 和 borrowdate_
            var intValuesField = typeof(SerializableModData).GetField("_intValues",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (!(intValuesField?.GetValue(data) is IDictionary intDict) || intDict.Count == 0) return;

            var borrowIds = new List<int>();       // borrow_<id> 的 itemId
            var dateMap = new Dictionary<int, int>(); // borrowdate_<id> → 日期

            foreach (DictionaryEntry entry in intDict)
            {
                string? key = entry.Key as string;
                if (key == null) continue;
                if (key.StartsWith("borrow_"))
                {
                    if (int.TryParse(key.Substring(7), out int itemId))
                        borrowIds.Add(itemId);
                }
                else if (key.StartsWith("borrowdate_"))
                {
                    if (int.TryParse(key.Substring(12), out int itemId))
                        dateMap[itemId] = (int)(entry.Value ?? 0);
                }
            }

            // 兼容旧存档：有 borrow_ 但无 borrowdate_ 的记录，补当前日期（给 1 年缓冲）
            var migrated = new List<int>();
            foreach (int itemId in borrowIds)
            {
                if (!dateMap.ContainsKey(itemId))
                {
                    data.Set("borrowdate_" + itemId, currDate);
                    dateMap[itemId] = currDate;
                    migrated.Add(itemId);
                }
            }

            // 清理超期记录
            var expiredIds = new List<int>();
            foreach (var kv in dateMap)
            {
                if (kv.Value < cutoff)
                    expiredIds.Add(kv.Key);
            }

            if (migrated.Count == 0 && expiredIds.Count == 0) return;

            foreach (int itemId in expiredIds)
            {
                data.RemoveInt("borrow_" + itemId);
                data.RemoveInt("borrowdate_" + itemId);
            }
            DomainManager.Mod.SetSerializableModData(context, _modIdStr, BorrowDataKey, true, data);

            if (migrated.Count > 0)
                AdaptableLog.Info("[AdjustModBackend] 过月补全 " + migrated.Count + " 条旧借用记录的日期");
            if (expiredIds.Count > 0)
                AdaptableLog.Info("[AdjustModBackend] 过月清理 " + expiredIds.Count + " 条过期借用记录");
        }

        /// <summary>
        /// 处理"获取 NPC 书籍阅读状态"的 Mod 方法调用
        /// </summary>
        /// <param name="context">数据上下文</param>
        /// <param name="param">参数：npcCharId(int), bookItemType(int), bookModState(int), bookTemplateId(int), bookId(int)</param>
        /// <returns>返回：success(bool), pageCount(int), p0-pN(bool) 每页是否已读</returns>
        private static SerializableModData HandleGetNpcBookReadState(DataContext context, SerializableModData param)
        {
            var result = new SerializableModData();

            // 解析参数
            if (!param.Get("npcCharId", out int npcCharId))
                return Fail(result, "Missing npcCharId");
            if (!param.Get("bookItemType", out int itemType))
                return Fail(result, "Missing bookItemType");
            if (!param.Get("bookModState", out int modState))
                return Fail(result, "Missing bookModState");
            if (!param.Get("bookTemplateId", out int templateId))
                return Fail(result, "Missing bookTemplateId");
            if (!param.Get("bookId", out int id))
                return Fail(result, "Missing bookId");

            // 重建 ItemKey
            var bookKey = new ItemKey(
                (sbyte)itemType,
                (byte)modState,
                (short)templateId,
                id
            );

            try
            {
                // 调用后端 API 获取 NPC 对指定书籍的阅读状态
                bool[] readState = DomainManager.Character.GetCharBookReadState(npcCharId, bookKey);

                if (readState == null || readState.Length == 0)
                {
                    result.Set("success", true);
                    result.Set("pageCount", 0);
                    return result;
                }

                result.Set("success", true);
                result.Set("pageCount", readState.Length);
                for (int i = 0; i < readState.Length; i++)
                {
                    result.Set("p" + i.ToString(), readState[i]);
                }
            }
            catch (Exception ex)
            {
                result.Set("success", false);
                result.Set("error", ex.Message);
            }

            return result;
        }

        /// <summary>
        /// 处理"统计依赖指定心材的已建造建筑数量"的 Mod 方法调用
        /// </summary>
        /// <param name="context">数据上下文</param>
        /// <param name="param">参数：materialTemplateId(int) 心材的模板 ID</param>
        /// <returns>返回：success(bool), count(int) 太吾村中需要该心材的已建造建筑数</returns>
        private static SerializableModData HandleCountBuildingsRequiringMaterial(DataContext context, SerializableModData param)
        {
            var result = new SerializableModData();

            if (!param.Get("materialTemplateId", out int materialTemplateId))
                return Fail(result, "Missing materialTemplateId");

            try
            {
                Location taiwuLocation = DomainManager.Taiwu.GetTaiwuVillageLocation();
                List<BuildingBlockData> blocks = DomainManager.Building.GetBuildingBlockList(taiwuLocation);

                int count = 0;
                foreach (var block in blocks)
                {
                    if (block.TemplateId <= 0) continue;
                    var config = BuildingBlock.Instance[block.TemplateId];
                    if (config != null && config.BuildingCoreItem == (short)materialTemplateId)
                    {
                        count++;
                    }
                }

                result.Set("success", true);
                result.Set("count", count);
            }
            catch (Exception ex)
            {
                result.Set("success", false);
                result.Set("error", ex.Message);
            }

            return result;
        }

        /// <summary>
        /// 返回失败结果
        /// </summary>
        private static SerializableModData Fail(SerializableModData result, string reason)
        {
            result.Set("success", false);
            result.Set("error", reason);
            return result;
        }

        #region 物品借用

        /// <summary>
        /// 处理"借用物品"的 Mod 方法调用。
        ///
        /// 【流程】太吾把物品借给队友：
        ///   1. 用前端传来的完整 ItemKey 调用 TransferInventoryItemFromAToB 纯转移
        ///      （不碰好感度/心情/警觉度）
        ///   2. 用 SerializableModData 记录 itemId → npcCharId 的借用关系
        ///
        /// 【为什么用 TransferInventoryItemFromAToB 而非 TransferInventoryItemWithDebt】
        /// 后者会触发 UpdateDebtByItemTransfer（好感度+心情）和 ChangeAlertnessOnGiveTeammateItem（警觉度）。
        /// 借用是临时借用，不应有任何社交后果，所以用纯转移 API。
        ///
        /// 【参数】前端传完整 ItemKey（4 个字段），避免后端遍历背包。
        /// </summary>
        private static SerializableModData HandleBorrowTransferItem(DataContext context, SerializableModData param)
        {
            var result = new SerializableModData();

            if (!param.Get("taiwuCharId", out int taiwuCharId))
                return Fail(result, "Missing taiwuCharId");
            if (!param.Get("npcCharId", out int npcCharId))
                return Fail(result, "Missing npcCharId");
            if (!ParseItemKey(param, out ItemKey itemKey))
                return Fail(result, "Missing/invalid itemKey");

            try
            {
                // 纯物品转移：无好感度、无心情、无警觉度变化
                DomainManager.Character.TransferInventoryItemFromAToB(context, taiwuCharId, npcCharId, itemKey, 1);

                // 记录借用关系（随存档持久化）
                SetBorrowRecord(context, itemKey.Id, npcCharId);

                AdaptableLog.Info("[AdjustModBackend] 借出物品 itemId=" + itemKey.Id + " 给 npcCharId=" + npcCharId);
                result.Set("success", true);
            }
            catch (Exception ex)
            {
                AdaptableLog.Warning("[AdjustModBackend] 借用物品异常: " + ex.Message);
                result.Set("success", false);
                result.Set("error", ex.Message);
            }

            return result;
        }

        /// <summary>
        /// 处理"查询物品是否被借出"的 Mod 方法调用。
        ///
        /// 用于：玩家从队友处拿取物品前，先查该物品是否是借出的。
        /// 命中 → 走借用取回流程（无好感度扣减）；未命中 → 走原版拿取（正常扣好感）。
        /// </summary>
        private static SerializableModData HandleCheckBorrowedItem(DataContext context, SerializableModData param)
        {
            var result = new SerializableModData();

            if (!param.Get("itemId", out int itemId))
                return Fail(result, "Missing itemId");

            try
            {
                bool isBorrowed = TryGetBorrowRecord(out int _, itemId);
                result.Set("success", true);
                result.Set("isBorrowed", isBorrowed);
            }
            catch (Exception ex)
            {
                result.Set("success", false);
                result.Set("error", ex.Message);
            }

            return result;
        }

        /// <summary>
        /// 处理"取回借出物品"的 Mod 方法调用。
        ///
        /// 【流程】把借给队友的物品拿回太吾：
        ///   1. 查借用记录，确认是借出的物品
        ///   2. 用前端传来的完整 ItemKey 调用 TransferInventoryItemFromAToB 纯转移
        ///      （不碰好感度/警觉度）
        ///   3. 删除借用记录
        /// </summary>
        private static SerializableModData HandleReturnBorrowedItem(DataContext context, SerializableModData param)
        {
            var result = new SerializableModData();

            if (!param.Get("taiwuCharId", out int taiwuCharId))
                return Fail(result, "Missing taiwuCharId");
            if (!param.Get("npcCharId", out int npcCharId))
                return Fail(result, "Missing npcCharId");
            if (!ParseItemKey(param, out ItemKey itemKey))
                return Fail(result, "Missing/invalid itemKey");

            try
            {
                // 确认是借出的物品
                if (!TryGetBorrowRecord(out int borrowedToCharId, itemKey.Id))
                {
                    result.Set("success", true);
                    result.Set("isBorrowed", false);
                    return result;
                }

                // 纯转移取回：无好感度、无警觉度变化
                DomainManager.Character.TransferInventoryItemFromAToB(context, npcCharId, taiwuCharId, itemKey, 1);

                // 删除借用记录
                RemoveBorrowRecord(context, itemKey.Id);

                AdaptableLog.Info("[AdjustModBackend] 取回借出物品 itemId=" + itemKey.Id + "（原借给 npcCharId=" + borrowedToCharId + "）");
                result.Set("success", true);
                result.Set("isBorrowed", true);
            }
            catch (Exception ex)
            {
                AdaptableLog.Warning("[AdjustModBackend] 取回借出物品异常: " + ex.Message);
                result.Set("success", false);
                result.Set("error", ex.Message);
            }

            return result;
        }

        /// <summary>
        /// 处理"获取所有借出物品 Id"的 Mod 方法调用。
        ///
        /// 用于：前端读档/换档后同步 BorrowedItems 缓存。
        /// 返回所有借出记录的 itemId 列表，前端据此重建缓存。
        /// </summary>
        /// <returns>success(bool), count(int), id0..idN(int)</returns>
        private static SerializableModData HandleGetAllBorrowedItemIds(DataContext context, SerializableModData param)
        {
            var result = new SerializableModData();
            try
            {
                var ids = GetAllBorrowRecordIds();
                result.Set("success", true);
                result.Set("count", ids.Count);
                for (int i = 0; i < ids.Count; i++)
                    result.Set("id" + i.ToString(), ids[i]);
            }
            catch (Exception ex)
            {
                result.Set("success", false);
                result.Set("error", ex.Message);
            }
            return result;
        }

        /// <summary>获取所有借出记录的 itemId 列表</summary>
        private static List<int> GetAllBorrowRecordIds()
        {
            var ids = new List<int>();
            if (!DomainManager.Mod.TryGet(_modIdStr, BorrowDataKey, true, out SerializableModData data))
                return ids;
            // SerializableModData 没有公开的 key 枚举，借用记录的 key 格式是 "borrow_<itemId>"
            // 通过反射读取 _intValues 字典（借用记录存的都是 int 类型的 npcCharId）
            var intValuesField = typeof(SerializableModData).GetField("_intValues",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (intValuesField?.GetValue(data) is System.Collections.IDictionary intDict)
            {
                foreach (DictionaryEntry entry in intDict)
                {
                    string? key = entry.Key as string;
                    if (key != null && key.StartsWith("borrow_"))
                    {
                        if (int.TryParse(key.Substring(7), out int itemId))
                            ids.Add(itemId);
                    }
                }
            }
            return ids;
        }

        /// <summary>
        /// 从 Mod 方法参数中解析完整 ItemKey（前端传 4 个 int 字段，跨进程安全）。
        /// </summary>
        private static bool ParseItemKey(SerializableModData param, out ItemKey itemKey)
        {
            itemKey = default;
            if (!param.Get("itemType", out int itemType)) return false;
            if (!param.Get("itemModState", out int modState)) return false;
            if (!param.Get("itemTemplateId", out int templateId)) return false;
            if (!param.Get("itemId", out int itemId)) return false;
            itemKey = new ItemKey((sbyte)itemType, (byte)modState, (short)templateId, itemId);
            return true;
        }

        /// <summary>
        /// 记录借用关系到存档数据。
        /// 存两条：borrow_&lt;id&gt; = npcCharId，borrowdate_&lt;id&gt; = 借出日期（当前月份计数）。
        /// 日期用于过月时清理超 1 年的过期记录。
        /// </summary>
        private static void SetBorrowRecord(DataContext context, int itemId, int npcCharId)
        {
            var data = GetOrCreateBorrowData();
            data.Set("borrow_" + itemId, npcCharId);
            data.Set("borrowdate_" + itemId, DomainManager.World.GetCurrDate());
            DomainManager.Mod.SetSerializableModData(context, _modIdStr, BorrowDataKey, true, data);
        }

        /// <summary>
        /// 查询 itemId 是否被借出。命中返回 true 并输出借给的 npcCharId。
        /// </summary>
        private static bool TryGetBorrowRecord(out int npcCharId, int itemId)
        {
            npcCharId = -1;
            if (!DomainManager.Mod.TryGet(_modIdStr, BorrowDataKey, true, out SerializableModData data))
                return false;
            return data.Get("borrow_" + itemId, out npcCharId);
        }

        /// <summary>
        /// 删除 itemId 的借用记录（含日期）。
        /// </summary>
        private static void RemoveBorrowRecord(DataContext context, int itemId)
        {
            var data = GetOrCreateBorrowData();
            data.RemoveInt("borrow_" + itemId);
            data.RemoveInt("borrowdate_" + itemId);
            DomainManager.Mod.SetSerializableModData(context, _modIdStr, BorrowDataKey, true, data);
        }

        /// <summary>
        /// 获取当前借用记录数据。读取已有记录；不存在则返回空容器（首次借用时）。
        /// </summary>
        private static SerializableModData GetOrCreateBorrowData()
        {
            if (DomainManager.Mod.TryGet(_modIdStr, BorrowDataKey, true, out SerializableModData data))
                return data;
            return new SerializableModData();
        }

        #endregion

        /// <summary>
        /// 插件清理
        /// </summary>
        public override void Dispose()
        {
            // 无需额外清理
        }
    }
}
