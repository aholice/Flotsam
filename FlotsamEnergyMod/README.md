# Flotsam 电量容量 Mod（v1.0.0）

把城镇的**储电上限放大**，电量条跟着变长——满电一次能跑更远。

- 默认 **容量 ×10**（每座储电建筑 1000 → 10000）
- **原本是满的电池，按新上限补满**（装完立刻有 10 倍电，不用干等充电）
- **F5**：重新读取配置文件并立即套用，改倍率不用重启

配合「船速加速 Mod」和「快速回电 Mod」用：
加速本身不省电，但容量 ×10 + 耗电 ×0.5 ⇒ 同样满电状态下能跑的距离接近原版的 20 倍。

---

## 一、安装情况

| 内容 | 位置 |
| --- | --- |
| 本 Mod | `E:\e宝\Flotsam\BepInEx\plugins\FlotsamEnergyMod\FlotsamEnergyMod.dll` |
| 配置文件 | `E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.energycap.cfg` |
| 日志 | `E:\e宝\Flotsam\BepInEx\LogOutput.log` |

---

## 二、配置

```
E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.energycap.cfg
```

```ini
[Energy]
CapacityMultiplier = 10   ; 容量倍率，1 = 原版，范围 1~50
RefillOnScale = true      ; 原本满的电池按新上限补满

[Keys]
Reload = F5               ; 重读配置并立即套用
```

改完在游戏里按 **F5** 即时生效。屏幕上会闪一行「电量容量 x10」。

> 想要"基本上用不完"可以把 `CapacityMultiplier` 调到 20~50；
> 不想要补满的把 `RefillOnScale` 改成 false。

---

## 三、原理

能量网格的总容量 = 所有储电建筑 `EnergyStorage` 的私有字段之和：

```csharp
[SerializeField] private float _energyCapacity = 1000f;   // 每座储电建筑（含引擎自带那块）
public float EnergyCapacity => _energyCapacity;           // 网格求和、HUD 电量条、总览面板都读这个
```

插件每 2 秒扫描一次场景里的 `EnergyStorage`（新建的储电建筑也会被扫到），把 `_energyCapacity` 按倍率缩放：

- 只改**运行时内存里的值**，不写存档；
- 存档只存"当前电量"（`EnergyStoragePersistentData.EnergyAmount`），不存容量，所以**不会污染存档**；
- 如果某座电池缩放前是满的（`EnergyAmount >= 原容量`），缩放后按新容量补满，避免"满电突然变 10%"；
- 扫描只处理**已加载场景里的物体**，跳过预制体资源，不会出现"倍率叠加"的问题。

---

## 四、怎么确认生效

日志里应有：

```
[Info   :Flotsam Energy Capacity] [Energy] ready. Capacity x10, reload key F5.
[Info   :Flotsam Energy Capacity] [Energy] scaled 1 storage(s) to x10 (now 1 tracked).
```

第二行出现就说明真的改到了。如果只有第一行、没有 `scaled`，说明当前存档里还没有储电建筑（先造一个）。

---

## 五、已知限制

1. **不改变发电速度，也不改变每单位距离的耗电**（耗电折扣在「快速回电 Mod」里）。
   容量变大后，充满一次要更久，但单次满电能跑的距离也同比变长。
2. 电量条的上限变大，所以刚装上时**百分比会显得变低**——这是正常的（绝对值变大了）。
   开了 `RefillOnScale` 时满电电池会直接补满，看不出来。
3. 能量耗尽的冷却机制不变（电量见底照样要等；用「快速回电 Mod」的 F4 可以立刻解掉）。
4. 不同能量网格各自计算，插件对所有储电建筑都生效。

---

## 六、卸载

删掉这个文件夹：

```
E:\e宝\Flotsam\BepInEx\plugins\FlotsamEnergyMod\
```

容量会回到原版（当前电量按原容量截断显示）。

---

## 七、重新编译

```
FlotsamEnergyMod\
  src\FlotsamEnergyMod.cs
  build.ps1
  dist\BepInEx\plugins\FlotsamEnergyMod\FlotsamEnergyMod.dll
```

```powershell
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'
```
