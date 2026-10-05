# Nivalis Nights 控制台插件

给《Nivalis Nights》加一个游戏内控制台，按 `F1` 打开。53 条命令，大部分是游戏自带
开发选项的包装，少数是自制功能（技能设定、冻结腐败、腐坏食材清理、剧情物品保护等）。

仓库里还有一批逆向整理出来的参考资料：全部物品代号、全部剧情变量、按地点的菜谱评分、
存档结构、游戏内置开发选项清单。即使不装插件，这些表也能单独查。

![](https://img.shields.io/badge/BepInEx-6%20IL2CPP-blue) ![](https://img.shields.io/badge/Unity-2020.3.44f1-black)

> 单机游戏的本地修改工具，不涉及联机。用前建议先手动存一档——有几条命令会永久写进
> 存档，命令表里标了 `←`。

## 安装

不需要装开发工具，也不需要编译。

1. 装 **BepInEx 6 IL2CPP (x64)**，解压到游戏根目录
   （`...\steamapps\common\Nivalis Nights`）。注意必须是 6.x 的 **IL2CPP** 版本，
   5.x 和 Mono 版本都不行。
2. 启动一次游戏让 BepInEx 生成 interop 程序集，然后退出。第一次会卡几分钟，
   `BepInEx\LogOutput.log` 里能看到进度。
3. 从 [Releases](../../releases) 下载 `NivalisDevUnlock.dll`，放进
   `BepInEx\plugins\`。
4. 启动游戏，**读档之后**按 `F1`。主菜单里按没用——大部分命令要有存档才有东西可操作。

配置文件在 `BepInEx\config\nivalisnights.devunlock.cfg`，可改快捷键、字号倍率、
打开时是否暂停、是否屏蔽游戏输入、以及开局是否冻结腐败。

> dll 是针对某个游戏版本编译的。游戏更新后如果控制台失效，见下面的「排查」。

## 用法

打开控制台后 `help` 列出全部命令，`help <命令>` 看单条用法。常用的几条：

```
give 04f867e4 10       发放物品，代号查 reference/items.txt
money                  查看金钱（单位是分，1 元 = 100 分）
properties             列出可收购的房产，带中文店名、地点和所需许可
restock                给名下每家店的冰箱和储物柜补满菜单需要的食材
spoiled clear          清掉背包和各店里腐坏的食材
skill Skill_Cooking 9  把厨艺提到 9 级
dumpitems              把全部物品和房产导出成 txt
dumpmenus              导出菜单规划数据（含 18 个地点的菜谱评分）
```

需要传名字的命令（`weather`、`stat`、`recipe`、`addvenue`）故意写个不存在的名字，
报错会把全部可选项列出来，等于拿报错当查询用。

**完整命令表见 [`reference/commands.txt`](reference/commands.txt)**，里面每条命令都
附了它背后调的是什么、有什么坑。

## 参考资料

| 文件 | 内容 |
|---|---|
| [`reference/commands.txt`](reference/commands.txt) | 53 条命令的完整说明和实现细节 |
| [`reference/items.txt`](reference/items.txt) | 全部物品，第一列是可直接粘贴的 `give` 命令 |
| [`reference/recipe-scores.txt`](reference/recipe-scores.txt) | 18 个地点的人群构成，以及每道菜在该地点的评分 |
| [`reference/menu-design.txt`](reference/menu-design.txt) | 菜单设计与储物规划的机制和方法 |
| [`reference/global-variables.txt`](reference/global-variables.txt) | 全部剧情变量名（配合 `getvar` / `setbool` / `setint`） |
| [`reference/devoptions.txt`](reference/devoptions.txt) | 游戏内置开发选项清单及参数签名 |
| [`reference/inventory.txt`](reference/inventory.txt) | 物品与容器系统的类型结构 |
| [`reference/savesystem.txt`](reference/savesystem.txt) | 存档结构分析笔记 |

这些表都是某个游戏版本的快照，物品和变量会随版本增减。要最新的自己跑一次
`dumpitems` / `dumpmenus`。

## 从源码构建（只有改代码才需要）

想用插件的话上面下载 Releases 就够了，这一节是给要改代码的人看的。

构建需要：

- .NET 6 SDK
- 一份**已经装好 BepInEx 并且已经启动过一次**的游戏

第二条是硬性的：`csproj` 直接引用 `BepInEx\interop\Assembly-CSharp.dll` 等由
BepInEx 在本机生成的程序集。那些文件不在仓库里（它们是游戏代码，而且每个游戏版本
都不一样），所以没有游戏就编译不了——这也是为什么没法用 GitHub Actions 自动出包。

```powershell
dotnet build src\NivalisDevUnlock\NivalisDevUnlock.csproj -c Release
```

游戏不在默认路径（`C:\Program Files (x86)\Steam\steamapps\common\Nivalis Nights`）
时传 `GameDir`：

```powershell
dotnet build src\NivalisDevUnlock\NivalisDevUnlock.csproj -c Release -p:GameDir="D:\Games\Nivalis Nights"
```

产物在 `src\NivalisDevUnlock\bin\Release\net6.0\NivalisDevUnlock.dll`。加
`-p:Deploy=true` 会在构建后自动拷进 `BepInEx\plugins`。
`scripts\deploy-and-run.ps1` 是一键关游戏、构建、部署、重启的脚本（Windows + Steam）。

## 排查

**按 F1 没反应。** 看 `BepInEx\LogOutput.log` 里有没有
`Failed to generate Il2Cpp interop assemblies`。那是 BepInEx 的 interop 程序集和
新版本游戏对不上：把 `BepInEx\interop` 整个目录改名存档，再启动一次游戏让它重新
生成，之后重新编译部署插件。

**命令报找不到对象。** 大部分命令要读档之后才有东西可操作，主菜单里跑会报空。

**改剧情变量把任务弄坏了。** 改之前先用 `getvar` 读一遍原值。`clearitems` 默认会
保护剧情物品，但如果误删了，物品加回背包剧情变量也会跟着置回——前提是**还没存档**。

## 说明

命令大多是对游戏自带 `DevOption` 的包装，所以行为和游戏自己的开发菜单一致，不是
另写一套逻辑。评分、容量、等级门槛这些数字都是直接调游戏的函数或读它的字段拿到的。

`dev list` 能列出游戏内置的全部开发选项，没被包装成好记命令的（下棋、传送到某人、
调试窗口等）可以用 `dev <序号> [参数...]` 直接调。
