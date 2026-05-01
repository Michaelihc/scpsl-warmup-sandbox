# SCP:SL Warmup Sandbox

[English README](README.md)

> **开发警告：** 这个插件仍在积极开发中。可能会有粗糙之处、行为变化，以及偶发的机器人或寻路问题。请先在私人服务器测试，再给玩家使用。

`ScpslPluginStarter` 是一个 LabAPI 插件，用来在 SCP:SL 服务器里运行热身沙盒。它可以管理假人机器人、玩家出生预设、可选 Dust2 场地、爆破模式、运行时调参命令，以及中英文帮助文本。

机器人相关实现部分参考了较旧的 SwiftNPCs/SwiftAPI 项目的思路，但本插件是面向当前热身沙盒用途的独立实现。

## 功能

- 根据配置在回合开始、首名玩家加入或等待玩家阶段自动启动热身。
- 自动生成并维护指定数量的假人机器人。
- 没有真人玩家在线时，可自动把机器人数量恢复到空服默认值。
- 支持运行时修改机器人数量、难度、AI 模式、地图模式、SCP 速度和近距离后退倍率。
- 玩家可以用 `loadout`、`ld` 或 `kit` 选择出生预设，也可以原地临时切换为 SCP 练习角色。
- 面向玩家的 `.help`、`.bots setcount <数量>` 和 `.ra` 命令带冷却限制。
- 受限玩家 Remote Admin 窗口只允许 `forcerole`、`bring`、`goto` 和 `give`。
- 支持职业默认装备，也支持完全自定义装备和备用弹药。
- 对职业默认装备也会按当前枪械弹药类型补充备用弹药，包括 9x19。
- 使用 SCP:SL 原生回合出生保护。
- 可选 ProjectMER Dust2 场地。
- 可选爆破模式，包含安放和拆除流程。
- 可选 Surface 区域运行时 NavMesh，随机 Facility 默认保持关闭。
- 通过 `bots language <en|cn>` 切换中英文帮助文本。

## 需求

- Windows 主机。
- Steam 安装的 SCP:SL Dedicated Server。兼容 SCP:SL 14.2.6。
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

脚本默认不会覆盖已有的线上插件配置。

### 中文公测服推荐配置

如果要直接开中文公测服，可以让脚本顺手配置 SCP:SL 原生服务器标题和人数上限：

```bat
scripts\host-warmup-server.bat --configure-cn-public --start
```

这会修改：

```text
%AppData%\SCP Secret Laboratory\config\7777\config_gameplay.txt
```

推荐值：

```yaml
server_name: [CN] [公测] 人机战斗服
player_list_title: [CN] [公测] 人机战斗服
max_players: 50
```

服务器简介建议使用非技术描述，让玩家一眼看懂这里能做什么。模板在：

```text
docs\server-description.zh-CN.txt
```

把模板内容发布到 Pastebin 后，把 Pastebin ID 填到原生配置：

```yaml
serverinfo_pastebin_id: 你的PastebinID
```

也可以让脚本一起写入：

```bat
scripts\host-warmup-server.bat --configure-cn-public --server-info-id 你的PastebinID --start
```

简介重点：

- 这是人机练枪和热身服务器。
- 服务器会自动生成机器人。
- 所有玩家都有受限管理面板，可临时切角色、传送和发物品。
- 输入 `.help` 查看玩家命令。
- 服务器仍在公测开发中。

脚本会先创建一次备份：

```text
config_gameplay.txt.warmup-cn-public-backup
```

只配置原生服务器信息、不构建插件时，也可以单独运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\configure-cn-public-server.ps1 -Port 7777
```

空服时建议保留 5 个机器人，让新玩家进服后马上能练枪：

```yaml
max_bot_count: 30
reset_bot_count_when_no_active_players: true
no_active_players_bot_count: 5
no_active_players_bot_reset_delay_ms: 3000
```

`max_bot_count` 会限制命令和配置里的机器人数量，避免玩家把机器人刷得太多。空服重置只改运行时机器人数量，不会把玩家用 `.bots setcount` 改过的数值写回配置文件。下一次有人进服后仍可重新用命令调整。

## 查看在线人数

通过 Steam 查询协议轮询服务器人数：

```bat
scripts\watch-player-count.bat
```

只查询一次并退出：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\watch-player-count.ps1 -Once
```

修改地址或刷新间隔：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\watch-player-count.ps1 -HostName 60.205.222.32 -Port 7777 -IntervalSeconds 5
```

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
.help
```

玩家出生预设：

```text
loadout
loadout <编号|预设|角色>
loadout <173|939|106|049|3114|096>
```

别名：

```text
ld
kit
```

面向玩家的命令：

```text
.help
.loadout
.loadout <编号|名称>
.loadout <173|939|106|049|3114|096>
.bots setcount <数量>
.ra
```

临时 SCP 练习角色会在当前位置切换，清空物品和弹药，并且不会变成你的永久出生预设。死亡后会恢复到上一次选择的人类预设。

`.bots setcount <数量>` 允许玩家修改机器人数量，默认有 60 秒全局冷却，以及每名玩家 3 分钟加 0-60 秒随机时间的个人冷却。

`.ra` 会打开一个短时间受限 Remote Admin 窗口。非管理员玩家只能使用 `forcerole`、`bring`、`goto` 和 `give`，其他 RA 命令会被拦截。默认窗口为 20 秒，结束后进入全局冷却和个人冷却。如果修改 RA 窗口时长，个人冷却会按配置中的 20 秒基准等比例缩放。

## 配置

示例配置：

```text
ScpslPluginStarter\config.yml
```

重要顶层字段：

- `language`
- `bot_count`
- `max_bot_count`
- `reset_bot_count_when_no_active_players`
- `no_active_players_bot_count`
- `no_active_players_bot_reset_delay_ms`
- `difficulty_preset`
- `human_role`
- `bot_role`
- `use_bot_role_default_loadout`
- `enable_spawn_protection`
- `auto_cleanup_enabled`
- `auto_cleanup_interval_seconds`
- `player_bot_count_global_cooldown_seconds`
- `player_bot_count_cooldown_seconds`
- `player_bot_count_cooldown_jitter_seconds`
- `limited_remote_admin_enabled`
- `limited_remote_admin_use_window_seconds`
- `limited_remote_admin_global_cooldown_seconds`
- `limited_remote_admin_cooldown_seconds`
- `limited_remote_admin_cooldown_jitter_seconds`
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
