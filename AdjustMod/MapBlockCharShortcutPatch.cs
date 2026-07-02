using System;
using System.Reflection;
using FrameWork;
using FrameWork.UISystem.UIElements;
using Game.Views.MapBlockCharList;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AdjustMod
{
    /// <summary>
    /// 地图地块左侧人物列表 1-9 快捷键对话补丁。
    ///
    /// 【设计思路】
    /// 不新增任何 MonoBehaviour / Update 循环，hook 游戏已有的集中按键处理入口
    /// UIManager.Update()（处理 Esc/右键隐藏等全局快捷键的地方）。
    /// 在它处理完之后检查数字键 1-9，如果未被其他 UI 消费则执行对话。
    ///
    /// 【冲突防护】
    ///   1. EventSystem 有选中对象时跳过（InputField 正在编辑等）
    ///   2. 仅当 UIElement.MapBlockCharList 是当前顶层 UI 时才响应
    ///   3. 搜索输入框聚焦时跳过
    ///   4. 不响应 Ctrl/Alt/Shift 组合键
    ///   5. 仅对 Content 中可见且可交互的角色生效
    /// </summary>
    [HarmonyPatch]
    internal static class MapBlockCharShortcutPatch
    {
        #region 反射缓存

        private static FieldInfo? _fInputField;
        private static FieldInfo? _fScrollWrapper;
        private static bool _refsInited;

        internal static void Init()
        {
            if (_refsInited) return;
            var type = typeof(ViewMapBlockCharList);
            // ViewMapBlockCharList.characterFilter (TMP_InputField，搜索输入框)
            _fInputField = AccessTools.Field(type, "characterFilter");
            // ViewMapBlockCharList.charScroll (MapBlockCharScroll，滚动容器)
            _fScrollWrapper = AccessTools.Field(type, "charScroll");
            _refsInited = true;
            Debug.Log($"[{ModMain.LogTag}] 快捷键反射：inputField={_fInputField != null} scroll={_fScrollWrapper != null}");
        }

        /// <summary>
        /// 检测数字键 1-9（主键盘 + 小键盘），跳过组合键。
        /// 返回 0-8，无匹配返回 -1。
        /// </summary>
        private static int GetKeyIndex()
        {
            if (!Input.anyKeyDown) return -1;
            // 组合键跳过
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

        /// <summary>
        /// 在 Content 中找到第 Nth 个可见可交互的 MapBlockChar 并触发点击。
        /// </summary>
        private static bool ExecuteShortcut(int keyIndex, ViewMapBlockCharList list)
        {
            var scrollWrapper = _fScrollWrapper?.GetValue(list) as MapBlockCharScroll;
            if (scrollWrapper == null) return false;

            var content = scrollWrapper.Content;
            if (content == null) return false;

            int visibleIndex = 0;
            foreach (Transform child in content)
            {
                var mbChar = child.GetComponent<MapBlockChar>();
                if (mbChar == null || !mbChar.isActiveAndEnabled || !mbChar.Interactable)
                    continue;

                if (visibleIndex == keyIndex)
                {
                    ModMain.LogDebug($"快捷键 {keyIndex + 1} → {mbChar.name} (CharId={mbChar.CharId})");
                    mbChar.OnClick();
                    return true;
                }
                visibleIndex++;
            }
            return false;
        }

        #endregion

        #region Harmony Patch —— 借 UIManager.Update 拦截按键

        /// <summary>
        /// UIManager.Update 后置补丁。UIManager 每帧处理全局输入（Esc/右键/Space 等），
        /// 本 postfix 在其之后检查 1-9 数字快捷键。
        /// </summary>
        [HarmonyPatch(typeof(UIManager), "Update")]
        [HarmonyPostfix]
        internal static void UIManager_Update_Postfix()
        {
            if (!ModMain.GetSettingBool("MapBlockCharShortcut", true)) return;

            int keyIndex = GetKeyIndex();
            if (keyIndex < 0) return;

            // EventSystem 有选中对象时跳过（如 InputField 正在编辑文字）
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
                return;

            // 列表必须是当前顶层 UI
            var element = UIElement.MapBlockCharList;
            if (element == null || !UIManager.Instance.IsFocusElement(element)) return;

            var list = element.UiBaseAs<ViewMapBlockCharList>();
            if (list == null || !list.isActiveAndEnabled) return;

            // 搜索输入框聚焦时跳过
            if (_fInputField != null)
            {
                var inputField = _fInputField.GetValue(list) as TMPro.TMP_InputField;
                if (inputField != null && inputField.isFocused) return;
            }

            ExecuteShortcut(keyIndex, list);
        }

        #endregion
    }
}
