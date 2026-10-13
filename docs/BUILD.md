# 构建与发布说明

本文件记录本机（D:\work\DSH）验证过的构建方法，以及踩过的坑。

## 环境事实（重要）

| 项目 | 情况 |
|---|---|
| .NET SDK | 需要 **10.0.101**（Dalamud API 15 要求 `net10.0-windows`），已放在 `D:\work\DSH\_setup\dotnet` |
| Git | 已解压便携版 MinGit 2.56.0 于 `D:\work\DSH\_setup\git\cmd\git.exe` |
| Dalamud 引用库 | 已下载官方开发包并解压于 `D:\work\DSH\_setup\dalamud-dev` |
| NuGet 包缓存 | `D:\work\DSH\_setup\nuget` |

### ⚠️ 本机 TLS 故障

本机 **.NET 与 PowerShell 的 TLS 全部失效**：

```
AuthenticationException: Authentication failed
Win32Exception: 安全包中没有可用的凭证 (SEC_E_NO_CREDENTIALS)
```

`curl` 同样报 `schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS`。

**但 Node 与 Python 的 TLS 正常**（各自带 OpenSSL）。因此：

- 下载依赖一律用 `python D:\work\DSH\_setup\fetch.py <输出> <URL...>`（支持断点续传与多镜像）。
- 在 DSH 沙箱内运行 `dotnet restore` 也会因为该沙箱限制而失败，需要放开文件沙箱后执行。

其他已知问题：
- `winget` 是 0 字节的 App Execution Alias，**不可用**。
- `raw.githubusercontent.com` 被 DNS 屏蔽，需走 `cdn.jsdelivr.net` / `ghproxy.net` / `codeload.github.com`。

## 编译

所有 .NET 状态都需要重定向到工作区，否则会去写 `C:\Users\mingke\.dotnet` 等沙箱外路径：

```powershell
$env:NUGET_PACKAGES          = "D:\work\DSH\_setup\nuget"
$env:NUGET_HTTP_CACHE_PATH   = "D:\work\DSH\_setup\httpcache"
$env:DOTNET_CLI_HOME         = "D:\work\DSH\_setup\clihome"
$env:APPDATA                 = "D:\work\DSH\_setup\appdata"
$env:LOCALAPPDATA            = "D:\work\DSH\_setup\localappdata"
$env:TEMP                    = "D:\work\DSH\_setup\temp"
$env:TMP                     = "D:\work\DSH\_setup\temp"
$env:DALAMUD_HOME            = "D:\work\DSH\_setup\dalamud-dev"

cd D:\work\DSH\FF14AutoTreasure\AutoTreasure
& "D:\work\DSH\_setup\dotnet\dotnet.exe" build -c Release
```

产物：`AutoTreasure\bin\Release\AutoTreasure\latest.zip`

## 踩过的坑

1. **csproj 中文乱码**：MSBuild 默认按系统 ANSI 代码页读取 csproj，导致清单里中文变成
   `鑷姩鎸栧疂`。**必须给 csproj 加 UTF-8 BOM**，否则游戏内插件名会乱码。
2. **`ReadOnlySpan` 不能跨 `yield`**：`IGameInventory.GetInventoryItems` 返回
   `ReadOnlySpan<GameInventoryItem>`，写成迭代器会编译报 CS4007。已改为返回 `List`。
3. **命名空间**（容易记错）：
   - `[PluginService]` → `Dalamud.IoC`
   - 所有 `I*` 服务接口 → `Dalamud.Plugin.Services`
   - `IDtrBarEntry` → **`Dalamud.Game.Gui.Dtr`**（不在 Plugin.Services）
   - `GameInventoryItem` / `GameInventoryType` → `Dalamud.Game.Inventory`
   - ImGui → **`Dalamud.Bindings.ImGui`**（API 13 起，不再是 `ImGuiNET`）
4. **Lumina 的 Item 表没有 `IsMarketable`**：可交易 = `!IsUntradable`。
   价格用 `PriceMid`，品质用 `Rarity`。
5. **ImGui 的 `InputInt` 只接受 `ref int`**，配置里的 `long` 字段需要中间变量转换。
6. **丢弃物品不在 Dalamud 公开 API 中**，必须用 FFXIVClientStructs：
   ```csharp
   var mgr = InventoryManager.Instance();
   mgr->DiscardItem((InventoryType)(uint)item.ContainerType, (ushort)item.InventorySlot);
   ```
   两个枚举的数值完全一致，可直接强转。
