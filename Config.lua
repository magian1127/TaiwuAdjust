return {
	Title = "综合调整",
	FileId = 3751327764,
	Description = [[太吾绘卷综合调整。

当前功能：
查看人物背包书籍时显示该人物的阅读完成状态。
制造装备资源自动填充。
突破选功法时自动选择总纲和正逆练篇章。
突破界面筛选的「功法状态」追加「可突破」选项，勾选后只列出可突破的功法。
突破格子界面额外加了疗伤按钮。
地图地块NPC列表悬停时默认显示互动信息（原版需按住Shift）。
功法悬停全部详情，按住ALT，改成ALT切换。
物品悬停默认显示全部详情（原版需按住Alt），悬浮的注解下移。
角色界面底部标签栏展开为单排扁平标签，去掉所有二级悬停菜单，设置需要重启。
战斗准备界面加内力震慑按钮，精纯高于对方时，可扣内力直接战斗胜利。
点击右上角村名人数可直接打开村民名册。
地块左侧列表可按数字键 1-9 快速对话。
心材悬浮信息显示已建造数。
书籍/装备可借用给队友（不增加好感度和警觉度，取回也不减少）。

每个功能都可以在模组设置中关闭
后续调整看我玩游戏的情况会陆续添加。

源码
https://github.com/magian1127/TaiwuAdjust
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
		{
			SettingType = "Toggle",
			Key = "DebugMode",
			DisplayName = "调试模式",
			Description = "输出详细日志到Player.log用于排查问题",
			DefaultValue = false,
		},
		{
			SettingType = "Toggle",
			Key = "BookReadStatus",
			DisplayName = "人物书籍阅读状态",
			Description = "查看人物背包书籍时显示该人物的阅读完成状态",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "AutoFillCraftMaterial",
			DisplayName = "制造自动填充资源",
			Description = "制造装备选好材料后自动填满资源",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "AutoBreakSelect",
			DisplayName = "突破自动选择总纲",
			Description = "突破功法时自动选择总纲和正逆练篇章（总纲随机选已读的，篇章优先正练）",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "AutoHealButton",
			DisplayName = "突破界面疗伤按钮",
			Description = "在实际突破（走格子）界面加一个疗伤按钮，主角有伤可治时可点",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "MapCharHoverInteraction",
			DisplayName = "地块NPC悬停默认显示互动",
			Description = "在地图地块的NPC列表上悬停时，默认直接显示互动信息（原版需按住Shift），按Alt看详细信息",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "FlatEquipTabs",
			DisplayName = "扁平标签栏（需要重启）",
			Description = "角色界面底部标签栏展开为单排扁平标签，去掉所有二级悬停菜单",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "EquipDetailDefault",
			DisplayName = "装备浮窗优化",
			Description = "背包悬停装备时默认显示全部详情（原版需按住Alt），隐藏热键提示，注解面板改为纵向排列",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "NeiliShock",
			DisplayName = "战斗准备内力震慑",
			Description = "战斗准备界面加内力震慑按钮，主角精纯高于对方时可扣内力（敌方现有真气/10）直接战斗胜利（走正常结算）",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "PopulationClick",
			DisplayName = "点击村名人数打开名册",
			Description = "点击右上角村名人数或图标直接打开村民名册",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "MapBlockCharShortcut",
			DisplayName = "地块人物列表快捷键",
			Description = "在地块左侧人物列表中按数字键 1-9 可直接对话对应角色",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "MaterialTipHint",
			DisplayName = "心材已建数提示",
			Description = "悬停心材类物品时，在浮窗属性网格额外显示太吾村中需要该心材的已建造建筑数（含0）",
			DefaultValue = true,
		},
		{
			SettingType = "Toggle",
			Key = "BorrowItem",
			DisplayName = "物品借用",
			Description = "书籍/装备操作菜单的转赠下方新增「借用」选项，借给队友不增加好感度和警觉度，取回借出物也不减少好感度",
			DefaultValue = true,
		},
		},
	Version = "1.0.0.9",
	TagList = {
		[1] = "Optimizations",
	},
	WorkshopCover = "Cover.jpg",
	Visibility = 0,
	ChangeConfig = false,
	HasArchive = false,
	NeedRestartWhenSettingChanged = false
}
