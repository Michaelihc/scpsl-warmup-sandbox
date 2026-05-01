# SCP:SL Warmup Sandbox

[English README](README.md)

> **开发警告：** 这个插件仍在积极开发中。可能会有粗糙之处、行为变化，以及偶发的机器人或寻路问题。请先在私人服务器测试，再给玩家使用。

`ScpslPluginStarter` 是一个 LabAPI 插件，用来在 SCP:SL 服务器里运行热身沙盒。它可以管理假人机器人、玩家出生预设、可选 Dust2 场地、爆破模式、运行时调参命令，以及中英文帮助文本。

## 功能

- 根据配置在回合开始、首名玩家加入或等待玩家阶段自动启动热身。
- 自动生成并维护指定数量的假人机器人。
- 支持运行时修改机器人数量、难度、AI 模式、地图模式、SCP 速度和近距离后退倍率。
- 玩家可以用 `loadout`、`ld` 或 `kit` 选择出生预设。
- 支持职业默认装备，也支持完全自定义装备和备用弹药。
- 对职业默认装备也会按当前枪械弹药类型补充备用弹药，包括 9x19。
- 使用 SCP:SL 原生回合出生保护。
- 可选 ProjectMER Dust2 场地。
- 可选爆破模式，包含安放和拆除流程。
- 可选 Surface 区域运行时 NavMesh，随机 Facility 默认保持关闭。
- 通过 `bots language <en|cn>` 切换中英文帮助文本。

## 需求

- Windows 主机。
- Steam 安装的 SCP:SL Dedicated Server。
- 服务器已安装 LabAPI。
- 可构建 `net48` 项目的 .NET SDK。
- 只有启用 Dust2 场地时才需要 ProjectMER。

默认服务器路径：

```text
C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server
```

如果服务器安装在其他目录，请修改 `ScpslPluginStarter/ScpslPluginStarter.csproj` 里的 `ScpslServerManagedDir`。

## 快速托管

在仓库根目录运行：

```bat
scripts\host-warmup-server.bat
```

脚本会：

- 构建插件
- 通过项目构建目标部署 DLL
- 如果没有现有配置，则复制示例配置
- 可选启动 `LocalAdmin.exe`

构建并启动服务器：

```bat
scripts\host-warmup-server.bat --start
```

指定服务器路径：

```bat
scripts\host-warmup-server.bat --server "D:\Servers\SCP Secret Laboratory Dedicated Server" --start
```

指定配置端口：

```bat
scripts\host-warmup-server.bat --port 7778
```

脚本不会覆盖已有的线上配置。

## 手动构建

```powershell
dotnet build .\ScpslPluginStarter\ScpslPluginStarter.csproj
```

构建后项目会复制：

- 插件 DLL 到 `%AppData%\SCP Secret Laboratory\LabAPI\plugins\global`
- Dust2 schematic 到 `%AppData%\SCP Secret Laboratory\LabAPI\configs\ProjectMER\Schematics\de_dust2`

常见线上配置路径：

```text
%AppData%\SCP Secret Laboratory\LabAPI\configs\7777\WarmupSandbox\config.yml
```

如果 LabAPI 还没有生成配置，可以把 `ScpslPluginStarter\config.yml` 复制到该目录。

## 语言切换

配置文件：

```yaml
language: cn
```

运行时命令：

```text
bots language cn
bots language en
```

当前本地化覆盖运行时命令说明、`modhelp`、`loadout` 菜单、常见错误提示和主要状态反馈。没有本地化的日志仍保留英文，便于调试和搜索源码。

## 常用命令

主命令别名：

```text
bots
bot
warmup
ws
warmupsandbox
```

常用命令：

```text
bots status
bots start
bots restart
bots roundrestart
bots stop
bots save
bots set <数量>
bots setcount <数量>
bots difficulty <easy|normal|hard|hardest>
bots aimode <classic|realistic>
bots language <en|cn>
bots map <bomb|standard|true|false>
bots setretreatspeed <倍率>
bots set retreatspeed <倍率>
```

SCP Facility 跟随速度：

```text
bots setspeed <速度>
bots set939speed <速度>
bots set3114speed <速度>
bots set049speed <速度>
bots set106speed <速度>
```

通用设置：

```text
bots set <键> <值>
```

示例：

```text
bots set 2
bots difficulty hard
bots aimode realistic
bots language cn
bots setretreatspeed 0.92
bots set939speed 7.5
bots map bomb
```

帮助：

```text
bots
modhelp
```

玩家出生预设：

```text
loadout
loadout <编号|预设|角色>
```

别名：

```text
ld
kit
```

## 配置

示例配置：

```text
ScpslPluginStarter\config.yml
```

重要顶层字段：

- `language`
- `bot_count`
- `difficulty_preset`
- `human_role`
- `bot_role`
- `use_bot_role_default_loadout`
- `enable_spawn_protection`
- `dust2_map`
- `bot_behavior`

重要 `bot_behavior` 字段：

- `ai_mode`
- `preferred_range`
- `range_tolerance`
- `orbit_retreat_distance`
- `close_retreat_speed_scale`
- `enable_adaptive_close_range_retreat`
- `facility_dummy_follow_speed`
- `facility_dummy_follow_speed_scp939`
- `facility_dummy_follow_speed_scp3114`
- `facility_dummy_follow_speed_scp049`
- `facility_dummy_follow_speed_scp106`
- `nav_debug_logging`
- `realistic_los_debug_logging`

## 难度

运行时修改难度：

```text
bots difficulty <easy|normal|hard|hardest>
```

较高难度使用更快的思考节奏，并更快提升瞄准追踪精度。较低难度主要降低追踪精度随时间增长的速度，而不是简单降低整个 AI 的刷新频率。

## 装备和弹药

自定义装备示例：

```yaml
items:
- GunCrossvec
- ArmorLight
- Medkit
ammo:
- type: Ammo9x19
  amount: 240
```

如果使用职业默认装备，插件会根据当前装备枪械的实际 `AmmoType` 补充备用弹药。如果配置里没有显式弹药项，会默认补到 240 发。

## Dust2 和爆破模式

Dust2 运行时依赖 ProjectMER。插件不会在编译期引用 ProjectMER，而是在运行时用反射查找加载器。

启用 Dust2：

```text
bots map true
```

启用爆破模式：

```text
bots map bomb
```

返回普通 Facility 热身：

```text
bots map standard
```

## 调试

默认关闭大部分日志。只打开需要的日志项：

- `enable_debug_logging`
- `enable_verbose_bot_logging`
- `enable_attachment_logging`
- `enable_arena_logging`
- `enable_zoom_logging`
- `bot_behavior.nav_debug_logging`
- `bot_behavior.realistic_los_debug_logging`

常见日志标签：

- `[BotCombat:...]`
- `[BotAim:...]`
- `[BotNav:...]`
- `[AttachmentDebug]`
- `[WarmupSandbox]`
