using System;
using System.Collections.Generic;
using System.Reflection;
using Config;
using Game.Views.MouseTips.Item.Common;
using GameData.Domains.Item;
using GameData.Domains.Mod;
using GameData.Serializer;
using GameData.Utilities;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace AdjustMod
{
    /// <summary>
    /// 心材已建数提示 —— 悬停心材类物品时，在浮窗属性网格（品阶/价值/类型/重量/子类 那个 GridLayoutGroup）
    /// 末尾新增一个"已建"格子，显示太吾村中需要该心材的已建造建筑数（含 0 也显示）。
    ///
    /// 【显示位置】子类(心材)格子的右边 / 重量格子换行后的下方（GridLayoutGroup 自动排列）。
    ///
    /// 【不动原版"持有"】原版 TooltipItemBase.RefreshHoldCount 已在浮窗顶部显示"持有：X"，
    /// 本补丁只额外加"已建"，两者互不干扰。
    ///
    /// 【为什么只对有建筑依赖的心材显示】BuildingBlock 配置表里只有部分心材被某些建筑
    /// 用作核心材料（BuildingCoreItem）。对没被任何建筑依赖的心材显示"已建造: 0"无意义，
        /// 故预先构建「心材 TemplateId → 有建筑依赖」集合，只对这些心材触发显示。
    ///
    /// 【UI 结构】tooltip 层级：OtherArea → MainPanel → TooltipMisc(挂 TooltipItemBase 组件)。
    /// OtherArea 往上两层 parent 即 TooltipMisc 根节点，在其上 GetComponent&lt;TooltipItemBase&gt;。
    ///
    /// 【每次实时查询，不缓存】建筑数在游戏过程中会变（新建/拆除），缓存必然过期。
    /// 心材浮窗不是高频操作，每次悬停实时查后端，跟原版 RefreshHoldCount 做法一致。
    /// </summary>
    [HarmonyPatch]
    internal static class ItemMaterialHintPatch
    {
        #region 反射缓存

        private static FieldInfo? _fiItemKey;
        private static FieldInfo? _fiCommonArea;       // TooltipItemBase.commonArea
        private static FieldInfo? _fiTextWeight;       // TooltipItemCommonArea.textWeight（重量格的 label，用于定位 GridLayoutGroup）

        /// <summary>有建筑依赖的心材 TemplateKey 集合（静态配置，仅构建一次）。
        /// 用 TemplateKey(ItemType+TemplateId) 而非单纯 TemplateId，避免不同物品类型
        /// （如护具 ItemType=1、宝物 ItemType=2）与心材 ItemType=12 的 TemplateId 数值撞车误判。</summary>
        private static HashSet<TemplateKey>? _materialsWithBuildings;

        internal static void Init()
        {
            try
            {
                _fiItemKey = AccessTools.Field(typeof(TooltipItemBase), "_itemKey");
                _fiCommonArea = AccessTools.Field(typeof(TooltipItemBase), "commonArea");
                _fiTextWeight = AccessTools.Field(typeof(TooltipItemCommonArea), "textWeight");
                ModMain.LogDebug($"心材浮窗反射缓存: _itemKey={_fiItemKey != null}, commonArea={_fiCommonArea != null}, textWeight={_fiTextWeight != null}");
                BuildMaterialWithBuildingsSet();
            }
            catch (Exception ex)
            {
                Debug.Log($"[{ModMain.LogTag}] 心材浮窗 Init 异常: {ex}");
            }
        }

        /// <summary>
        /// 遍历所有建筑配置，收集「被某建筑用作核心材料」的心材 TemplateId 集合。
        /// </summary>
        private static void BuildMaterialWithBuildingsSet()
        {
            _materialsWithBuildings = new HashSet<TemplateKey>();
            var buildingConfigs = BuildingBlock.Instance;
            int count = buildingConfigs.Count;
            for (int i = 0; i < count; i++)
            {
                var config = buildingConfigs[i];
                if (config == null) continue;
                if (config.BuildingCoreItem <= 0) continue;
                // BuildingCoreItem 只存 TemplateId，心材实际属于杂物类 Misc(ItemType=12)
                _materialsWithBuildings.Add(new TemplateKey(ItemType.Misc, config.BuildingCoreItem));
            }

            ModMain.LogDebug($"心材→建筑映射构建完成，共 {_materialsWithBuildings.Count} 种心材有建筑依赖");
        }

        /// <summary>判断给定 ItemKey 是否为有建筑依赖的心材</summary>
        /// <remarks>
        /// 心材属于杂物类物品（MiscItem，ItemType=12），不能用 ItemType 判定。
        /// 这里只靠 _materialsWithBuildings 集合判定——它收集自 BuildingBlock 配置的
        /// BuildingCoreItem 字段（建筑核心材料 TemplateId），天然只含心材 templateId。
        /// </remarks>
        private static bool IsMaterialWithBuildings(ItemKey key, out short templateId)
        {
            templateId = key.TemplateId;
            // 必须用 TemplateKey（ItemType+TemplateId）匹配：不同物品类型的 TemplateId 独立编号，
            // 只比 TemplateId 会导致护具/宝物等与心材 TemplateId 撞数值时误判。
            // 心材属于杂物类 Misc(ItemType=12)，护具=1、宝物=2 等装备类不会撞进来。
            return _materialsWithBuildings != null
                && key.ItemType == ItemType.Misc
                && _materialsWithBuildings.Contains(key.TemplateKey);
        }

        #endregion

        #region 叶节点：TooltipItemOtherArea.Refresh

        /// <summary>
        /// TooltipItemOtherArea.Refresh 的 Postfix。
        /// 这里只是作为"tooltip 已刷新完毕"的可靠钩子（每次悬停物品必触发）。
        /// 实际把"已建造"格子加到 CommonArea 的属性网格里。
        /// </summary>
        [HarmonyPatch(typeof(TooltipItemOtherArea), "Refresh")]
        [HarmonyPostfix]
        internal static void OtherArea_Refresh_Postfix(TooltipItemOtherArea __instance)
        {
            if (!ModMain.GetSettingBool("MaterialTipHint", true)) return;
            if (_fiItemKey == null || _materialsWithBuildings == null || _fiCommonArea == null || _fiTextWeight == null) return;

            try
            {
                // 层级：OtherArea → MainPanel → TooltipMisc(挂 TooltipItemBase)
                var tipNode = __instance.transform.parent?.parent;
                var tooltip = tipNode?.GetComponent<TooltipItemBase>();
                if (tooltip == null) return;

                var itemKeyObj = _fiItemKey.GetValue(tooltip);
                if (itemKeyObj is not ItemKey itemKey) return;

                var commonArea = _fiCommonArea.GetValue(tooltip) as TooltipItemCommonArea;
                if (commonArea == null) return;

                // 非心材（或无建筑依赖）：若有残留的已建格子则销毁，避免切换物品后残留
                if (!IsMaterialWithBuildings(itemKey, out short templateId))
                {
                    CleanupBuildingCell(commonArea);
                    return;
                }

                // 是心材 → 确保格子存在（先放占位 0），每次实时查后端拿最新建筑数
                EnsureBuildingCell(commonArea, 0);
                QueryBuildingCount(tooltip, commonArea, templateId);
            }
            catch (Exception ex)
            {
                ModMain.LogDebug($"心材 postfix 异常: {ex.Message}");
            }
        }

        #endregion

        #region 后端查询

        /// <summary>
        /// 每次悬停都实时查后端，不缓存——建筑数在游戏过程中会变。
        /// </summary>
        private static void QueryBuildingCount(Component tooltip, TooltipItemCommonArea commonArea, short materialTemplateId)
        {
            var param = new SerializableModData();
            param.Set("materialTemplateId", (int)materialTemplateId);

            AsyncMethodCallbackDelegate callback = (int resultCode, RawDataPool resultPool) =>
            {
                try
                {
                    if (resultCode >= 0)
                    {
                        SerializableModData? result = null;
                        SerializerHolder<SerializableModData>.Deserialize(resultPool, resultCode, ref result!);
                        if (result != null && result.Get("success", out bool success) && success)
                        {
                            int count = 0;
                            result.Get("count", out count);
                            // 浮窗还在，更新数值
                            TryUpdateCell(commonArea, count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log($"[{ModMain.LogTag}] 心材查询回调异常: {ex.Message}");
                }
            };

            ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                (IAsyncMethodRequestHandler)tooltip,
                ModMain._modIdStr,
                "CountBuildingsRequiringMaterial",
                param,
                callback
            );
        }

        /// <summary>回调到达时更新已建造格子的数值</summary>
        private static void TryUpdateCell(TooltipItemCommonArea commonArea, int count)
        {
            if (commonArea == null) return;
            var cell = FindBuildingCell(commonArea);
            if (cell == null) return;

            var numText = cell.Find("NumLabel");
            if (numText != null)
            {
                var tmp = numText.GetComponent<TextMeshProUGUI>();
                if (tmp != null) tmp.SetText(count.ToString());
            }
        }

        #endregion

        #region UI：属性网格新增"已建造"格子

        /// <summary>已建格子的特殊名，用于查找</summary>
        private const string BuildingCellName = "BuildingCountCell";

        /// <summary>
        /// 确保 commonArea 的属性网格里存在"已建"格子，并显示给定数值。
        /// 以"重量"格为模板克隆（结构：Icon + title + WeightLabel），
        /// 改名、改标题为"已建"、值为 count，加到 GridLayoutGroup 末尾。
        /// 已存在则只更新数值。
        /// </summary>
        private static void EnsureBuildingCell(TooltipItemCommonArea commonArea, int count)
        {
            // 已存在 → 更新数值
            var existing = FindBuildingCell(commonArea);
            if (existing != null)
            {
                var numText = existing.Find("NumLabel");
                if (numText != null)
                {
                    var tmp = numText.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) tmp.SetText(count.ToString());
                }
                return;
            }

            // 以重量格为模板
            var weightLabel = _fiTextWeight?.GetValue(commonArea) as TextMeshProUGUI;
            if (weightLabel == null) return;

            // 重量格的根 = WeightLabel 的 parent（Weight 格子）
            var weightCell = weightLabel.transform.parent;
            if (weightCell == null) return;

            var gridLayout = weightCell.parent;
            if (gridLayout == null) return;

            // 克隆
            var newCell = UnityEngine.Object.Instantiate(weightCell.gameObject, gridLayout, false);
            newCell.name = BuildingCellName;
            newCell.SetActive(true);

            // 重量格子节点名固定：Icon / title / WeightLabel
            // 按名字精确改写，不依赖组件判断
            var titleTr = newCell.transform.Find("title");
            if (titleTr != null)
            {
                // title 有 TextLanguage 组件，Awake 时按 Key 把 text 设成"重量"。
                // 必须【立即】销毁该组件（DestroyImmediate，不延迟），否则后续 SetText
                // 后它仍可能把文本覆盖回本地化的"重量"。
                var langComp = titleTr.GetComponent("TextLanguage");
                if (langComp != null) UnityEngine.Object.DestroyImmediate(langComp);
                // TMP 克隆后直接赋 .text 可能不触发重新渲染，必须用 SetText 强制更新。
                var titleTmp = titleTr.GetComponent<TextMeshProUGUI>();
                if (titleTmp != null) titleTmp.SetText("已建");
            }

            var labelTr = newCell.transform.Find("WeightLabel");
            if (labelTr != null)
            {
                labelTr.name = "NumLabel";
                var numTmp = labelTr.GetComponent<TextMeshProUGUI>();
                if (numTmp != null) numTmp.SetText(count.ToString());
            }

            // 隐藏 Icon（已建造不需要图标）
            var iconTr = newCell.transform.Find("Icon");
            if (iconTr != null) iconTr.gameObject.SetActive(false);
        }

        /// <summary>查找已存在的已建格子</summary>
        private static Transform? FindBuildingCell(TooltipItemCommonArea commonArea)
        {
            // GridLayoutGroup 在 CommonArea → ImageBack → Layout 层级下
            var grid = commonArea.GetComponentInChildren<UnityEngine.UI.GridLayoutGroup>(true);
            if (grid == null) return null;
            return grid.transform.Find(BuildingCellName);
        }

        /// <summary>销毁所有残留的已建格子（切换到非心材时清理）</summary>
        private static void CleanupBuildingCell(TooltipItemCommonArea commonArea)
        {
            var grid = commonArea.GetComponentInChildren<UnityEngine.UI.GridLayoutGroup>(true);
            if (grid == null) return;

            // 倒序销毁所有同名格子（防御重复克隆）
            for (int i = grid.transform.childCount - 1; i >= 0; i--)
            {
                var child = grid.transform.GetChild(i);
                if (child.name == BuildingCellName)
                    UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        #endregion
    }
}
