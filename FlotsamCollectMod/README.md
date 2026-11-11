# Flotsam 收集物数量 Mod（v1.0.0）

给《浮游世界 / Flotsam》（Epic 版，v1.1.0f5，Unity 6.3 Mono）做的收集物数量修改 Mod。

- **收集倍率**：从世界里打捞 / 捡到 / 钓到的物品，交回船舱或仓库时变成 N 个（默认 2）。
- **F7 面板**：游戏内物品清单，点一下就能把指定物品直接放进船舱 / 仓库（数量可自己填）。

已实测：BepInEx 能在 Unity 6000.3.28 上正常注入，Harmony 补丁应用成功，面板与中文字体渲染正常。

---

## 一、已经装好的东西

| 内容 | 位置 |
| --- | --- |
| BepInEx 5.4.23.6（Mod 加载器） | `E:\e宝\Flotsam\BepInEx\`、`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version` |
| 本 Mod 插件 | `E:\e宝\Flotsam\BepInEx\plugins\FlotsamCollectMod\FlotsamCollectMod.dll` |
| 配置文件 | `E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.collectmod.cfg` |
| 运行日志 | `E:\e宝\Flotsam\BepInEx\LogOutput.log` |

装法是标准的 BepInEx 注入：`winhttp.dll` 作为代理 DLL 被游戏加载，从而在 Mono 运行时启动时先加载 `BepInEx\core`，再加载 `plugins` 里的插件。**不改动任何游戏原文件**（没有替换 `Assembly-CSharp.dll`）。

---

## 二、怎么用

1. 照常从 Epic 启动 Flotsam（不用改启动方式）。
2. 进入存档后：
   - 漂民把打捞物 / 漂浮物 / 鱼搬回船上时，每个物品会**多给 N-1 个**（默认总共 2 个）。
   - 按 **F7** 打开物品面板：左边是物品名，右边 `+N` / `+1` / `+99` 按钮直接把物品放进船舱或仓库。顶部输入框可以改 `+N` 的 N，也可以搜索物品名。
   - 再按 F7 关闭面板。

> 面板里的物品清单来自游戏自己的物品表（`GameManager.Settings.ItemSettings.ItemProperties`），显示为当前语言的名字。

---

## 三、改倍率

用记事本打开：

```
E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.collectmod.cfg
```

```ini
[Collection]
Multiplier = 2          ; 每个收集物变成几个，1 = 原版，范围 1~20

[Interface]
PanelKey = F7           ; 面板快捷键
OpenPanelAtStart = false ; 进游戏自动打开面板（排查问题时可设 true）
```

改完**重启游戏**生效。

---

## 四、怎么确认生效

打开 `E:\e宝\Flotsam\BepInEx\LogOutput.log`，正常应该有：

```
[Message: BepInEx] 1 plugin to load
[Info   :Flotsam Collect Quantity Mod] [CollectMod] patch applied. Multiplier = 2
```

进存档后第一次捡到东西，会出现：

```
[CollectMod] world pickup detected (...), multiplier active.
```

物品交回船上/仓库后（每累计 25 个记录一次）：

```
[CollectMod] +1 x Driftwood (extra items total: 25)
```

看到这几行就说明倍率真的在起作用。

---

## 五、实现原理（给想改的人）

只有一个 Harmony 补丁，挂在游戏里唯一的"物品进入容器"入口上：

```csharp
[HarmonyPatch(typeof(Inventory), "OnItemAdded", new Type[] { typeof(Item), typeof(SubInventory) })]
```

- `OnItemAdded` 的前缀执行时，`item.Inventory` **还指向上一个容器**，所以能判断来源：
  - 来源 `InventoryType.Flotsam`（打捞点、海面漂浮物、鱼群）→ 记为"在途收集物"；
  - 目的地是 `InventoryType.Boat` / `Storage`（船 / 仓库）且该物品在记录里 → 就地复制 `Multiplier - 1` 份，复制前用 `FitsInInventory` 检查空间。
- 只复制"从世界收集来的"物品；玩家在仓库之间倒腾物品、建筑生产物品都不会触发，避免刷物品 / 无限循环。
- 任务物品、`SalvageMarker` / `FishMarker` 标记物不复制。

其它：面板是纯 IMGUI（`OnGUI`），用 `Font.CreateDynamicFontFromOSFont` 加载系统中文字体（微软雅黑等），所以中文能正常显示；每帧异常都有 try/catch，出错只写日志、不会让游戏崩。

---

## 六、已知限制

1. **仓库/船舱没有空位时不会多给**（Mod 不会把容量塞爆）。想多囤就多造仓库，或直接用 F7 面板加。
2. 只影响"从世界里收集"的物品（打捞点、漂浮物、鱼群）；农场、鸟屋这类**建筑自己生产**的东西不翻倍——需要的话用 F7 面板补。
3. 面板列表是游戏的全部物品表，前期会看到还没解锁的物品，属正常现象。
4. 和存档兼容，但**改数量类 Mod 都建议先备份存档**：
   `C:\Users\<你>\AppData\LocalLow\Pajama Llama Games\Flotsam\Saves`

---

## 七、卸载

删掉这 4 项即可完全恢复原版：

```
E:\e宝\Flotsam\BepInEx\
E:\e宝\Flotsam\winhttp.dll
E:\e宝\Flotsam\doorstop_config.ini
E:\e宝\Flotsam\.doorstop_version
```

游戏本体文件没有被修改过，所以卸载不需要校验/重装游戏。

---

## 八、重新编译 / 改代码

源码在本仓库：

```
FlotsamCollectMod\
  src\FlotsamCollectMod.cs     插件源码（C#）
  build.ps1                    编译脚本（Roslyn csc，直接引用游戏 DLL）
  install.ps1                  把编译结果复制进游戏目录
  dist\BepInEx\plugins\...     编译产物
```

编译（不需要装 Unity 或 .NET SDK，只用一个 Roslyn 的 `csc.exe`，可从 NuGet 包 `Microsoft.Net.Compilers.Toolset` 里取 `tasks\net472\csc.exe`）：

```powershell
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'

.\install.ps1 -GameDir 'X:\path\to\Flotsam'
```

`build.ps1` 是 `/nostdlib+` 编译，直接引用游戏 `Flotsam_Data\Managed` 里的 `mscorlib`、`UnityEngine.*`、`Assembly-CSharp.dll` 和 `BepInEx\core` 里的 `BepInEx.dll`、`0Harmony.dll`，所以产物的依赖版本和游戏完全一致。

---

## 九、故障排查

| 现象 | 原因 / 处理 |
| --- | --- |
| 游戏打不开、黑屏、闪退 | 删掉 `winhttp.dll` + `BepInEx\` 先恢复原版，再把 `LogOutput.log` 发我 |
| 日志里有 `could not apply patch` | 游戏更新后方法名/签名变了，需要重新适配（把日志发我即可） |
| 日志里没有 `1 plugin to load` | BepInEx 没被加载，检查 `winhttp.dll` 是否还在游戏根目录 |
| 按 F7 没反应 | 确认进的是存档而不是主菜单；或日志里看有没有 `panel drawn` |
| 面板有字但没多给东西 | 看日志有没有 `+N x ...`；没有的话多半是仓库满了 |
