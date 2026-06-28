return {
	Title = "综合调整",
	FileId = 3751327764,
	Description = [[太吾绘卷综合调整。

当前功能：
查看人物背包书籍时显示该人物的阅读完成状态。
制造装备资源自动填充。
突破选功法时自动选择总纲和正逆练篇章。
突破格子界面额外加了疗伤按钮。
地图地块NPC列表悬停时默认显示互动信息（原版需按住Shift）。
角色界面直接显示为独立标签（不再有悬停二级菜单）。

每个功能都可以在模组设置中关闭
后续调整看我玩游戏的情况会陆续添加。
]],
	Cover = "Cover.jpg",
	Author = "Magian",
	BackendPlugins = {
		[1] = "AdjustModBackend.dll",
	},
	FrontendPlugins = {
		[1] = "AdjustMod.dll",
	},
	Source = 1,
	GameVersion = "1.0.20.0",
	DefaultSettings = {
		[1] = {
			SettingType = "Toggle",
			Key = "DebugMode",
			DisplayName = "调试模式",
			Description = "输出详细日志到Player.log用于排查问题",
			DefaultValue = false,
		},
		[2] = {
			SettingType = "Toggle",
			Key = "BookReadStatus",
			DisplayName = "人物书籍阅读状态",
			Description = "查看人物背包书籍时显示该人物的阅读完成状态",
			DefaultValue = true,
		},
		[3] = {
			SettingType = "Toggle",
			Key = "AutoFillCraftMaterial",
			DisplayName = "制造自动填充资源",
			Description = "制造装备选好材料后自动填满资源",
			DefaultValue = true,
		},
		[4] = {
			SettingType = "Toggle",
			Key = "AutoBreakSelect",
			DisplayName = "突破自动选择总纲",
			Description = "突破功法时自动选择总纲和正逆练篇章（总纲随机选已读的，篇章优先正练）",
			DefaultValue = true,
		},
		[5] = {
			SettingType = "Toggle",
			Key = "AutoHealButton",
			DisplayName = "突破界面疗伤按钮",
			Description = "在实际突破（走格子）界面加一个疗伤按钮，主角有伤可治时可点",
			DefaultValue = true,
		},
		[6] = {
			SettingType = "Toggle",
			Key = "MapCharHoverInteraction",
			DisplayName = "地块NPC悬停默认显示互动",
			Description = "在地图地块的NPC列表上悬停时，默认直接显示互动信息（原版需按住Shift），按Alt看详细信息",
			DefaultValue = true,
		},
		[7] = {
			SettingType = "Toggle",
			Key = "FlatEquipTabs",
			DisplayName = "扁平标签栏",
			Description = "角色界面底部标签栏展开为单排扁平标签，去掉所有二级悬停菜单（人物→队伍/关押、武具→车马、造诣→技艺/武学、关系→族谱、见闻→秘闻等）",
			DefaultValue = true,
		},
	},
	Version = "1.0.0.4",
	TagList = {
		[1] = "Optimizations",
	},
	WorkshopCover = "Cover.jpg",
	Visibility = 0,
	ChangeConfig = false,
	HasArchive = false,
	NeedRestartWhenSettingChanged = false
}
