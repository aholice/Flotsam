# Flotsam 快速回电 Mod（v1.0.0）

解决"船开一小段就没电"：直接给能量条补电，并（可选）降低航行耗电。

| 功能 | 默认 | 说明 |
| --- | --- | --- |
| **F4 瞬间充满** | 开到 100% | 按一下就充满，屏幕提示"电量 X / Y（约可航行 Z）" |
| 持续回电 | 0（关闭） | 每秒自动回 N 点电，设成 300 就是每秒回 300 |
| 耗电折扣 | **×0.5** | 航行耗电减半；设 1.0 恢复原版 |
| 完全不掉电 | 关闭 | 打开后航行一点都不耗电 |

> 和「电量容量 Mod」配合：容量 ×10 + 耗电 ×0.5 ⇒ 同样一箱电能跑的距离接近原版的 **20 倍**。

---

## 一、安装情况

| 内容 | 位置 |
| --- | --- |
| 本 Mod | `E:\e宝\Flotsam\BepInEx\plugins\FlotsamEnergyRechargeMod\FlotsamEnergyRechargeMod.dll` |
| 配置文件 | `E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.energyrecharge.cfg` |
| 日志 | `E:\e宝\Flotsam\BepInEx\LogOutput.log` |

---

## 二、配置

```
E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.energyrecharge.cfg
```

```ini
[Recharge]
RefillKey = F4            ; 瞬间充满的热键
FillPercent = 100         ; 充到百分之多少
RegenPerSecond = 0        ; 每秒自动回电，0 = 关闭

[Cheat]
ConsumptionMultiplier = 0.5  ; 航行耗电倍率，1 = 原版，0.5 = 一半，0 = 不耗电
NoEnergyDrain = false        ; true = 航行完全不耗电（覆盖上面的倍率）
```

改完按一下 **F4** 就会重读配置并生效，不用重启游戏。

---

## 三、原理

游戏的能量系统很简单：

```csharp
EnergyGrid.ReturnStorageEnergy()    // 当前电量
EnergyGrid.ReturnStorageCapacity()  // 总容量（所有储电建筑之和）
EnergyGrid.FillStorageEnergy(n)     // 充电
Engine.ReturnEnergyCost(distance)   // 航行耗电 = distance × 每单位耗电
Engine.ReturnEnergyRange()          // 当前电量还能跑多远
```

本 Mod 做三件事：

1. **F4**：算出目标电量 `容量 × FillPercent`，差值丢给 `FillStorageEnergy`。
   电量补回去以后，游戏的冷却（`IsCoolingDown`）会在下一帧自动清掉，不用额外处理。
2. **持续回电**：每帧按 `RegenPerSecond × dt` 累积，每 0.25 秒真正充一次（降低 UI 刷新频率）。
3. **耗电折扣**：Harmony 给 `Engine.ReturnEnergyCost` 加 Postfix，把返回值乘上倍率。
   这个方法同时被扣电逻辑和 UI 的"续航估算"使用，所以打折后两处一致。
4. **完全不掉电**：直接用游戏自带的开发开关 `EnergyDevTools.FreeMovement`，
   打开后 `Engine.ConsumeEnergy()` 直接 return true，一个电都不扣。

安全性：补丁失败只写日志；所有调用都 try/catch；主菜单（没有城镇电网）时什么都不做。

---

## 四、怎么确认生效

日志里应有：

```
[Recharge] patch applied. Energy cost x0.5.
[Recharge] ready. F4 = instant refill, consumption x0.5, regen 0/s, no-drain False.
[Recharge] grid ready: 4800 / 10000 energy, current travel range 1200 units.
[Recharge] refilled: 电量 10000 / 10000（+5200，约可航行 2500）
```

`grid ready` 那行的"travel range"就是**当前电量还能航行多少世界单位**，可以直接看出改动效果。

---

## 五、已知限制

1. 只对**玩家城镇的电网**（`Community.PlayerCommunity.Engine.EnergyGrid`）生效，NPC 城镇不受影响。
2. "瞬间充满"最多充到容量上限，所以容量本身还是由「电量容量 Mod」决定；容量太小的话，充满后能跑的距离也有限。
3. `NoEnergyDrain` 用的是游戏自带的开发开关，属于全局状态；关掉 Mod 后随进程结束恢复。
4. 耗电折扣只改"航行耗电"，不影响建筑耗电（生产/照明等）。

---

## 六、卸载

删掉这个文件夹：

```
E:\e宝\Flotsam\BepInEx\plugins\FlotsamEnergyRechargeMod\
```

---

## 七、重新编译

```
FlotsamEnergyRechargeMod\
  src\FlotsamEnergyRechargeMod.cs
  build.ps1
  dist\BepInEx\plugins\FlotsamEnergyRechargeMod\FlotsamEnergyRechargeMod.dll
```

```powershell
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'
```
