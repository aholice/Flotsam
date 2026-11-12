# Flotsam Mods

给《浮游世界 / Flotsam》（Epic 版 **v1.1.0f5**，Unity **6000.3.28f1**，Mono 后端）写的四个 BepInEx 插件。
都是单机向的便利 Mod：不改游戏本体文件，随时可以删掉还原。

| Mod | 作用 | 热键 |
| --- | --- | --- |
| [FlotsamCollectMod](FlotsamCollectMod/README.md) | 收集物数量：打捞/捡到/钓到的物品交回船时按倍率多给；附带物品数量面板 | **F7** 面板 |
| [FlotsamShipSpeedMod](FlotsamShipSpeedMod/README.md) | 船速加速：地图航行与手动拖行都变快（默认 3 倍） | **F6** 开关 |
| [FlotsamEnergyMod](FlotsamEnergyMod/README.md) | 电量容量：所有储电建筑容量 ×10，满电电池按新上限补满 | **F5** 重读配置 |
| [FlotsamEnergyRechargeMod](FlotsamEnergyRechargeMod/README.md) | 快速回电：F4 瞬间充满 + 可选持续回电 + 航行耗电 ×0.5 | **F4** 充满 |

> 组合效果：容量 ×10 + 耗电 ×0.5 ≈ **原版 20 倍续航**，而且随时按 F4 可以满电。

## 依赖

- [BepInEx 5.4.23.x（x64, Mono）](https://github.com/BepInEx/BepInEx/releases) —— 实测 5.4.23.6 可在 Unity 6000.3.28 上正常注入
- 游戏必须是 Mono 后端（`Flotsam_Data\Managed\Assembly-CSharp.dll` 存在即满足），IL2CPP 版本不适用

## 安装

1. 把 BepInEx 解压到游戏根目录（得到 `winhttp.dll`、`doorstop_config.ini`、`BepInEx\`）。
2. 把插件 DLL 放进对应目录：
   ```
   <游戏目录>\BepInEx\plugins\FlotsamCollectMod\FlotsamCollectMod.dll
   <游戏目录>\BepInEx\plugins\FlotsamShipSpeedMod\FlotsamShipSpeedMod.dll
   <游戏目录>\BepInEx\plugins\FlotsamEnergyMod\FlotsamEnergyMod.dll
   <游戏目录>\BepInEx\plugins\FlotsamEnergyRechargeMod\FlotsamEnergyRechargeMod.dll
   ```
3. 启动游戏，检查 `<游戏目录>\BepInEx\LogOutput.log` 里有没有 `4 plugins to load` 和各自的 `ready` 日志。

配置文件会在首次启动后生成在 `<游戏目录>\BepInEx\config\`。
改完配置：船速按 F6、电量容量按 F5、快速回电按 F4 即可热重载，其余需要重启游戏。

## 目录结构

```
FlotsamCollectMod\
  src\FlotsamCollectMod.cs          插件源码（带完整中文注释）
  build.ps1  install.ps1  README.md
FlotsamShipSpeedMod\
  src\FlotsamShipSpeedMod.cs
  build.ps1  README.md
FlotsamEnergyMod\
  src\FlotsamEnergyMod.cs
  build.ps1  README.md
FlotsamEnergyRechargeMod\
  src\FlotsamEnergyRechargeMod.cs
  build.ps1  README.md
```

## 编译

不需要装 Unity，也不需要 .NET SDK；**只用一个 Roslyn 的 `csc.exe`**。
脚本用 `/nostdlib+` 编译，引用直接取游戏 `Flotsam_Data\Managed` 里的 `mscorlib`、`UnityEngine.*`、`Assembly-CSharp.dll`，以及 `BepInEx\core` 里的 `BepInEx.dll`、`0Harmony.dll`，所以产物和游戏运行时的依赖版本完全一致。

`csc.exe` 可以从 NuGet 包 **Microsoft.Net.Compilers.Toolset** 里取（解压后 `tasks\net472\csc.exe`）。

```powershell
cd FlotsamCollectMod
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'

cd ..\FlotsamShipSpeedMod
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'

cd ..\FlotsamEnergyMod
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'

cd ..\FlotsamEnergyRechargeMod
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'
```

## 说明与免责

- 均为单机 Mod，**不适用于联机/排行榜**；改数值类 Mod 建议先备份存档
  （`%USERPROFILE%\AppData\LocalLow\Pajama Llama Games\Flotsam\Saves`）。
- 游戏更新后如果方法/字段改名，插件会在 `LogOutput.log` 里报错并自动失效，不会让游戏崩溃。
- 仅用于个人学习与单机游玩。
