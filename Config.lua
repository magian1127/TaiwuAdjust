return {
	Title = "综合调整",
	FileId = 3751327764,
	Description = "太吾绘卷综合调整。\n\n当前功能：\n查看人物背包书籍时显示该人物的阅读完成状态。\n制造装备资源自动填充。\n\n后续调整看我玩游戏的情况会陆续添加。",
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
			Key = "BookReadStatus",
			DisplayName = "人物书籍阅读状态",
			Description = "查看人物背包书籍时显示该人物的阅读完成状态",
			DefaultValue = true,
		},
		[2] = {
			SettingType = "Toggle",
			Key = "AutoFillCraftMaterial",
			DisplayName = "制造自动填充资源",
			Description = "制造装备选好材料后自动填满资源",
			DefaultValue = true,
		},
		[3] = {
			SettingType = "Toggle",
			Key = "DebugMode",
			DisplayName = "调试模式",
			Description = "输出详细日志到Player.log用于排查问题",
			DefaultValue = false,
		},
	},
	Version = "1.0.0.0",
	TagList = {
		[1] = "Optimizations",
	},
	WorkshopCover = "Cover.jpg",
	Visibility = 0,
	ChangeConfig = false,
	HasArchive = false,
	NeedRestartWhenSettingChanged = false
}
