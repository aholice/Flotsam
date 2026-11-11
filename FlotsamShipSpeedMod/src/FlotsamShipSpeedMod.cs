// =============================================================================
// Flotsam 船速加速 Mod（Flotsam Ship Speed）v1.0.0
// -----------------------------------------------------------------------------
// 【功能】让城镇（也就是那艘漂在海上的大船）在地图上移动得更快：
//   - 在地图上点目标位置、让城镇自动航行过去（MoveTo）时更快；
//   - 用 WASD 手动拖行时也更快。
//   默认 3 倍；按 F6 可随时在"加速 / 原速"之间切换，切换时会重新读一次配置文件。
//
// 【目标游戏】
//   Flotsam 1.1.0f5（Epic 版），Unity 6000.3.28f1，Mono 后端；依赖 BepInEx 5.4.23.x。
//
// 【核心原理】
//   地图航行速度来自 WorldMap 上的两个私有序列化字段：
//       _movementSpeed = 50f   // MoveToCoroutine 里：每秒进度 = _movementSpeed / 路径长度
//       _rotationSpeed = 50f   // 航行途中转向对准航向的快慢
//   它们同时被公开属性 WorldMap.MovementSpeed / RotationSpeed 使用（WASD 拖行走的是属性），
//   所以只要把这两个字段按倍率放大，两种移动方式会一起变快。
//
//   ★ 这里刻意不使用 Harmony：字段本身没有 getter 可 patch，而给协程打 transpiler 又容易
//     被游戏版本的 IL 细节坑到。直接改字段值最简单，而且就算字段改名了也只是"找不到、不生效"，
//     不会让游戏崩溃。
//
// 【安全性】
//   - 每个 WorldMap 实例只在第一次被看到时读一次原始值，之后按倍率重算，避免反复相乘；
//   - 读档 / 开新档会换 WorldMap 实例，检测到新实例后自动重新套用；
//   - 所有反射调用都包了 try/catch。
// =============================================================================

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace FlotsamShipSpeedMod
{
    /// <summary>
    /// 插件入口：绑定配置、跟踪当前世界实例、按倍率改写 WorldMap 的速度字段、F6 开关。
    /// </summary>
    [BepInPlugin(Guid, PluginName, Version)]
    public class ShipSpeedPlugin : BaseUnityPlugin
    {
        // BepInEx 插件标识 / 显示名 / 版本
        public const string Guid = "com.dsh.flotsam.shipspeed";
        public const string PluginName = "Flotsam Ship Speed";
        public const string Version = "1.0.0";

        // 要改写的私有字段名（如果游戏更新改名，插件会在日志里报 "not found" 而不是崩掉）
        private const string SpeedFieldName = "_movementSpeed";
        private const string RotationFieldName = "_rotationSpeed";

        internal static ConfigEntry<float> SpeedMultiplierEntry;   // 航行速度倍率
        internal static ConfigEntry<float> RotationMultiplierEntry; // 转向速度倍率
        internal static ConfigEntry<KeyboardShortcut> ToggleKeyEntry; // 开关热键（默认 F6）
        internal static ConfigEntry<bool> EnabledEntry;             // 是否默认开启
        internal static ManualLogSource ModLog;

        // 反射拿到的字段句柄（静态初始化一次，之后复用）
        private static readonly FieldInfo SpeedField =
            typeof(WorldMap).GetField(SpeedFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RotationField =
            typeof(WorldMap).GetField(RotationFieldName, BindingFlags.Instance | BindingFlags.NonPublic);

        private WorldMap _handledWorld;        // 当前已经处理过的 WorldMap 实例
        private float _baseSpeed;              // 该实例的原始航行速度（未放大）
        private float _baseRotation;           // 该实例的原始转向速度
        private bool _active;                  // 当前是否处于加速状态
        private bool _reportedMissingFields;   // 字段缺失只报一次
        private float _toastUntil;             // 屏幕提示的显示截止时间
        private string _toastText = string.Empty;
        private Font _font;                    // 提示文字用的中文字体

        /// <summary>
        /// 插件加载时执行：绑定配置、检查字段是否存在、打印启动日志。
        /// </summary>
        private void Awake()
        {
            ModLog = Logger;

            SpeedMultiplierEntry = Config.Bind("Speed", "SpeedMultiplier", 3f,
                new ConfigDescription("Map travel speed multiplier (1 = vanilla). Applied to WorldMap._movementSpeed.",
                    new AcceptableValueRange<float>(1f, 10f)));
            RotationMultiplierEntry = Config.Bind("Speed", "RotationMultiplier", 3f,
                new ConfigDescription("Turning speed multiplier while travelling (1 = vanilla).",
                    new AcceptableValueRange<float>(1f, 10f)));
            EnabledEntry = Config.Bind("Speed", "Enabled", true,
                "Start with the speed-up enabled.");
            ToggleKeyEntry = Config.Bind("Keys", "Toggle", new KeyboardShortcut(KeyCode.F6),
                "Toggle the speed-up on/off (also re-reads this config file).");

            if (SpeedField == null || RotationField == null)
            {
                // 游戏版本不匹配：只报错，不做任何事，游戏照常玩
                Logger.LogError("[ShipSpeed] WorldMap speed fields not found - this game build is not supported.");
            }
            else
            {
                Logger.LogInfo("[ShipSpeed] ready. Default multiplier x" + SpeedMultiplierEntry.Value + ", toggle key " + ToggleKeyEntry.Value + ".");
            }
        }

        /// <summary>
        /// 每帧：跟踪 WorldMap 实例变化 + 处理 F6。
        /// 用 Update 而不是 FixedUpdate，避免暂停时热键失灵。
        /// </summary>
        private void Update()
        {
            try
            {
                TrackWorld();

                if (ToggleKeyEntry.Value.IsDown())
                {
                    Toggle();
                }
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
        }

        /// <summary>
        /// 跟踪当前世界：WorldMap 在进入存档后才有；读档 / 换档 / 回主菜单都会换实例。
        /// 只有实例变化时才重新取原始值并套用倍率，避免每帧反复相乘把数值滚爆。
        /// </summary>
        private void TrackWorld()
        {
            WorldMap world = GetWorldMap();

            if (world == null)
            {
                if (_handledWorld != null)
                {
                    Log("[ShipSpeed] world unloaded.");
                }
                _handledWorld = null;
                return;
            }

            if (ReferenceEquals(world, _handledWorld))
            {
                return;   // 还是同一个世界，什么都不用做
            }

            _handledWorld = world;
            _baseSpeed = ReadField(SpeedField, world, 50f);
            _baseRotation = ReadField(RotationField, world, 50f);
            _active = EnabledEntry.Value;

            Apply();

            Log("[ShipSpeed] world ready: travel speed " + Fmt(_baseSpeed) + " -> " + Fmt(_baseSpeed * SpeedMultiplierEntry.Value)
                + " (x" + Fmt(SpeedMultiplierEntry.Value) + "), turn speed " + Fmt(_baseRotation) + " -> " + Fmt(_baseRotation * RotationMultiplierEntry.Value)
                + ", " + (_active ? "ENABLED" : "disabled, press " + ToggleKeyEntry.Value + " to enable") + ".");
        }

        /// <summary>
        /// F6：切换加速开关。顺便 Config.Reload()，这样玩家手改 .cfg 后按一下 F6 就能生效，不用重启。
        /// </summary>
        private void Toggle()
        {
            try
            {
                Config.Reload();
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }

            _active = !_active;
            Apply();

            string state = _active
                ? "x" + Fmt(SpeedMultiplierEntry.Value) + " (" + Fmt(_baseSpeed) + " -> " + Fmt(_baseSpeed * SpeedMultiplierEntry.Value) + ")"
                : "vanilla (" + Fmt(_baseSpeed) + ")";
            ShowToast("航速 " + (_active ? "加速 " : "正常 ") + state);
            Log("[ShipSpeed] " + (_active ? "enabled " : "disabled ") + state + ".");
        }

        /// <summary>把当前状态写回 WorldMap 的两个私有字段（加速时 = 原始值 × 倍率）。</summary>
        private void Apply()
        {
            if (_handledWorld == null)
            {
                return;
            }
            if (SpeedField == null || RotationField == null)
            {
                if (!_reportedMissingFields)
                {
                    _reportedMissingFields = true;
                    Log("[ShipSpeed] speed fields missing, nothing to do.");
                }
                return;
            }

            float speed = _active ? _baseSpeed * SpeedMultiplierEntry.Value : _baseSpeed;
            float rotation = _active ? _baseRotation * RotationMultiplierEntry.Value : _baseRotation;

            try
            {
                SpeedField.SetValue(_handledWorld, speed);
                RotationField.SetValue(_handledWorld, rotation);
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
        }

        /// <summary>
        /// 读私有 float 字段，读失败或值不正常（&lt;= 0）时回退到 fallback。
        /// 回退值用 50 是因为这就是游戏里 _movementSpeed / _rotationSpeed 的默认值。
        /// </summary>
        private static float ReadField(FieldInfo field, object target, float fallback)
        {
            try
            {
                if (field == null || target == null)
                {
                    return fallback;
                }
                object value = field.GetValue(target);
                if (value is float)
                {
                    float number = (float)value;
                    if (number > 0.01f)
                    {
                        return number;
                    }
                }
            }
            catch
            {
            }
            return fallback;
        }

        /// <summary>取当前世界的 WorldMap（主菜单里为 null）。</summary>
        internal static WorldMap GetWorldMap()
        {
            try
            {
                if (GameManager.WorldMapManager == null)
                {
                    return null;
                }
                return GameManager.WorldMapManager.WorldMap;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ UI

        /// <summary>屏幕中间上方闪一行提示，2.5 秒后自动消失。</summary>
        private void ShowToast(string text)
        {
            _toastText = text;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        /// <summary>
        /// 绘制提示文字。只在提示有效期内工作，异常只记日志并立刻隐藏，避免每帧刷屏。
        /// 中文字体用系统字体动态创建（IMGUI 默认字体没有 CJK 字形）。
        /// </summary>
        private void OnGUI()
        {
            if (Time.unscaledTime > _toastUntil)
            {
                return;
            }
            try
            {
                if (_font == null)
                {
                    _font = Font.CreateDynamicFontFromOSFont(
                        new string[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun", "Arial Unicode MS" }, 18);
                }
                if (_font != null && GUI.skin != null && GUI.skin.font != _font)
                {
                    GUI.skin.font = _font;
                }

                GUIStyle style = new GUIStyle(GUI.skin.label);
                style.fontSize = 18;
                style.alignment = TextAnchor.MiddleCenter;
                if (_font != null)
                {
                    style.font = _font;
                }

                float width = 360f;
                Rect rect = new Rect((Screen.width - width) * 0.5f, 90f, width, 32f);
                GUI.Label(rect, _toastText, style);
            }
            catch (Exception e)
            {
                LogThrottled(e);
                _toastUntil = 0f;
            }
        }

        // --------------------------------------------------------------- misc

        /// <summary>数值格式化：最多一位小数，避免日志里出现 149.99999 这种。</summary>
        private static string Fmt(float value)
        {
            return value.ToString("0.#");
        }

        internal static void Log(string message)
        {
            try
            {
                if (ModLog != null)
                {
                    ModLog.LogInfo(message);
                }
            }
            catch
            {
            }
        }

        internal static void LogThrottled(Exception e)
        {
            try
            {
                if (ModLog != null)
                {
                    ModLog.LogError("[ShipSpeed] " + e);
                }
            }
            catch
            {
            }
        }
    }
}
