using System;
using System.Collections.Generic;
using System.Reflection;
using GameData.Domains.Item;
using GameData.Domains.Item.Display;
using GameData.Domains.Mod;
using GameData.Serializer;
using GameData.Utilities;
using HarmonyLib;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 物品转借/借还补丁 —— 在物品操作菜单新增独立选项，实现无好感度/警觉度的物品借用。
    ///
    /// 【两个独立按钮】
    ///   1. 太吾物品菜单的「转赠」下方新增「转借」——把太吾的书籍/装备借给队友（不增加好感/警觉）
    ///   2. 队友物品菜单的「拿取」下方新增「借还」——把借出去的物品拿回（不减少好感/警觉）
    ///      借还按钮只有当该物品确实是借出物时才可点（亮色），否则灰色不可点。
    ///
    /// 【为什么不复用原版的转赠/拿取】
    /// 原版转赠/拿取走 TransferInventoryItemWithDebt，会触发好感度+心情+警觉度变化。
    /// 转借/借还走后端纯转移 API（TransferInventoryItemFromAToB），完全不碰社交数值。
    /// 独立按钮避免误导玩家（拿取按钮悬停时仍显示好感度降低提示，但那是原版逻辑，与借还无关）。
    ///
    /// 【借用记录持久化】
    /// 后端用 ModDomain.SerializableModData(isArchive=true) 存储 itemId→npcCharId，随存档保存。
    /// 前端维护 BorrowedItems 缓存（HashSet&lt;int&gt;）决定「借还」按钮可否点击。
    /// 读档时通过 GetAllBorrowedItemIds 同步缓存。
    /// 卸载 MOD 不损坏存档（OnLoadWorld 非严格模式，缺数据返回空）。
    /// </summary>
    [HarmonyPatch]
    internal static class BorrowItemPatch
    {
        #region 转借状态

        /// <summary>
        /// 正在转借标志。由「转借」按钮设置，由 TransferItem prefix 消费后清零。
        /// </summary>
        private static bool _transferring = false;

        /// <summary>
        /// 转借用的 ItemDisplayData（在选队友 UI 之前保存，TransferItem prefix 时使用）。
        /// </summary>
        private static ItemDisplayData? _pendingLendItem;

        #endregion

        #region 第一层 Patch：ShowItemOperateMenuGive —— 追加「转借」按钮

        /// <summary>
        /// ViewCharacterMenuItems.ShowItemOperateMenuGive 的 postfix。
        /// 在原版「转赠」按钮之后，往 btnList 追加「转借」按钮。
        ///
        /// 【显示条件】设置开关开启 + 物品是书籍/装备 + 有队友
        /// 【点击行为】设置转借标志，然后复用原版 EnterTransferMode 的选队友 UI。
        /// 选中队友后原版调 TransferItem，prefix 检测到转借标志改走纯转移。
        /// </summary>
        [HarmonyPatch(typeof(Game.Views.CharacterMenu.ViewCharacterMenuItems), "ShowItemOperateMenuGive")]
        [HarmonyPostfix]
        internal static void ShowItemOperateMenuGive_Postfix(
            Game.Views.CharacterMenu.ViewCharacterMenuItems __instance,
            List<Game.Views.ViewPopupMenu.BtnData> btnList,
            ItemDisplayData itemData)
        {
            if (!ModMain.GetSettingBool("BorrowItem", true)) return;
            try
            {
                if (!IsBorrowableItemType(itemData.Key.ItemType)) return;

                bool hasTeammate = SingletonObject.getInstance<CharacterMonitorModel>().GetTaiwuTeamCharIds().Count > 1
                    || SingletonObject.getInstance<CharacterMonitorModel>().GetTaiwuSpecialGroup().Count > 0;
                if (!hasTeammate) return;

                var btnData = new Game.Views.ViewPopupMenu.BtnData(
                    "转借", hasTeammate, Game.Views.CharacterMenu.EItemMenuDisplayOrder.Give,
                    () => OnClickLend(__instance, itemData),
                    null, null, false);
                btnList.Add(btnData);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 转借按钮追加异常: {ex.Message}");
            }
        }

        #endregion

        #region 第二层 Patch：ShowItemOperateMenuTake —— 追加「借还」按钮

        /// <summary>
        /// ViewCharacterMenuItems.ShowItemOperateMenuTake 的 postfix。
        /// 在原版「拿取」按钮之后追加「借还」按钮。
        ///
        /// 【可点条件】该物品在 BorrowedItems 缓存中（即确实是借出去的物品）。
        /// 非借出物 → 灰色不可点，并显示提示「此项非借出物」。
        /// </summary>
        [HarmonyPatch(typeof(Game.Views.CharacterMenu.ViewCharacterMenuItems), "ShowItemOperateMenuTake")]
        [HarmonyPostfix]
        internal static void ShowItemOperateMenuTake_Postfix(
            Game.Views.CharacterMenu.ViewCharacterMenuItems __instance,
            List<Game.Views.ViewPopupMenu.BtnData> btnList,
            ItemDisplayData itemData)
        {
            if (!ModMain.GetSettingBool("BorrowItem", true)) return;
            try
            {
                // 读档后首次打开物品菜单时，从后端同步借出记录缓存
                SyncBorrowedItemsFromBackend(__instance as IAsyncMethodRequestHandler);

                if (!IsBorrowableItemType(itemData.Key.ItemType)) return;

                bool isBorrowed = ModMain.BorrowedItems.Contains(itemData.Key.Id);
                var btnData = new Game.Views.ViewPopupMenu.BtnData(
                    "借还", isBorrowed, Game.Views.CharacterMenu.EItemMenuDisplayOrder.Take,
                    () => OnClickReturn(__instance, itemData),
                    null, null, false);
                // 灰色时给个提示
                if (!isBorrowed)
                    btnData.SetTip("", "此项非借出物，无法借还");
                btnList.Add(btnData);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 借还按钮追加异常: {ex.Message}");
            }
        }

        #endregion

        #region 第三层 Patch：TransferItem —— 转借拦截

        /// <summary>
        /// ViewCharacterMenuItems.TransferItem 的 prefix。
        /// 仅当转借标志为 true 时拦截，改走纯转移；否则放行原版（含拿取、正常转赠）。
        /// </summary>
        [HarmonyPatch(typeof(Game.Views.CharacterMenu.ViewCharacterMenuItems), "TransferItem")]
        [HarmonyPrefix]
        internal static bool TransferItem_Prefix(
            Game.Views.CharacterMenu.ViewCharacterMenuItems __instance,
            ItemDisplayData itemData,
            int characterId)
        {
            if (!ModMain.GetSettingBool("BorrowItem", true)) return true;
            if (!_transferring) return true; // 非转借，放行原版

            try
            {
                _transferring = false;
                DoLend(__instance, itemData, characterId);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] TransferItem prefix 转借异常: {ex.Message}");
                _transferring = false;
            }
            return false; // 拦截，不走原版
        }

        #endregion

        #region 转借流程（太吾 → 队友）

        /// <summary>点击「转借」：设置标志，复用原版选队友 UI</summary>
        private static void OnClickLend(Game.Views.CharacterMenu.ViewCharacterMenuItems instance, ItemDisplayData itemData)
        {
            try
            {
                _transferring = true;
                _pendingLendItem = itemData;
                _miEnterTransferMode?.Invoke(instance, new object[] { itemData });
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] OnClickLend 异常: {ex.Message}");
                _transferring = false;
            }
        }

        /// <summary>执行转借：调后端 BorrowTransferItem（纯转移 + 记录）</summary>
        private static void DoLend(Game.Views.CharacterMenu.ViewCharacterMenuItems instance,
            ItemDisplayData itemData, int targetCharId)
        {
            int taiwuCharId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId;
            ModMain.LogDebug($"转借：太吾({taiwuCharId}) → 队友({targetCharId}) 物品={itemData.Key}");

            var param = new SerializableModData();
            param.Set("taiwuCharId", taiwuCharId);
            param.Set("npcCharId", targetCharId);
            param.Set("itemType", (int)itemData.Key.ItemType);
            param.Set("itemModState", (int)itemData.Key.ModificationState);
            param.Set("itemTemplateId", (int)itemData.Key.TemplateId);
            param.Set("itemId", itemData.Key.Id);

            CallBackend(instance, "BorrowTransferItem", param, itemData.Key.Id, isLend: true);
        }

        #endregion

        #region 借还流程（队友 → 太吾）

        /// <summary>点击「借还」：直接调后端 ReturnBorrowedItem（纯转移 + 删记录）</summary>
        private static void OnClickReturn(Game.Views.CharacterMenu.ViewCharacterMenuItems instance, ItemDisplayData itemData)
        {
            try
            {
                int taiwuCharId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId;
                int srcCharId = instance.CharacterMenu.CurCharacterId;
                ModMain.LogDebug($"借还：队友({srcCharId}) → 太吾({taiwuCharId}) 物品={itemData.Key}");

                var param = new SerializableModData();
                param.Set("taiwuCharId", taiwuCharId);
                param.Set("npcCharId", srcCharId);
                param.Set("itemType", (int)itemData.Key.ItemType);
                param.Set("itemModState", (int)itemData.Key.ModificationState);
                param.Set("itemTemplateId", (int)itemData.Key.TemplateId);
                param.Set("itemId", itemData.Key.Id);

                CallBackend(instance, "ReturnBorrowedItem", param, itemData.Key.Id, isLend: false);
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] OnClickReturn 异常: {ex.Message}");
            }
        }

        #endregion

        #region 后端调用公共方法

        /// <summary>
        /// 调用后端 Mod 方法（转借或借还），成功后更新前端缓存 + 刷新 UI。
        /// </summary>
        private static void CallBackend(
            Game.Views.CharacterMenu.ViewCharacterMenuItems instance,
            string methodName, SerializableModData param,
            int itemId, bool isLend)
        {
            AsyncMethodCallbackDelegate callback = (int resultCode, RawDataPool resultPool) =>
            {
                try
                {
                    if (resultCode < 0)
                    {
                        Debug.Log($"[{ModMain.LogTag}] {methodName} 失败：resultCode={resultCode}");
                        return;
                    }
                    SerializableModData? result = null;
                    SerializerHolder<SerializableModData>.Deserialize(resultPool, resultCode, ref result!);
                    if (result != null && result.Get("success", out bool success) && success)
                    {
                        if (isLend)
                        {
                            ModMain.BorrowedItems.Add(itemId);
                            ModMain.LogDebug($"转借成功：itemId={itemId}");
                        }
                        else
                        {
                            // 借还：后端返回 isBorrowed 表示是否确实是借出物
                            result.Get("isBorrowed", out bool isBorrowed);
                            if (isBorrowed)
                            {
                                ModMain.BorrowedItems.Remove(itemId);
                                ModMain.LogDebug($"借还成功：itemId={itemId}");
                            }
                            else
                            {
                                Debug.Log($"[{ModMain.LogTag}] 借还：后端判定非借出物，清除前端缓存");
                                ModMain.BorrowedItems.Remove(itemId);
                            }
                        }
                    }
                    else
                    {
                        string err = "unknown";
                        result?.Get("error", out err);
                        Debug.Log($"[{ModMain.LogTag}] {methodName} 后端返回失败: {err}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log($"[{ModMain.LogTag}] {methodName} 回调异常: {ex.Message}");
                }
                finally
                {
                    RefreshAfterItemOp(instance);
                }
            };

            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                instance as IAsyncMethodRequestHandler,
                ModMain._modIdStr,
                methodName,
                param,
                callback);
        }

        #endregion

        #region 读档后同步缓存

        /// <summary>缓存是否已从后端同步（读档后为 false，首次打开物品菜单时触发同步）</summary>
        private static bool _cacheSynced = false;

        /// <summary>
        /// 标记缓存需要重新同步（读档/换档时调用）。
        /// 实际同步在 ShowItemOperateMenuTake postfix 中触发（那时才有可用的 handler）。
        /// </summary>
        internal static void MarkCacheStale()
        {
            _cacheSynced = false;
            ModMain.BorrowedItems.Clear();
        }

        /// <summary>
        /// 从后端同步 BorrowedItems 缓存。需要传入一个 UIBase 作为 handler。
        /// 在 ShowItemOperateMenuTake postfix（有 __instance）中触发。
        /// </summary>
        private static void SyncBorrowedItemsFromBackend(IAsyncMethodRequestHandler handler)
        {
            if (_cacheSynced) return;
            _cacheSynced = true; // 先标记，避免重入

            ModMain.LogDebug("从后端同步借出记录缓存...");
            var param = new SerializableModData();
            AsyncMethodCallbackDelegate callback = (int resultCode, RawDataPool resultPool) =>
            {
                try
                {
                    if (resultCode < 0) return;
                    SerializableModData? result = null;
                    SerializerHolder<SerializableModData>.Deserialize(resultPool, resultCode, ref result!);
                    if (result != null && result.Get("success", out bool success) && success)
                    {
                        result.Get("count", out int count);
                        for (int i = 0; i < count; i++)
                        {
                            if (result.Get("id" + i.ToString(), out int id))
                                ModMain.BorrowedItems.Add(id);
                        }
                        ModMain.LogDebug($"同步借出记录完成：{ModMain.BorrowedItems.Count} 条");
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log($"[{ModMain.LogTag}] 同步借出记录回调异常: {ex.Message}");
                }
            };

            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                handler,
                ModMain._modIdStr,
                "GetAllBorrowedItemIds",
                param,
                callback);
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 判断物品类型是否支持转借/借还（书籍或装备）。
        /// 装备：Weapon/Armor/Accessory/Clothing/Carrier（ItemType 0-4）
        /// 书籍：SkillBook
        /// </summary>
        private static bool IsBorrowableItemType(sbyte itemType)
        {
            return ItemType.IsEquipmentItemType(itemType) || itemType == ItemType.SkillBook;
        }

        /// <summary>转移操作完成后刷新物品列表 UI</summary>
        private static void RefreshAfterItemOp(Game.Views.CharacterMenu.ViewCharacterMenuItems instance)
        {
            try
            {
                _miCallRefreshItems?.Invoke(instance, new object?[] { null });
                instance.CharacterMenu.RerenderCharacterScroll();
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 刷新 UI 异常: {ex.Message}");
            }
        }

        #endregion

        #region 反射缓存

        private static MethodInfo? _miEnterTransferMode;
        private static MethodInfo? _miCallRefreshItems;

        internal static void Init()
        {
            var itemType = typeof(Game.Views.CharacterMenu.ViewCharacterMenuItems);
            _miEnterTransferMode = AccessTools.Method(itemType, "EnterTransferMode");
            _miCallRefreshItems = AccessTools.Method(itemType, "CallRefreshItems");

            Debug.Log($"[{ModMain.LogTag}] 转借反射缓存：" +
                $"enterTransfer={_miEnterTransferMode != null}, callRefresh={_miCallRefreshItems != null}");
        }

        #endregion
    }
}
