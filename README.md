# FF14 自动挖宝助手 (AutoTreasure)

面向 **国服（卫月 / XIVLauncherCN）** 的 FFXIV 挖宝流程自动化插件。

本仓库实现了需求思维导图中的 **阶段 1 / 2 / 6 / 7**；
阶段 3（寻路）、4（战斗）、5（副本）**尚未实现**，界面上会明确标注，不会假装完成。

---

## 当前实现范围

| 阶段 | 内容 | 状态 |
|---|---|---|
| 1 买图 | 金币余额检查、地图库存统计、补货判断 | ✅ 已实现（购买动作待接入板子 NPC 交互） |
| 2 读图 | 地图物品配置、背包 / 陆行鸟背包分仓统计、队列计数 | ✅ 统计与判定已实现（读图动作待接入） |
| 3 寻路挖宝 | 传送、寻路、挖掘 | ❌ 未实现 |
| 4 开箱战斗 | 开箱、战斗循环、拾取 | ❌ 未实现 |
| 5 结果分支 | 进副本 / 下一张 | ❌ 未实现 |
| 6 丢弃垃圾 | 黑名单 / 白名单 / 售价 / 品质判定、背包满触发、单次上限 | ✅ 已实现（含真实丢弃调用） |
| 7 总控 | 循环开关、紧急暂停热键、日志、收益统计、拟人化间隔 | ✅ 已实现 |

> **重要**：阶段 6 的「真正删除物品」通过 FFXIVClientStructs 调用游戏自身的
> `InventoryManager.DiscardItem` 实现。这是唯一一处越过 Dalamud 公开 API 的地方，
> 属于内存调用，游戏版本更新后可能失效。

---

## 安装

### 方式一：从 Release 安装（推荐）

1. 打开游戏，输入 `/xlsettings`。
2. 切到「测试版」页签，在 **自定义插件仓库** 里填入：
   ```
   https://raw.githubusercontent.com/mingke/FF14AutoTreasure/main/pluginmaster.json
   ```
3. 输入 `/xlplugins`，在「全部插件」里搜索 **自动挖宝** 并安装。

### 方式二：本地开发安装

把编译产物目录加入 Dalamud 的开发插件目录，或直接在游戏内 `/xldev` 中指定。

---

## 使用

游戏内命令：

| 命令 | 作用 |
|---|---|
| `/at` 或 `/autotreasure` | 打开主窗口 |
| `/at on` | 开始自动循环 |
| `/at off` | 停止循环 |
| `/at panic` | 紧急停止 |
| `/at clear` | 清空日志 |

**紧急暂停热键**默认 `F12`，可在设置中修改虚拟键码。

首次使用建议按顺序：
1. 在「设置 → 买图/读图」中 **添加你的地图物品 ID**（这是所有库存统计的基础）。
2. 在「设置 → 丢弃垃圾」中配置黑白名单与判定规则。
3. 回到主窗口点「开始循环」，观察日志确认行为符合预期。

---

## 构建

需要 **.NET SDK 10.0.101**（Dalamud API 15 要求 `net10.0-windows`）。

```bash
cd AutoTreasure
dotnet build -c Release
```

`Dalamud.NET.Sdk` 需要能找到一个 Dalamud 安装来引用其程序集，按以下顺序解析：

1. 环境变量 `DALAMUD_HOME`
2. 默认路径 `%APPDATA%\XIVLauncher\addon\Hooks\dev\`（国服为 XIVLauncherCN 时路径可能不同）

若本机没装卫月，可手动下载 Dalamud 开发包并设置 `DALAMUD_HOME`：

```powershell
$env:DALAMUD_HOME = "C:\path\to\dalamud-dev"
dotnet build -c Release
```

产物位于 `AutoTreasure/bin/Release/AutoTreasure/latest.zip`。

---

## 发布新版本（游戏内自动更新）

1. 修改 `AutoTreasure/AutoTreasure.csproj` 里的 `<Version>`。
2. 打 tag 并推送：
   ```bash
   git tag v0.2.0.0
   git push origin v0.2.0.0
   ```
3. GitHub Actions 会自动构建并把 `latest.zip` 附到 Release。
4. 更新仓库根目录的 `pluginmaster.json` 中的 `AssemblyVersion`、`LastUpdate`
   与下载链接（链接指向 `releases/latest/download/latest.zip`，通常无需改动）。
5. 玩家在 `/xlplugins` 中点更新即可。

---

## 免责声明

本插件通过模拟操作与内存调用实现自动化。使用自动化工具可能违反游戏服务条款，
存在账号风险。请自行评估并承担后果。作者不对任何账号处罚负责。

请优先在**小号**上测试。
