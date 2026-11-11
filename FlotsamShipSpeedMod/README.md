# Flotsam 船速加速 Mod（v1.0.0）

让城镇/船在地图上**航行更快**：点地图航行、以及 WASD 手动拖行都快，默认 **3 倍**。

- **F6**：加速 开 / 关（同时会重新读取配置文件，改完倍率不用重启）
- 开关时屏幕上方会闪一行提示，例如「航速 加速 x3 (50 -> 150)」

与已有 Mod 的按键不冲突：**F6** 航速，**F7** 收集物数量。

---

## 一、安装情况

| 内容 | 位置 |
| --- | --- |
| 本 Mod | `E:\e宝\Flotsam\BepInEx\plugins\FlotsamShipSpeedMod\FlotsamShipSpeedMod.dll` |
| 配置文件 | `E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.shipspeed.cfg` |
| 日志 | `E:\e宝\Flotsam\BepInEx\LogOutput.log` |

---

## 二、怎么用

装好直接进游戏就是 3 倍速，不需要额外操作。按 **F6** 可以随时切回原速。

改倍率：打开

```
E:\e宝\Flotsam\BepInEx\config\com.dsh.flotsam.shipspeed.cfg
```

```ini
[Speed]
SpeedMultiplier = 3      ; 航行速度倍率，1 = 原版，范围 1~10
RotationMultiplier = 3   ; 转向速度倍率，范围 1~10
Enabled = true           ; 进游戏默认是否开启

[Keys]
Toggle = F6              ; 开关热键
```

改完在游戏里按一下 **F6**（关再开）就会用新倍率，**不用重启**。

---

## 三、原理

地图航行由 `WorldMap` 里两个私有序列化字段决定：

| 字段 | 默认值 | 作用 |
| --- | --- | --- |
| `_movementSpeed` | 50 | `MoveToCoroutine` 里 `进度/秒 = _movementSpeed / 路径长度`，即航行速度 |
| `_rotationSpeed` | 50 | 转向时对准航向的速度 |

插件在检测到世界加载后，按倍率把这两个字段乘一下（**不打 IL 补丁**，只是改字段值）。因为每帧检查 `GameManager.WorldMapManager.WorldMap` 的实例是否变化，所以读档、开新档、切换场景后会自动重新套用。

安全性：

- 字段名找不到时**只报错、不做事**，不会影响游戏；
- 全程 try/catch，出错只写日志；
- 世界没加载时不动任何东西。

能量消耗按**距离**算（`ConsumeEnergy(每帧移动距离)`），所以加速不会多花电，只是把同样的路程花更少时间。能量耗尽的冷却仍然照常触发。

---

## 四、怎么确认生效

看 `E:\e宝\Flotsam\BepInEx\LogOutput.log`，进存档后应有：

```
[Info   :Flotsam Ship Speed] [ShipSpeed] ready. Default multiplier x3, toggle key F6.
[Info   :Flotsam Ship Speed] [ShipSpeed] world ready: travel speed 50 -> 150 (x3), turn speed 50 -> 150, ENABLED.
```

如果出现 `WorldMap speed fields not found`，说明游戏版本变了、字段改名了，把日志发我即可。

---

## 五、已知限制

1. **不会减少能耗**：加速只省时间，电量还是按原来的「每单位距离」消耗；跑太远照样会没电冷却。
2. 倍率开太高（比如 8~10）时，**手动 WASD 拖行**可能与障碍物的碰撞表现变怪（一帧位移过大穿过去）；点地图航行是沿路径插值，一般没问题。推荐 2~5。
3. 不影响游戏速度（暂停 / 1x / 2x / 3x 那套），只影响船的航行速度。
4. 与「收集物数量 Mod」「（已删除的）标点传送 Mod」互不影响。

---

## 六、卸载

删掉这个文件夹即可：

```
E:\e宝\Flotsam\BepInEx\plugins\FlotsamShipSpeedMod\
```

（配置文件 `com.dsh.flotsam.shipspeed.cfg` 留着不影响游戏，想清干净也可以一并删。）

---

## 七、重新编译

```
FlotsamShipSpeedMod\
  src\FlotsamShipSpeedMod.cs
  build.ps1
  dist\BepInEx\plugins\FlotsamShipSpeedMod\FlotsamShipSpeedMod.dll
```

```powershell
.\build.ps1 -GameDir 'X:\path\to\Flotsam' -ProjectDir . -Csc 'C:\path\to\csc.exe'
```

`csc.exe` 是 Roslyn 编译器，可从 NuGet 包 `Microsoft.Net.Compilers.Toolset` 解压获得（`tasks\net472\csc.exe`）。
