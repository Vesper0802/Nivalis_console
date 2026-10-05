# 给接手开发的 agent

《Nivalis Nights》的 BepInEx 6 IL2CPP 控制台插件。这份文档记录的是**踩过的坑和已经查实的结论**，目的是让你不必重新发现它们。代码的用法见 `README.md`，命令说明见 `reference/commands.txt`。

## 现状

- 插件版本 0.6.0，**53 条命令**，已发布 Release v0.6.0（dll 的 SHA256 与当前 `main` 的构建产物一致）
- 游戏 Steam AppID `1488490`，Unity 2020.3.44f1，IL2CPP x64
- BepInEx **固定在 `6.0.0-be.697`**（commit `53625800`）。IL2CPP 这条线是预发布，interop API 在变，插件和 BepInEx 版本不一致的表现是「插件静默不加载」，极难排查。所以 `scripts/install.ps1` 里写死了这个版本，不要改成「取最新」
- 仓库是公开的，**文档里不能出现任何存档专属数据**（见下面「公开仓库的约束」）

## 工作循环

```powershell
.\scripts\deploy-and-run.ps1          # 关游戏 → 构建 → 部署 → 重启
```

**部署前必须先关游戏，而且不要不问就去杀用户的游戏进程。** BepInEx 只在启动时加载插件，运行中的游戏还锁着 dll。先问用户，让他自己关或者同意你关。

改完代码后的验证纪律：**不要相信命令自己的报告，去读日志。**

```powershell
$g = "C:\Program Files (x86)\Steam\steamapps\common\Nivalis Nights"
Get-Content "$g\BepInEx\LogOutput.log" -Encoding utf8
```

这条纪律是有代价换来的：`clearitems` 曾经打印「没有携带任何剧情物品」然后销毁了用户的任务物品（原因见下）。现在它清完后会逐个向游戏核对并打 `WARNING`。凡是涉及删除的命令，都要在事后把状态从游戏里读回来验证，而不是凭自己的记账下结论。

## 查游戏 API 的方法

interop 程序集没有方法体，只能看 API 形状。`tools/MetaDump` 从里面导出类型签名：

```powershell
dotnet run --project tools\MetaDump -- `
  "C:\Program Files (x86)\Steam\steamapps\common\Nivalis Nights\BepInEx\interop\Assembly-CSharp.dll" `
  Venue Property > "$env:TEMP\nn.txt"
```

传空字符串或通用词可以导出全部类型（约 97000 行），存一份当索引用，比反复跑快得多。

## Il2CppInterop 的坑（最重要的一节）

**每次访问同一个原生对象，interop 都会给你一个新的托管包装对象。** 所以：

```csharp
// 错的：永远匹配不上，因为两个包装是不同的 .NET 引用
var map = new Dictionary<ItemType, string>();
if (map.ContainsKey(itemType)) { ... }

// 对的：用稳定的值做键
var map = new Dictionary<string, string>();
if (map.ContainsKey(GameRefs.Guid(itemType))) { ... }
```

这就是 `clearitems` 销毁任务物品的根因——它用 `Dictionary<ItemType, T>` 查保护表，查询永远失败，于是「什么都没保护」却报告成功。`src/NivalisDevUnlock/StoryItems.cs` 里的 `Key()` 是现在的正确做法。**任何身份比较都必须走 `Guid`，或者交给游戏自己去判断（比如 `GetItemCount(type)`）。**

其他几条：

- **接口被建模成类**，要 `TryCast<T>()`：`player.TryCast<IPropertyOwner>()`
- **泛型集合的枚举器曾经把进程搞崩**。按 key 去探测容器是安全的，`foreach` 遍历 interop 集合要谨慎。`Spoiled.cs` 和 `StoryItems.cs` 都是逐个物品类型按 key 探测，而不是遍历容器
- **底层 list 用下标访问而不是迭代**（`stack._instanceData[i]`）
- 调游戏的原生方法前先用 `Interop.HasNativeMethod()` 确认绑定成功，否则在某些版本上会静默返回 0
- `Resources.FindObjectsOfTypeAll(Il2CppType.Of<BaseProperty>())` 能拿到所有派生实例，是扫描 ScriptableObject 的通用手段

## 已查实的游戏机制

不要重新推导这些，都是读代码或实测确认过的。

**店铺储物是两个互不相通的容器。** `VenueAreaGhost.JointInventory` 下是 `FridgeInventory`（冷藏）和 `CupboardInventory`（常温），**各自用自己的 `NormalCapacity`**，所以两者的 `coldCap` 都读作 0。两边容量不能相加。食材归哪边看 `ItemType.RequiresRefridgeration`。容量来自摆放家具的 `basicStorage` / `refridgeratedStorage` 累加，升级只是解锁更多摆放位。

**`ShoppingListManager.demandAmount` 是静态字段，值为 10**——每种食材的目标库存。容量 ÷ 10 = 能支撑的食材种数。

**`ItemContainer.TryAdd(Il2CppReferenceArray<ItemTypeAmount>)` 是 void 且全有或全无**，一批放不下就一个都不放。所以每个品类单独调一次并裁剪到剩余空间，而且要在调用前后测量容器来判断实际放进去多少。其他重载返回 `ItemCollectionOperationResult`。

**剧情物品没有任何标记。** `ItemType` 上没有 quest 字段。唯一的判据是 `Nivalis.Dialogue.ArticyGlobalInventoryLinker`，它持有 `Links`（`List<VariableItemPair>`，即 Articy 变量名 ↔ 物品类型），并注册了 `OnPlayerInventoryChanged`——**背包里少了物品，剧情变量会被同步改写**。好消息是这个联动是双向的：物品加回去变量也会置回，所以误删在存档前可以救。

**许可是具体物品，不是等级门槛。** `BaseProperty.requiredPermit` 指向某一个 `BusinessPermit`，拿着 4 级也买不了要求 2 级的店。而且 **`requiredPermit` 和 `isAcquireable` 是两套独立开关**：75 家 NPC 固定经营的店铺同样写着 `requiredPermit`，但永远拿不到。140 项房产里只有 14 间店铺可收购。

**`PropertyManager.GetBuyPrice()` 是实时估值，不是固定标价。** 随店内家具和库存变动，两次运行数字就不一样。资源上的 `BuyCost` 字段给所有可收购店铺写的是同一个数，像占位值。`RentCost` 相反，是写死的。所以**售价不能写进文档**。

**人群构成有两个来源，`DEMOSOURCE` 会说明是哪个。** `venue.DemographicPriority`（若已 authored，**每个人群权重都算 1.0**）优先于 `location.groups`（真实占比）。这曾经被误判成「数据过时」——两次导出对同一地点给出不同人群构成，实际是走了不同来源，两次都对。`reference/recipe-scores.txt` 按地点算，对有 authored priority 的店铺不适用。

**`dumpmenus` 的前四段是静态数据，与存档无关**（`## ALL VENUES` / `## RECIPES` / `## RECIPE SLOTS` / `## LOCATIONS`），只有 `## OWNED VENUES` 取决于拥有什么。`## LOCATIONS` 是后加的，对全部 18 个地点算全部 193 道菜的评分，所以不需要拥有店铺也能做菜单规划。

**`Venue` 的中文名走 `locObjRef.Obj.displayName`（`LocItemPlain`），地点走 `Venue.Location.DisplayName`（`WorldLocation`）。** 只有店铺有 `Location`。`Venue.OwnershipType` 读的是 authored 默认值，判断归属要用 `PlayerOwned` 或 `player.IsOwningVenue`。

## 环境坑（Windows / PowerShell 5.1）

- **游戏残留进程**：进程关闭后可能留下 0 线程、0 句柄、几十 KB 内存的空壳，但**仍然锁着它加载过的 dll**（映射镜像是 section 引用而非句柄，所以 0 句柄和文件被锁是自洽的）。`Stop-Process` / `taskkill /F` 都没用，只有重启能清。两个脚本都用「把锁住的文件改名挪开」来绕过。**重新生成 interop 之前必须先重启**，否则 `Assembly-CSharp.dll` 被锁会直接卡住
- **PowerShell 没有 heredoc，`&&` 也不是合法分隔符**。多行 git commit message 要写进文件再 `git commit -F`
- **不要用 `Set-Content` 写中文**，会损坏编码。用 Write / StrReplace 工具
- `Out-File -Encoding utf8` 在 5.1 会写 BOM，Python 读的时候要用 `encoding="utf-8-sig"`
- PowerShell 控制台会把 UTF-8 中文显示成乱码。要读含中文的输出，先 `[System.IO.File]::WriteAllLines(path, $out, (New-Object System.Text.UTF8Encoding $false))` 写文件，再用 Read 工具读
- `Invoke-WebRequest` 要带 `-UseBasicParsing`，并且先设 `[Net.ServicePointManager]::SecurityProtocol = 'Tls12'`
- 推送到 `main` 是受保护的，会被拦一次，需要带原始拦截理由重试

## 公开仓库的约束

仓库是公开的，`reference/` 下的文档**不能包含存档专属数据**。已经清理过一轮，判断标准：

- 不能写的：售价（`GetBuyPrice` 是实时估值）、「已拥有的 N 家店」、某个存档的物品数量、具体店铺的推荐菜单、你的等级和储物容量
- 可以写的：游戏的固定数据（店名、地点、许可、`RentCost`、菜谱评分、人群构成、物品代号、变量名）、机制说明、API 结构

⚠️ **`dumpitems` 导出的房产行带 `yours` / `have-permit` 标记**，反映的是运行者的存档。当前提交的 `reference/items.txt` 是旧格式快照、不含房产段，所以没有泄漏；但**重新生成后再提交就会把进度带进仓库**。

## 发布流程

游戏更新后需要重新发 Release，否则安装脚本装的还是旧 dll：

```powershell
dotnet build src\NivalisDevUnlock\NivalisDevUnlock.csproj -c Release
# 把 src\NivalisDevUnlock\bin\Release\net6.0\NivalisDevUnlock.dll 传到新 tag 的 Release
```

`scripts/install.ps1` 通过 GitHub API 取 `releases/latest` 的 `NivalisDevUnlock.dll`，资源名必须叫这个。注意 CI 构建不可行——`csproj` 引用的是 BepInEx 在本机生成的 interop 程序集，那些文件不在仓库里。

## 待办与未验证项

**待办**

- 残留进程会随启动次数累积（用一次 `Get-Process -Name 'Nivalis Nights'` 看当前有几个）。重新生成 interop 前先重启，否则会被锁住
- `scripts/install.ps1` 的**全新安装分支从未实测**（会清掉现有 interop 所以没在用户机器上测）。压缩包结构单独验证过正确（顶层是 `winhttp.dll` / `doorstop_config.ini` / `BepInEx\` / `dotnet\`），逻辑也没问题，但「干净机器上全程跑通」还没被证明
- 提过但用户还没定的改进：安装脚本自动处理过期的 interop（检测到生成失败就把 `BepInEx\interop` 改名并重启游戏重新生成）。这样游戏更新后用户只需重跑安装命令，不必手动改文件夹名

**未验证，不要当成事实**

- `GetBuyPrice` 看起来是「当前估值」（已拥有的店报价远高于条件相近的空店，家具最多的最贵），但公式没读过
- **租金的结算周期（每天还是每周）不知道**，所以买与租的回本估算不可靠
- `buylist` 按的是 `BaseMarketPrice`，不是各商人的实时价格
- 员工技能只能通过玩家接口设定，`AgentGhost.GetSkill<T>()` 没探索过
- `devmenu` 从未验证过能真的渲染出来
- `reference/savesystem.txt` 只是设计笔记，没有可用的存档编辑器
- `Spoiled` 用 `GetStack(type)`，每个类型只返回一个 stack，但 `MultiDictionary` 暗示同一类型可能有多个 stack，所以一次可能清不干净（已通过打印清理前后的 `ItemCount` 缓解）
