// =============================================================================
// Flotsam 快速回电 Mod（Flotsam Energy Recharge）v1.0.0
// -----------------------------------------------------------------------------
// 【解决什么问题】
//   原版里船的电池容量本来就小，而航行耗电是按"距离 × 每单位耗电"算的
//   （每单位耗电还跟城镇重量挂钩，越重越费）。所以拖着重船开一小段就见底。
//   这个 Mod 给三种互补的手段，默认只开"半耗电"这一档，其余按需在配置里开：
//
//     1) F4 瞬间把电量充满（可配置充到百分之多少）；
//     2) 持续回电：每秒自动回 X 点电（默认 0 = 关闭）；
//     3) 降低耗电：耗电 ×0.5（默认），或直接"完全不掉电"。
//
// 【热键】F4 = 充满电量（同时会重读配置文件，改完不用重启）
//
// 【目标游戏】Flotsam 1.1.0f5（Epic 版），Unity 6000.3.28f1，Mono；依赖 BepInEx 5.4.23.x。
//
// 【原理】
//   - 能量都存在 EnergyGrid 里：ReturnStorageEnergy() 是当前电量，
//     ReturnStorageCapacity() 是总容量，FillStorageEnergy(n) 是充电。
//   - 城镇的电网 = Community.PlayerCommunity.Engine.EnergyGrid。
//   - 航行耗电 = Engine.ReturnEnergyCost(distance) = distance × 每单位耗电。
//     用 Harmony 给这个方法加 Postfix，把返回值乘上倍率即可（1 = 原版）。
//   - "完全不掉电"用的是游戏自带的开发用开关 EnergyDevTools.FreeMovement，
//     打开后 Engine.ConsumeEnergy() 直接 return true，不扣电。
// =============================================================================

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FlotsamEnergyRechargeMod
{
    /// <summary>
    /// 插件入口：F4 瞬间充满、可选的持续回电、可选的耗电折扣 / 完全不掉电。
    /// </summary>
    [BepInPlugin(Guid, PluginName, Version)]
    public class RechargePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.dsh.flotsam.energyrecharge";
        public const string PluginName = "Flotsam Energy Recharge";
        public const string Version = "1.0.0";

        internal static ConfigEntry<KeyboardShortcut> RefillKeyEntry;
        internal static ConfigEntry<float> FillPercentEntry;
        internal static ConfigEntry<float> RegenPerSecondEntry;
        internal static ConfigEntry<float> ConsumptionMultiplierEntry;
        internal static ConfigEntry<bool> NoEnergyDrainEntry;
        internal static ManualLogSource ModLog;

        /// <summary>给 Harmony 补丁用的耗电倍率（1 = 原版）。</summary>
        internal static float ConsumptionMultiplier = 1f;

        private static readonly PropertyInfo FreeMovementProperty =
            typeof(EnergyDevTools).GetProperty("FreeMovement", BindingFlags.Public | BindingFlags.Static);

        private float _regenAccumulator;   // 回电累加，攒够 0.25 秒再一次性充，减少 UI 刷新
        private float _regenTimer;
        private float _toastUntil;
        private string _toastText = string.Empty;
        private Font _font;
        private bool _loggedWorldOnce;
        private bool _reportedPatchFailure;

        private void Awake()
        {
            ModLog = Logger;

            RefillKeyEntry = Config.Bind("Recharge", "RefillKey", new KeyboardShortcut(KeyCode.F4),
                "Instantly fill the town's energy storage.");
            FillPercentEntry = Config.Bind("Recharge", "FillPercent", 100f,
                new ConfigDescription("How full to fill on the hotkey, in percent.", new AcceptableValueRange<float>(1f, 100f)));
            RegenPerSecondEntry = Config.Bind("Recharge", "RegenPerSecond", 0f,
                new ConfigDescription("Passively regenerate this much energy per second (0 = off).", new AcceptableValueRange<float>(0f, 100000f)));
            ConsumptionMultiplierEntry = Config.Bind("Cheat", "ConsumptionMultiplier", 0.5f,
                new ConfigDescription("Travel energy cost multiplier (1 = vanilla, 0.5 = half the drain).",
                    new AcceptableValueRange<float>(0f, 1f)));
            NoEnergyDrainEntry = Config.Bind("Cheat", "NoEnergyDrain", false,
                "Never consume energy while travelling at all (overrides ConsumptionMultiplier).");

            ApplyRuntimeConfig();
            ApplyFreeMovement();

            // 耗电折扣：给 Engine.ReturnEnergyCost 加 Postfix。即使这里失败（游戏改版），
            // F4 充满和持续回电仍然可用，只是不打折而已。
            try
            {
                Harmony harmony = new Harmony(Guid);
                harmony.PatchAll(typeof(EnergyCostPatch));
                Logger.LogInfo("[Recharge] patch applied. Energy cost x" + ConsumptionMultiplier + ".");
            }
            catch (Exception e)
            {
                Logger.LogError("[Recharge] could not patch Engine.ReturnEnergyCost (game build mismatch?): " + e);
            }

            Logger.LogInfo("[Recharge] ready. " + RefillKeyEntry.Value + " = instant refill, consumption x" + ConsumptionMultiplier
                + ", regen " + RegenPerSecondEntry.Value + "/s, no-drain " + NoEnergyDrainEntry.Value + ".");
        }

        private void Update()
        {
            try
            {
                EnergyGrid grid = GetGrid();

                if (RefillKeyEntry.Value.IsDown())
                {
                    Refill(grid);
                }

                if (grid != null)
                {
                    if (!_loggedWorldOnce)
                    {
                        _loggedWorldOnce = true;
                        Log("[Recharge] grid ready: " + Fmt(grid.ReturnStorageEnergy()) + " / " + Fmt(grid.ReturnStorageCapacity())
                            + " energy, current travel range " + Fmt(GetTravelRange()) + " units.");
                    }
                    Regenerate(grid);
                }
                else
                {
                    _loggedWorldOnce = false;
                }
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
        }

        /// <summary>持续回电：按秒累积，每 0.25 秒真正充一次，避免每帧刷新 UI。</summary>
        private void Regenerate(EnergyGrid grid)
        {
            if (RegenPerSecondEntry.Value <= 0f)
            {
                return;
            }

            float dt = GameSpeedManager.PausableUnscaledDeltaTime;
            if (dt <= 0f)
            {
                return;
            }

            _regenAccumulator += RegenPerSecondEntry.Value * dt;
            _regenTimer += dt;
            if (_regenTimer < 0.25f || _regenAccumulator <= 0f)
            {
                return;
            }

            float amount = _regenAccumulator;
            _regenAccumulator = 0f;
            _regenTimer = 0f;

            try
            {
                grid.FillStorageEnergy(amount);
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
        }

        /// <summary>F4：把电量充到指定百分比，并重读一次配置文件。</summary>
        private void Refill(EnergyGrid grid)
        {
            try
            {
                Config.Reload();
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }

            ApplyRuntimeConfig();
            ApplyFreeMovement();

            if (grid == null)
            {
                ShowToast("没有找到城镇电网（先进入存档）");
                return;
            }

            float capacity = grid.ReturnStorageCapacity();
            if (capacity <= 0f)
            {
                ShowToast("当前没有储电设备");
                return;
            }

            float target = capacity * Mathf.Clamp(FillPercentEntry.Value, 1f, 100f) / 100f;
            float current = grid.ReturnStorageEnergy();
            float added = 0f;
            if (target > current)
            {
                added = grid.FillStorageEnergy(target - current);
            }

            string text = "电量 " + Fmt(grid.ReturnStorageEnergy()) + " / " + Fmt(capacity)
                + "（+" + Fmt(added) + "，约可航行 " + Fmt(GetTravelRange()) + "）";
            ShowToast(text);
            Log("[Recharge] refilled: " + text);
        }

        /// <summary>把配置里的数值同步到运行期字段（耗电倍率有 0~1 的夹紧）。</summary>
        private void ApplyRuntimeConfig()
        {
            float multiplier = NoEnergyDrainEntry.Value ? 0f : ConsumptionMultiplierEntry.Value;
            ConsumptionMultiplier = Mathf.Clamp01(multiplier);
        }

        /// <summary>用游戏自带的 EnergyDevTools.FreeMovement 开关实现"完全不掉电"。</summary>
        private void ApplyFreeMovement()
        {
            try
            {
                if (FreeMovementProperty == null)
                {
                    if (!_reportedPatchFailure)
                    {
                        _reportedPatchFailure = true;
                        Log("[Recharge] EnergyDevTools.FreeMovement not found, no-drain option unavailable.");
                    }
                    return;
                }
                MethodInfo setter = FreeMovementProperty.GetSetMethod(true);
                if (setter == null)
                {
                    return;
                }
                setter.Invoke(null, new object[] { NoEnergyDrainEntry.Value });
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
        }

        /// <summary>当前电量大约还能航行多少世界单位（游戏自己的换算：电量 / 每单位耗电）。</summary>
        private static float GetTravelRange()
        {
            try
            {
                Community community = Community.PlayerCommunity;
                if (community == null || community.Engine == null)
                {
                    return 0f;
                }
                return community.Engine.ReturnEnergyRange();
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>取城镇电网（主菜单里为 null）。</summary>
        internal static EnergyGrid GetGrid()
        {
            try
            {
                Community community = Community.PlayerCommunity;
                if (community == null)
                {
                    return null;
                }
                Engine engine = community.Engine;
                if (engine == null)
                {
                    return null;
                }
                return engine.EnergyGrid;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ UI

        private void ShowToast(string text)
        {
            _toastText = text;
            _toastUntil = Time.unscaledTime + 3f;
        }

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

                float width = 620f;
                GUI.Label(new Rect((Screen.width - width) * 0.5f, 90f, width, 32f), _toastText, style);
            }
            catch (Exception e)
            {
                LogThrottled(e);
                _toastUntil = 0f;
            }
        }

        // --------------------------------------------------------------- misc

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
                    ModLog.LogError("[Recharge] " + e);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Harmony 后置补丁：把 Engine.ReturnEnergyCost（距离 → 耗电）的返回值乘上倍率。
    /// 这个方法被 ConsumeEnergy 和 UI 的"还能跑多远"估算共用，所以打折后两处一起生效。
    /// </summary>
    [HarmonyPatch(typeof(Engine), "ReturnEnergyCost")]
    internal static class EnergyCostPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref float __result)
        {
            try
            {
                __result *= RechargePlugin.ConsumptionMultiplier;
            }
            catch
            {
                // 补丁里不能抛异常，否则会影响游戏原本的耗电计算
            }
        }
    }
}
