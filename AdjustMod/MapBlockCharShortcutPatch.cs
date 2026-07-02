using System;
using FrameWork.UISystem.UIElements;
using Game.Views.Adventure;
using Game.Views.MapBlockCharList;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AdjustMod
{
    /// <summary>
    /// 地图/奇遇左侧互动列表 1-9 快捷键补丁。
    ///
    /// 【设计思路】
    /// 不新增任何 MonoBehaviour / Update 循环，hook 游戏已有的集中按键处理入口
    /// UIManager.Update()（处理 Esc/右键隐藏等全局快捷键的地方）。
    /// 在它处理完之后检查数字键 1-9，如果未被其他 UI 消费则执行对应操作。
    ///
    /// 两种场景共用同一套按键检测逻辑：
    ///   1. 世界地图左侧人物列表 ViewMapBlockCharList → 调 MapBlockChar.OnClick()（对话）
    ///   2. 奇遇左侧元素列表 ViewAdventureRemake → 调 CButton.onClick.Invoke()（互动）
    ///
    /// 【为什么不挂 MonoBehaviour / 劫持滚动事件？】
    ///   第一次实现挂载 MonoBehaviour 到 UI_MapBlockCharList，每帧 Update 扫描 → 被批太蠢。
    ///   第二次劫持 InfinityScrollLegacy.LateUpdate → 语义不对（滚动事件不是按键检测）。
    ///   第三次（终版）：hook UIManager.Update —— 游戏自身 Esc/右键/Space 全在这里处理，
    ///   这是全局键盘快捷键的正确 hook 点。
    ///
    /// 【踩坑记录】
    ///   1. 目标类不是 UI_MapBlockCharList 而是 ViewMapBlockCharList（前者废弃不在 UI 树中）
    ///   2. LoopVerticalScrollRect 继承自 LoopScrollRectBase（继承 UIBehaviour），不是 ScrollRect
    ///   3. as ScrollRect 转型静默失败返回 null，整个方法不执行也不报错
    ///
    /// 【冲突防护】
    ///   1. EventSystem.current.currentSelectedGameObject != null 时跳过（InputField 正在编辑）
    ///   2. 仅当对应 UI 是顶层焦点时才响应（UIManager.Instance.IsFocusElement）
    ///   3. 搜索输入框聚焦时跳过（用户可能正在输入过滤条件）
    ///   4. 不响应 Ctrl/Alt/Shift 组合键
    ///   5. 仅对 Content 中可见且可交互的角色/元素生效
    /// </summary>
    [HarmonyPatch]
    internal static class MapBlockCharShortcutPatch
    {
        #region 反射缓存

        /// <summary>ViewMapBlockCharList.characterFilter —— 搜索输入框（TMP_InputField）。聚焦时跳过快捷键。</summary>
        private static System.Reflection.FieldInfo? _fInputField;

        /// <summary>ViewMapBlockCharList.charScroll —— MapBlockCharScroll 滚动容器，通过 .Content 取角色列表。</summary>
        private static System.Reflection.FieldInfo? _fScrollWrapper;

        /// <summary>ViewAdventureRemake.elementScrollView —— LoopVerticalScrollRect（★不是 ScrollRect！）。</summary>
        private static System.Reflection.FieldInfo? _fAdvElementScroll;

        private static bool _refsInited;

        /// <summary>
        /// 一次性缓存反射字段引用。
        /// 在 ModMain.Initialize() 中调用，PatchAll 之前完成，避免运行时重复反射。
        /// </summary>
        internal static void Init()
        {
            if (_refsInited) return;

            // 地图人物列表字段
            var mapType = typeof(ViewMapBlockCharList);
            _fInputField = AccessTools.Field(mapType, "characterFilter");
            _fScrollWrapper = AccessTools.Field(mapType, "charScroll");

            // 奇遇元素列表字段
            var advType = typeof(ViewAdventureRemake);
            _fAdvElementScroll = AccessTools.Field(advType, "elementScrollView");

            _refsInited = true;
            Debug.Log($"[{ModMain.LogTag}] 快捷键反射：mapInput={_fInputField != null} mapScroll={_fScrollWrapper != null} advScroll={_fAdvElementScroll != null}");
        }

        /// <summary>
        /// 检测数字键 1-9 是否被按下。
        /// 同时检测主键盘区（Alpha1-9）和小键盘区（Keypad1-9），排除组合键。
        /// </summary>
        /// <returns>0-8 对应按键 1-9，无匹配返回 -1</returns>
        private static int GetKeyIndex()
        {
            if (!Input.anyKeyDown) return -1;

            // 组合键跳过（Ctrl/Alt/Shift + 数字可能是其他功能的快捷键）
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                return -1;

            for (int i = 0; i < 9; i++)
            {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)) ||
                    Input.GetKeyDown((KeyCode)((int)KeyCode.Keypad1 + i)))
                    return i;
            }
            return -1;
        }

        #endregion

        #region 地图人物列表（世界地图左侧）

        /// <summary>
        /// 尝试在世界地图左侧人物列表中执行快捷键对话。
        ///
        /// 逻辑链：
        ///   1. 确认 UIElement.MapBlockCharList 是当前顶层 UI
        ///   2. 获取 ViewMapBlockCharList 实例
        ///   3. 检查搜索输入框是否聚焦（聚焦时跳过）
        ///   4. 通过 charScroll.Content 获取角色列表容器
        ///   5. 遍历子节点找 MapBlockChar 组件（仅活跃且可交互的）
        ///   6. 第 Nth 个匹配的即为目标角色，调 OnClick()（与鼠标点击同路径）
        /// </summary>
        private static bool TryExecuteMapList(int keyIndex)
        {
            // 步骤 1：确认列表是焦点 UI
            var element = UIElement.MapBlockCharList;
            if (element == null || !UIManager.Instance.IsFocusElement(element)) return false;

            // 步骤 2：获取实例
            var list = element.UiBaseAs<ViewMapBlockCharList>();
            if (list == null || !list.isActiveAndEnabled) return false;

            // 步骤 3：搜索输入框聚焦时跳过
            if (_fInputField != null)
            {
                var inputField = _fInputField.GetValue(list) as TMPro.TMP_InputField;
                if (inputField != null && inputField.isFocused) return false;
            }

            // 步骤 4：获取 Content（角色列表的容器）
            var scrollWrapper = _fScrollWrapper?.GetValue(list) as MapBlockCharScroll;
            if (scrollWrapper?.Content == null) return false;

            // 步骤 5-6：遍历找第 Nth 个可交互角色
            int visibleIndex = 0;
            foreach (Transform child in scrollWrapper.Content)
            {
                var mbChar = child.GetComponent<MapBlockChar>();
                if (mbChar == null || !mbChar.isActiveAndEnabled || !mbChar.Interactable) continue;
                if (visibleIndex == keyIndex)
                {
                    ModMain.LogDebug($"地图快捷键 {keyIndex + 1} → {mbChar.name} (CharId={mbChar.CharId})");
                    mbChar.OnClick();
                    return true;
                }
                visibleIndex++;
            }
            return false;
        }

        #endregion

        #region 奇遇元素列表（奇遇/五湖商会等左侧）

        /// <summary>
        /// 尝试在奇遇左侧元素列表中执行快捷键互动。
        ///
        /// 逻辑链：
        ///   1. 确认 UIElement.AdventureRemake 是当前顶层 UI
        ///   2. 获取 ViewAdventureRemake 实例
        ///   3. 检查搜索输入框 searchByName 是否聚焦
        ///   4. 获取 elementScrollView（★LoopVerticalScrollRect，非 ScrollRect★）
        ///   5. 遍历 .content 子节点找 AdventureElementTemplate + CButton
        ///   6. 第 Nth 个可交互的即为目标元素，调 btn.onClick.Invoke() 触发互动
        ///
        /// 【坑：as ScrollRect 转型失败】
        ///   LoopVerticalScrollRect 继承链：→ LoopScrollRect → LoopScrollRectBase → UIBehaviour
        ///   它有自己的 .content 属性（RectTransform），但无法 as ScrollRect 转型。
        ///   必须 as LoopVerticalScrollRect 才能访问 .content。
        /// </summary>
        private static bool TryExecuteAdventure(int keyIndex)
        {
            // 步骤 1：确认是焦点 UI
            var element = UIElement.AdventureRemake;
            if (element == null || !UIManager.Instance.IsFocusElement(element)) return false;

            // 步骤 2：获取实例
            var adv = element.UiBaseAs<ViewAdventureRemake>();
            if (adv == null || !adv.isActiveAndEnabled) return false;

            // 步骤 3：搜索输入框聚焦时跳过
            if (adv.searchByName != null && adv.searchByName.isFocused) return false;

            // 步骤 4：获取 LoopVerticalScrollRect（★不能 as ScrollRect★）
            var loopScroll = _fAdvElementScroll?.GetValue(adv) as LoopVerticalScrollRect;
            if (loopScroll?.content == null) return false;

            // 步骤 5-6：遍历找第 Nth 个可交互元素
            int visibleIndex = 0;
            foreach (Transform child in loopScroll.content)
            {
                var elem = child.GetComponent<AdventureElementTemplate>();
                if (elem == null || !child.gameObject.activeSelf) continue;

                // CButton 与 AdventureElementTemplate 在同一 GameObject 上
                var btn = child.GetComponent<CButton>();
                if (btn == null || !btn.interactable) continue;

                if (visibleIndex == keyIndex)
                {
                    ModMain.LogDebug($"奇遇快捷键 {keyIndex + 1} → {child.name}");
                    // 调 onClick.Invoke() 与鼠标点击走同一路径
                    btn.onClick.Invoke();
                    return true;
                }
                visibleIndex++;
            }
            return false;
        }

        #endregion

        #region Harmony Patch —— 借 UIManager.Update 拦截按键

        /// <summary>
        /// UIManager.Update 后置补丁。
        ///
        /// 【触发时机】UIManager.Update() 每帧调用，先执行 CheckQuickHide（处理 Esc/右键隐藏），
        /// 然后执行本 postfix。如果按键已被其他 UI 消费（如 InputField 正在编辑），跳过。
        ///
        /// 【执行顺序】
        ///   1. 检查设置开关
        ///   2. 检测 1-9 按键
        ///   3. EventSystem 有选中对象时跳过（输入框/下拉菜单等 UI 控件正在使用键盘）
        ///   4. 先试地图人物列表（TryExecuteMapList），成功了就返回
        ///   5. 地图没命中再试奇遇元素列表（TryExecuteAdventure）
        ///      两者不会同时出现，一个 return 另一个就自然跳过。
        /// </summary>
        [HarmonyPatch(typeof(UIManager), "Update")]
        [HarmonyPostfix]
        internal static void UIManager_Update_Postfix()
        {
            if (!ModMain.GetSettingBool("MapBlockCharShortcut", true)) return;

            int keyIndex = GetKeyIndex();
            if (keyIndex < 0) return;

            // EventSystem 有选中对象（InputField 编辑中 / 下拉菜单展开等），跳过
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
                return;

            // 先试地图，再试奇遇
            if (TryExecuteMapList(keyIndex)) return;
            TryExecuteAdventure(keyIndex);
        }

        #endregion
    }
}
