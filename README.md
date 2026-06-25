# 综合调整

太吾绘卷（游戏版本 `1.0.20.x`）MOD，综合调整合集。

## 功能

| 功能 | 默认 | 说明 |
| --- | --- | --- |
| **NPC书籍阅读状态** | ✅ 开 | 查看 NPC 背包书籍时，显示该 NPC 的阅读完成状态（每页追加「此人已读/此人未读」）。原版只显示太吾自己的进度。 |
| **制造自动填充材料** | ✅ 开 | 制造装备选好材料后，自动按规则填满资源：低上限材料（木材/金铁/织物）各填到上限，剩余额度全投玉石。 |
| **突破自动选择** | ✅ 开 | 突破功法时自动选择总纲（随机一个已读的）和正逆练篇章（优先正练）。总纲没读则不处理；已手动选的不覆盖。 |
| **调试模式** | ❌ 关 | 开启后输出详细运行日志到 `Player.log`，用于排查问题。 |

三个功能都可在游戏内 **MOD 管理器 → 综合调整 → 设置** 里独立开关，改完即时生效，无需重启。

## 安装

把 `Adjust` 整个目录放到游戏目录的 `Mod/` 下：

```
<游戏目录>/Mod/Adjust/
├── Config.lua
├── Settings.Lua
└── Plugins/
    ├── AdjustMod.dll          # 前端插件（Unity 侧）
    └── AdjustModBackend.dll   # 后端插件（GameData 侧）
```

启动游戏，在 MOD 管理器里启用「综合调整」即可。

## 构建（开发者）

```powershell
cd mods/Adjust
dotnet build AdjustMod.slnx
```

构建成功后会**自动部署**到 `<游戏目录>\Mod\Adjust\`（由 `AdjustMod.props` 的 `TaiwuDeployToGame` target 完成，复制 Config.lua + 编译产物 dll）。换机器只改 `AdjustMod.props` 里的 `<GameDir>`。

> 注意：部署**不复制 `Settings.Lua`**（避免覆盖玩家设置），它由游戏首次加载时自动生成。

## 项目结构

```
Adjust/
├── Config.lua               # MOD 元信息 + 3 个设置开关（源文件）
├── Settings.Lua             # 玩家设置值（源码保持 return {}，绝不部署）
├── AdjustMod.props          # 共享 MSBuild 属性 + 自动部署 target
├── AdjustMod.slnx           # 解决方案（前端 + 后端）
├── AdjustMod/               # 前端插件（netstandard2.1）：UI、Harmony patch、设置读取
│   └── AdjustModPlugin.cs
└── AdjustModBackend/        # 后端插件（net8.0）：游戏数据查询（NPC 阅读状态）
    └── AdjustModBackendPlugin.cs
```

## 技术文档

通用 MOD 开发文档见仓库根 [docs/](../docs/README.md)。本 MOD 涉及的实战案例：

- [docs/cases/背包书籍NPC阅读状态.md](../docs/cases/背包书籍NPC阅读状态.md) — Tooltip 浮窗系统架构 + 两个 patch 点数据流
- [docs/cases/自动填充制造材料.md](../docs/cases/自动填充制造材料.md) — MakeSubPageMake 制造面板数据流 + ResourceInts struct 写回坑
- [docs/cases/制造UI重构调试经验.md](../docs/cases/制造UI重构调试经验.md) — 定位「patch 挂上但零触发」的调试经验
