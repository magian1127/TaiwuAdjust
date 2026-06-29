using System;
using System.Collections;
using System.Reflection;
using FrameWork.UISystem.UIElements;
using Game.Views.CharacterMenu;
using GameData.Utilities;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 角色界面底部标签栏扁平化补丁。
    /// 
    /// 原版有二级菜单的标签（人物→人物/队伍/关押, 武具→武具/车马, 造诣→造诣/武学/技艺,
    /// 关系→关系/族谱, 见闻→见闻/秘闻）被展开为一行独立扁平标签。
    /// 原父标签隐藏，替换为每个子页面的克隆标签，克隆脱离 CToggleGroup（group = null），
    /// 视觉 isOn 由 SyncCurrentSubPage 后置补丁手动同步。
    ///
    /// 快捷键 Q/E 用 SelectNext 前缀完全接管，在 _flatOrder 完整列表中循环导航。
    /// 游戏自带的 UpdateInformationTabVisibility 会在页面切换时重新激活原版见闻标签，
    /// 用 RehideParents 在 RefreshToggleAndPage 和 UpdateInformationTabVisibility 后
    /// 确保所有原父标签保持隐藏。
    /// </summary>
    [HarmonyPatch]
    internal static class FlatEquipTabsPatch
    {
        #region 有子页面的父标签定义

        /// <summary>
        /// 有子页面的父标签定义，每个条目包含：(父标签枚举, 子页面数组, 子页面文字键数组)。
        /// 子页面数组的第一个元素是默认子页面（即原版点击父标签时进入的页面）。
        /// 这些父标签的原版 Toggle 会被隐藏，替换为每个子页面的克隆。
        /// </summary>
        private static readonly (ECharacterSubToggleBase parent, ECharacterSubPage[] subPages, LanguageKey[] labelKeys)[]
            _parentDefs =
        {
            (ECharacterSubToggleBase.CharacterBase,
                new[] { ECharacterSubPage.Character, ECharacterSubPage.Team, ECharacterSubPage.Prison },
                new[] { LanguageKey.LK_CharacterMenu_Tog_Char, LanguageKey.LK_CharacterMenu_Tog_Team_New, LanguageKey.LK_Kidnap }),
            (ECharacterSubToggleBase.EquipmentBase,
                new[] { ECharacterSubPage.Equipment, ECharacterSubPage.Vehicle },
                new[] { LanguageKey.LK_CharacterMenu_Title_Equip, LanguageKey.LK_CarHorse }),
            (ECharacterSubToggleBase.AttainmentBase,
                new[] { ECharacterSubPage.Attainment, ECharacterSubPage.AttainmentLifeSkill, ECharacterSubPage.AttainmentCombatSkill },
                new[] { LanguageKey.LK_CharacterMenu_Tog_Attainment, LanguageKey.LK_CharacterMenu_Tog_LifeSkill, LanguageKey.LK_CharacterMenu_Tog_CombatSkill }),
            (ECharacterSubToggleBase.RelationshipBase,
                new[] { ECharacterSubPage.Relationship, ECharacterSubPage.Genealogy },
                new[] { LanguageKey.LK_RelationShip, LanguageKey.LK_Genealogy }),
            (ECharacterSubToggleBase.InformationBase,
                new[] { ECharacterSubPage.Information, ECharacterSubPage.Secret },
                new[] { LanguageKey.LK_CharacterMenu_Tog_Information, LanguageKey.LK_CharacterMenu_Tog_SecretInformation }),
        };

        /// <summary>
        /// 完整扁平标签的从左到右排列顺序。
        /// 用于 Q/E 快捷键导航（补丁 2 的 SelectNext 前缀在此列表上循环）。
        /// 包含所有标签：克隆的子页面 + 直接标签（持有/突破/内力/运功/经历，subPage=None）。
        /// </summary>
        private static readonly (ECharacterSubToggleBase parent, ECharacterSubPage subPage)[] _flatOrder =
        {
            (ECharacterSubToggleBase.CharacterBase,     ECharacterSubPage.Character),
            (ECharacterSubToggleBase.CharacterBase,     ECharacterSubPage.Team),
            (ECharacterSubToggleBase.CharacterBase,     ECharacterSubPage.Prison),
            (ECharacterSubToggleBase.EquipmentBase,     ECharacterSubPage.Equipment),
            (ECharacterSubToggleBase.EquipmentBase,     ECharacterSubPage.Vehicle),
            (ECharacterSubToggleBase.ItemBase,           ECharacterSubPage.None),
            (ECharacterSubToggleBase.AttainmentBase,     ECharacterSubPage.Attainment),
            (ECharacterSubToggleBase.AttainmentBase,     ECharacterSubPage.AttainmentLifeSkill),
            (ECharacterSubToggleBase.AttainmentBase,     ECharacterSubPage.AttainmentCombatSkill),
            (ECharacterSubToggleBase.PracticeBase,       ECharacterSubPage.None),
            (ECharacterSubToggleBase.NeiliBase,          ECharacterSubPage.None),
            (ECharacterSubToggleBase.EquipCombatSkillBase, ECharacterSubPage.None),
            (ECharacterSubToggleBase.RelationshipBase,   ECharacterSubPage.Relationship),
            (ECharacterSubToggleBase.RelationshipBase,   ECharacterSubPage.Genealogy),
            (ECharacterSubToggleBase.StoryBase,          ECharacterSubPage.None),
            (ECharacterSubToggleBase.InformationBase,    ECharacterSubPage.Information),
            (ECharacterSubToggleBase.InformationBase,    ECharacterSubPage.Secret),
        };
        #endregion

        #region 反射缓存（用于访问游戏私有成员）

        /// <summary>CharacterMenuToggleGroup._tabLookup（字典：父标签类型 → TabRuntime）</summary>
        private static FieldInfo? _fTabLookup;
        /// <summary>ViewCharacterMenu.mainToggleGroup</summary>
        private static FieldInfo? _fMainToggleGroup;
        /// <summary>ViewCharacterMenu.HandleSubPageButtonClicked（private 方法）</summary>
        private static MethodInfo? _mHandleSubPageClicked;
        /// <summary>CharacterMenuToggleGroup 的私有嵌套类型 TabRuntime</summary>
        private static Type? _tabRuntimeType;
        /// <summary>TabRuntime.Toggle（CToggle）</summary>
        private static FieldInfo? _fTabRuntimeToggle;
        /// <summary>InitRefs 是否已完成，避免重复反射查找</summary>
        private static bool _refsInited;

        /// <summary>
        /// 一次性缓存所有反射引用。
        /// 因为要访问 ViewCharacterMenu 和 CharacterMenuToggleGroup 的 private 字段，
        /// 需要在补丁运行时通过 Harmony 的 AccessTools 获取。
        /// </summary>
        private static void InitRefs()
        {
            if (_refsInited) return;
            var cmtgType = typeof(CharacterMenuToggleGroup);
            _fTabLookup = AccessTools.Field(cmtgType, "_tabLookup");
            _tabRuntimeType = cmtgType.GetNestedType("TabRuntime", BindingFlags.NonPublic);
            if (_tabRuntimeType != null)
                _fTabRuntimeToggle = AccessTools.Field(_tabRuntimeType, "Toggle");
            var vmcType = typeof(ViewCharacterMenu);
            _fMainToggleGroup = AccessTools.Field(vmcType, "mainToggleGroup");
            _mHandleSubPageClicked = AccessTools.Method(vmcType, "HandleSubPageButtonClicked",
                new[] { typeof(ECharacterSubToggleBase), typeof(ECharacterSubPage) });
            _refsInited = true;
        }
        #endregion

        #region 运行时状态

        /// <summary>所有克隆标签的数组，索引对应 _parentDefs 展开后顺序</summary>
        private static CToggle?[]? _allClones;
        /// <summary>防止 onValueChanged 递归进入 DoSwitch 或 SyncVisualState 的 reentry 锁</summary>
        private static bool _handlingSwitch;
        /// <summary>读取 Mod 设置"扁平标签栏"开关</summary>
        private static bool IsEnabled => ModMain.GetSettingBool("FlatEquipTabs", true);
        #endregion

        #region 补丁 1：展开子页面为独立标签

        /// <summary>
        /// ViewCharacterMenu.InitializeTabDropdownComponent 后置补丁。
        /// 在角色界面标签栏初始化完成后运行一次，执行一次性的展开操作：
        ///   1. 在人物标签前插入一条固定分隔线（LeadingSeparator）
        ///   2. 缩小所有标签宽度（10/17 等比例）
        ///   3. 遍历 _parentDefs，为每个子页面创建克隆标签并插入到原父标签后
        ///   4. 隐藏原父标签
        /// 克隆标签从 ItemBase（持有）的 GameObject 复制，脱离 CToggleGroup（group = null）。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ViewCharacterMenu), "InitializeTabDropdownComponent")]
        private static void InitializeTabDropdownComponent_Postfix(ViewCharacterMenu __instance)
        {
            if (!IsEnabled) return;
            InitRefs();
            Cleanup();

            var mainToggleGroup = _fMainToggleGroup?.GetValue(__instance) as CharacterMenuToggleGroup;
            if (mainToggleGroup == null) return;
            var lookup = _fTabLookup?.GetValue(mainToggleGroup) as IDictionary;
            if (lookup == null) return;

            // 用"持有"作为克隆模板：它无子页面、无悬停弹出、布局最干净
            var itemRuntime = lookup[ECharacterSubToggleBase.ItemBase];
            if (itemRuntime == null || _fTabRuntimeToggle == null) return;
            var itemToggle = _fTabRuntimeToggle.GetValue(itemRuntime) as CToggle;
            if (itemToggle == null) return;
            var parent = itemToggle.transform.parent;

            // 在第一个克隆前插入固定分隔线
            AddLeadingSeparator(itemToggle, parent, lookup);

            // 计算等比例缩窄后的标签宽度：原宽度 × (10 个原标签 ÷ 17 个总标签)
            int totalClones = 0;
            foreach (var (_, subPages, _) in _parentDefs) totalClones += subPages.Length;
            int totalTabs = totalClones + 5;
            float newW = (itemToggle.GetComponent<RectTransform>()?.rect.width ?? 100f) * 10f / totalTabs;

            // 缩小所有原版标签（包括持有/突破等留存的直接标签）
            foreach (ECharacterSubToggleBase key in Enum.GetValues(typeof(ECharacterSubToggleBase)))
            { if (key != ECharacterSubToggleBase.None) ResizeToggle(lookup, key, newW); }

            _allClones = new CToggle?[totalClones];
            int cloneIdx = 0;
            foreach (var (parentKey, subPages, labelKeys) in _parentDefs)
            {
                // 安全检查：lookup 可能不含该键（FunctionLock 未解锁等）
                if (!lookup.Contains(parentKey)) continue;
                var parentRuntime = lookup[parentKey];
                if (parentRuntime == null || _fTabRuntimeToggle == null) continue;
                var parentToggle = _fTabRuntimeToggle.GetValue(parentRuntime) as CToggle;
                if (parentToggle == null) continue;

                int insertIdx = parentToggle.transform.GetSiblingIndex() + 1;
                for (int s = 0; s < subPages.Length; s++)
                {
                    // 直接使用硬编码文本，不依赖 LocalStringManager（语言包未加载时会炸）
                    var label = GetLabelText(parentKey, s);

                    var clone = UnityEngine.Object.Instantiate(itemToggle.gameObject, parent);
                    clone.name = $"Flat_{label}";
                    clone.transform.SetSiblingIndex(insertIdx + s);
                    ResizeChildCheckmark(clone, newW);

                    var ct = clone.GetComponent<CToggle>()!;
                    ct.group = null;                   // 脱离 CToggleGroup，独立管理
                    ct.interactable = true;
                    var cloneRt = clone.GetComponent<RectTransform>();
                    if (cloneRt != null) cloneRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, newW);
                    var le = clone.GetComponent<LayoutElement>();
                    if (le == null) le = clone.AddComponent<LayoutElement>();
                    le.preferredWidth = newW; le.flexibleWidth = 0;  // 固定宽度，防止选中后撑大

                    // 清除原 PointerTrigger 的悬停事件（原版绑定的是二级菜单弹出逻辑）
                    var pt = clone.GetComponent<PointerTrigger>();
                    if (pt != null) { pt.EnterEvent?.RemoveAllListeners(); pt.ExitEvent?.RemoveAllListeners(); }

                    var style = ct.GetComponent<ToggleStyle>();
                    if (style != null) style.SetLabelText(label);

                    ct.onValueChanged.RemoveAllListeners();
                    var capturedPage = subPages[s];
                    ct.onValueChanged.AddListener((bool isOn) =>
                    {
                        // _handlingSwitch 保护：来自 SyncVisualState 的编程设置不触发页面跳转
                        if (_handlingSwitch) return;
                        // 用户点击了已激活标签（Unity Toggle 会翻转 isOn）→ 复原
                        if (!isOn) { _handlingSwitch = true; ct.isOn = true; _handlingSwitch = false; return; }
                        DoSwitch(capturedPage);
                    });
                    _allClones[cloneIdx++] = ct;
                }
                // 隐藏原父标签：它的位置由克隆标签取代
                parentToggle.gameObject.SetActive(false);
            }
            ModMain.LogDebug($"已展开 {totalClones} 个子标签");
        }

        /// <summary>
        /// 同步缩小克隆标签的 checkmark（选中高亮红底图片）
        /// </summary>
        private static void ResizeChildCheckmark(GameObject go, float w)
        {
            var cm = go.transform.Find("checkmark")?.GetComponent<CImage>();
            if (cm != null)
                cm.GetComponent<RectTransform>()?.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);
        }
        #endregion

        #region 补丁 2：QE 快捷键在完整扁平列表上导航

        /// <summary>
        /// CToggleGroup.SelectNext 前缀补丁。
        /// 当 Q/E 触发 SelectNext 时，如果当前 CToggleGroup 属于 CharacterMenuToggleGroup，
        /// 完全接管导航逻辑：在 _flatOrder 列表中找到当前页面，然后移动到 prev/next 条目，
        /// 直接通过 HandleSubPageButtonClicked 切换页面，跳过原版 SelectNext 的 toggleList 遍历。
        ///
        /// 原版 SelectNext 只遍历 CToggleGroup 内的可见 Toggle，会跳过隐藏的原父标签，
        /// 导致 Q/E 只能在持有/突破等 5 个留存标签之间循环，无法导航到扁平克隆标签。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CToggleGroup), "SelectNext")]
        private static bool SelectNext_Prefix(CToggleGroup __instance, ref bool __result, int direction)
        {
            if (!IsEnabled
                // CharacterMenuToggleGroup 在 CToggleGroup 的父级组件中，用它判断是否为本补丁管理的实例
                || __instance.GetComponentInParent<CharacterMenuToggleGroup>() == null)
                return true; // 不是角色菜单的 CToggleGroup，走原逻辑

            var curToggle = ViewCharacterMenu.CurSubToggleIndex;
            var curPage = ViewCharacterMenu.CurSubSubPageIndex;

            // 在 _flatOrder 中找到当前位置
            int curIdx = -1;
            for (int i = 0; i < _flatOrder.Length; i++)
            {
                var (p, sp) = _flatOrder[i];
                if (p == curToggle && sp == curPage) { curIdx = i; break; }
            }
            if (curIdx < 0) { __result = false; return false; }

            int newIdx = (curIdx + direction + _flatOrder.Length) % _flatOrder.Length;
            var (target, targetPage) = _flatOrder[newIdx];

            _handlingSwitch = true;
            try
            {
                var vcm = UIElement.CharacterMenu?.UiBaseAs<ViewCharacterMenu>();
                if (vcm != null && _mHandleSubPageClicked != null)
                    _mHandleSubPageClicked.Invoke(vcm, new object[] { target, targetPage });
            }
            finally { _handlingSwitch = false; }

            __result = true;
            return false; // 跳过原版 SelectNext
        }
        #endregion

        #region 补丁 3：同步视觉选中状态

        /// <summary>
        /// CharacterMenuToggleGroup.SyncCurrentSubPage 后置补丁。
        /// 每次子页面切换后遍历所有克隆标签，将当前子页面对应的克隆设为 isOn=true，
        /// 其他克隆设为 isOn=false。
        /// 因为克隆已脱离 CToggleGroup（group=null），它们的 isOn 不会再被 CToggleGroup 自动管理，
        /// 必须在此补丁中手动同步。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterMenuToggleGroup), "SyncCurrentSubPage")]
        private static void SyncCurrentSubPage_Postfix()
        {
            if (!IsEnabled || _allClones == null) return;
            var curPage = ViewCharacterMenu.CurSubSubPageIndex;
            _handlingSwitch = true;
            foreach (var (_, subPages, _) in _parentDefs)
                for (int s = 0; s < subPages.Length; s++)
                {
                    int idx = GetGlobalCloneIndex(subPages[s]);
                    if (idx < 0 || idx >= _allClones.Length) continue;
                    if (_allClones[idx] != null) _allClones[idx]!.isOn = curPage == subPages[s];
                }
            _handlingSwitch = false;
        }

        /// <summary>
        /// 根据子页面类型查找它在 _allClones 数组中的全局索引
        /// </summary>
        private static int GetGlobalCloneIndex(ECharacterSubPage subPage)
        {
            int idx = 0;
            foreach (var (_, subPages, _) in _parentDefs)
            {
                for (int s = 0; s < subPages.Length; s++)
                { if (subPages[s] == subPage) return idx + s; }
                idx += subPages.Length;
            }
            return -1;
        }
        #endregion

        #region 补丁 4：页面切换后重新隐藏父标签

        /// <summary>
        /// RefreshToggleAndPage 后置补丁。每次页面切换完成后，
        /// 确保所有被 _parentDefs 管理的原父标签保持隐藏。
        /// 主要针对 UpdateInformationTabVisibility 会重新激活见闻标签的问题。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ViewCharacterMenu), "RefreshToggleAndPage")]
        private static void RefreshToggleAndPage_Postfix() { RehideParents(); }

        /// <summary>
        /// UpdateInformationTabVisibility 后置补丁。
        /// 游戏自带的见闻显隐逻辑（根据 FunctionLockManager.IsFunctionUnlock(20)）
        /// 会在切换页面时自动 SetActive(InformationBase)，本补丁在它之后立刻隐藏。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ViewCharacterMenu), "UpdateInformationTabVisibility")]
        private static void UpdateInformationTabVisibility_Postfix() { RehideParents(); }

        /// <summary>
        /// 遍历 _parentDefs 中的所有父标签，如果它们当前是激活状态（gameObject.activeSelf == true）
        /// 就设为 SetActive(false)。因为克隆标签已取代了它们的视觉和功能位置。
        /// </summary>
        private static void RehideParents()
        {
            if (!IsEnabled || _allClones == null) return;
            var vcm = UIElement.CharacterMenu?.UiBaseAs<ViewCharacterMenu>();
            if (vcm == null) return;
            var mainGroup = _fMainToggleGroup?.GetValue(vcm) as CharacterMenuToggleGroup;
            if (mainGroup == null) return;
            var lookup = _fTabLookup?.GetValue(mainGroup) as IDictionary;
            if (lookup == null) return;
            foreach (var (parentKey, _, _) in _parentDefs)
            {
                if (!lookup.Contains(parentKey)) continue;
                var runtime = lookup[parentKey];
                if (runtime == null || _fTabRuntimeToggle == null) continue;
                var toggle = _fTabRuntimeToggle.GetValue(runtime) as CToggle;
                if (toggle != null && toggle.gameObject.activeSelf)
                    toggle.gameObject.SetActive(false);
            }
        }
        #endregion

        #region 辅助方法

        /// <summary>
        /// 硬编码标签文本，不依赖 LocalStringManager 避免语言包加载时序问题。
        /// </summary>
        private static string GetLabelText(ECharacterSubToggleBase parentKey, int subIndex)
        {
            return parentKey switch
            {
                ECharacterSubToggleBase.CharacterBase => subIndex switch { 0 => "人物", 1 => "队伍", _ => "关押" },
                ECharacterSubToggleBase.EquipmentBase => subIndex switch { 0 => "武具", _ => "车马" },
                ECharacterSubToggleBase.AttainmentBase => subIndex switch { 0 => "造诣", 1 => "技艺", _ => "武学" },
                ECharacterSubToggleBase.RelationshipBase => subIndex switch { 0 => "关系", _ => "族谱" },
                ECharacterSubToggleBase.InformationBase => subIndex switch { 0 => "见闻", _ => "秘闻" },
                _ => "?",
            };
        }

        /// <summary>
        /// 跳转到指定子页面。
        /// 通过反射调用 ViewCharacterMenu.HandleSubPageButtonClicked(None, subPage)，
        /// 由 ApplyPageIndex 内部的 GetSubTogglekeyBySubPage 自动解析父标签类型。
        /// </summary>
        private static void DoSwitch(ECharacterSubPage subPage)
        {
            if (_handlingSwitch) return;
            _handlingSwitch = true;
            try
            {
                var vcm = UIElement.CharacterMenu?.UiBaseAs<ViewCharacterMenu>();
                if (vcm != null && _mHandleSubPageClicked != null)
                    _mHandleSubPageClicked.Invoke(vcm, new object[] { ECharacterSubToggleBase.None, subPage });
            }
            finally { _handlingSwitch = false; }
        }

        /// <summary>
        /// 销毁所有克隆标签，用于角色界面重新初始化时清理旧的克隆
        /// </summary>
        private static void Cleanup()
        {
            if (_allClones == null) return;
            for (int i = 0; i < _allClones.Length; i++)
            {
                if (_allClones[i]?.gameObject != null) UnityEngine.Object.Destroy(_allClones[i]!.gameObject);
                _allClones[i] = null;
            }
        }

        /// <summary>
        /// 缩小指定原版标签到目标宽度，添加 LayoutElement 固定宽度防止选中后撑大，
        /// 同步缩小 checkmark 高亮红底图片。
        /// </summary>
        private static void ResizeToggle(IDictionary lookup, ECharacterSubToggleBase key, float newW)
        {
            if (!lookup.Contains(key)) return;
            var runtime = lookup[key];
            if (runtime == null || _fTabRuntimeToggle == null) return;
            var toggle = _fTabRuntimeToggle.GetValue(runtime) as CToggle;
            if (toggle?.gameObject == null) return;

            toggle.GetComponent<RectTransform>()?.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, newW);
            var le = toggle.GetComponent<LayoutElement>();
            if (le == null) le = toggle.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = newW; le.flexibleWidth = 0;
            ResizeChildCheckmark(toggle.gameObject, newW);
        }

        /// <summary>
        /// 在标签栏开头（Q 提示键与第一个人物标签之间）添加一条固定分隔线。
        /// 从 ItemBase 的 ImgLineRow 克隆，移除 CommonToggleAutoHideLine 使其永久显示。
        /// </summary>
        private static void AddLeadingSeparator(CToggle template, Transform parent, IDictionary lookup)
        {
            var lineSrc = template.transform.Find("ImgLineRow")?.gameObject;
            if (lineSrc == null) return;

            // 找到原人物标签的位置，插在它前面（SetSiblingIndex 不 +1），
            // 这样分隔线位于 Q 热键显示和人物标签之间
            var charRuntime = lookup[ECharacterSubToggleBase.CharacterBase];
            if (charRuntime == null || _fTabRuntimeToggle == null) return;
            var charToggle = _fTabRuntimeToggle.GetValue(charRuntime) as CToggle;
            if (charToggle == null) return;

            int insertPos = charToggle.transform.GetSiblingIndex();

            var lineClone = UnityEngine.Object.Instantiate(lineSrc, parent);
            lineClone.name = "LeadingSeparator";
            lineClone.transform.SetSiblingIndex(insertPos);
            lineClone.SetActive(true);
        }
        #endregion
    }
}
