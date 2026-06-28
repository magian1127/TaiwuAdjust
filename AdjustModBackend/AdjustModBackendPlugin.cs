using System;
using GameData.Common;
using GameData.Domains;
using GameData.Domains.Item;
using GameData.Domains.Mod;
using TaiwuModdingLib.Core.Plugin;

namespace AdjustModBackend
{
    /// <summary>
    /// 调整模块后端插件 - 提供 NPC 书籍阅读状态查询的 Mod 方法
    /// </summary>
    [PluginConfig("AdjustModBackend", "Magian", "1.0.0.0")]
    public class ModMain : TaiwuRemakePlugin
    {
        /// <summary>
        /// 插件初始化，注册 Mod 方法到 DomainManager
        /// </summary>
        public override void Initialize()
        {
            DomainManager.Mod.AddModMethod(
                ModIdStr,
                "GetNpcBookReadState",
                new Func<DataContext, SerializableModData, SerializableModData>(HandleGetNpcBookReadState)
            );
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
        /// 返回失败结果
        /// </summary>
        private static SerializableModData Fail(SerializableModData result, string reason)
        {
            result.Set("success", false);
            result.Set("error", reason);
            return result;
        }

        /// <summary>
        /// 插件清理
        /// </summary>
        public override void Dispose()
        {
            // 无需额外清理
        }
    }
}
