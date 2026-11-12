// Flotsam Energy Capacity Mod
// ---------------------------------------------------------------------------
// Makes the town's energy bar hold more.
//
// The total capacity of an energy grid is the sum of every EnergyStorage's
// private serialized field:
//     _energyCapacity = 1000f
// and the HUD bar / overview panel read that same value, so scaling the field
// makes both the bar and the real storage bigger.
//
// Only EnergyAmount is saved per storage (EnergyStoragePersistentData), the
// capacity is not, so scaling it at runtime cannot corrupt a save.
//
//   F5 = re-read the config file and re-apply (no restart needed)
//
// Built against the retail Unity 6.3 / Mono build of Flotsam 1.1.0f5.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace FlotsamEnergyMod
{
    [BepInPlugin(Guid, PluginName, Version)]
    public class EnergyPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.dsh.flotsam.energycap";
        public const string PluginName = "Flotsam Energy Capacity";
        public const string Version = "1.0.0";

        private const string CapacityFieldName = "_energyCapacity";
        private static readonly FieldInfo CapacityField =
            typeof(EnergyStorage).GetField(CapacityFieldName, BindingFlags.Instance | BindingFlags.NonPublic);

        internal static ConfigEntry<float> MultiplierEntry;
        internal static ConfigEntry<bool> RefillEntry;
        internal static ConfigEntry<KeyboardShortcut> ReloadKeyEntry;
        internal static ManualLogSource ModLog;

        private readonly Dictionary<EnergyStorage, float> _baseCapacity = new Dictionary<EnergyStorage, float>();
        private readonly List<EnergyStorage> _buffer = new List<EnergyStorage>();
        private float _nextScan;
        private float _toastUntil;
        private string _toastText = string.Empty;
        private Font _font;
        private bool _reportedMissingField;

        private void Awake()
        {
            ModLog = Logger;

            MultiplierEntry = Config.Bind("Energy", "CapacityMultiplier", 10f,
                new ConfigDescription("Multiplies the capacity of every energy storage (1 = vanilla).",
                    new AcceptableValueRange<float>(1f, 50f)));
            RefillEntry = Config.Bind("Energy", "RefillOnScale", true,
                "When a storage was already full, top it up to its new capacity (a full battery stays full when the tank grows).");
            ReloadKeyEntry = Config.Bind("Keys", "Reload", new KeyboardShortcut(KeyCode.F5),
                "Re-read this config file and re-apply the multiplier.");

            if (CapacityField == null)
            {
                Logger.LogError("[Energy] EnergyStorage._energyCapacity not found - this game build is not supported.");
            }
            else
            {
                Logger.LogInfo("[Energy] ready. Capacity x" + MultiplierEntry.Value + ", reload key " + ReloadKeyEntry.Value + ".");
            }
        }

        private void Update()
        {
            try
            {
                if (ReloadKeyEntry.Value.IsDown())
                {
                    ReloadAndApply();
                }

                if (Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + 2f;
                    ScanAndApply(false);
                }
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
        }

        private void ReloadAndApply()
        {
            try
            {
                Config.Reload();
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
            ScanAndApply(true);
            ShowToast("电量容量 x" + Fmt(MultiplierEntry.Value));
        }

        private void ScanAndApply(bool reapplyAll)
        {
            if (CapacityField == null)
            {
                if (!_reportedMissingField)
                {
                    _reportedMissingField = true;
                    Log("[Energy] capacity field missing, nothing to do.");
                }
                return;
            }

            CollectStorages(_buffer);

            // Drop references to storages that were removed / destroyed.
            if (_baseCapacity.Count > 0)
            {
                List<EnergyStorage> dead = null;
                foreach (KeyValuePair<EnergyStorage, float> pair in _baseCapacity)
                {
                    if (pair.Key == null)
                    {
                        if (dead == null)
                        {
                            dead = new List<EnergyStorage>();
                        }
                        dead.Add(pair.Key);
                    }
                }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++)
                    {
                        _baseCapacity.Remove(dead[i]);
                    }
                }
            }

            int scaled = 0;
            for (int i = 0; i < _buffer.Count; i++)
            {
                EnergyStorage storage = _buffer[i];
                if (storage == null)
                {
                    continue;
                }

                bool isNew = !_baseCapacity.ContainsKey(storage);
                if (!isNew && !reapplyAll)
                {
                    continue;
                }

                Apply(storage, isNew);
                if (isNew)
                {
                    scaled++;
                }
            }

            if (scaled > 0)
            {
                Log("[Energy] scaled " + scaled + " storage(s) to x" + Fmt(MultiplierEntry.Value) + " (now " + _baseCapacity.Count + " tracked).");
            }
            else if (reapplyAll)
            {
                Log("[Energy] re-applied x" + Fmt(MultiplierEntry.Value) + " to " + _baseCapacity.Count + " storage(s).");
            }
        }

        private float Apply(EnergyStorage storage, bool isNew)
        {
            float baseCapacity;
            if (isNew)
            {
                baseCapacity = ReadField(storage);
                if (baseCapacity <= 0.01f)
                {
                    baseCapacity = 1000f;
                }
                _baseCapacity[storage] = baseCapacity;
            }
            else
            {
                baseCapacity = _baseCapacity[storage];
            }

            float target = baseCapacity * Mathf.Max(1f, MultiplierEntry.Value);

            float amountBefore = 0f;
            try
            {
                amountBefore = storage.EnergyAmount;
            }
            catch
            {
            }

            try
            {
                CapacityField.SetValue(storage, target);
            }
            catch (Exception e)
            {
                LogThrottled(e);
                return 0f;
            }

            // A battery that was full before the scaling should stay full afterwards.
            if (isNew && RefillEntry.Value && baseCapacity > 0.01f && amountBefore >= baseCapacity - 0.5f && amountBefore < target)
            {
                try
                {
                    storage.SetEnergyAmount(target);
                }
                catch (Exception e)
                {
                    LogThrottled(e);
                }
            }

            // Keep the stored amount sane if the multiplier was lowered.
            try
            {
                if (storage.EnergyAmount > target)
                {
                    storage.SetEnergyAmount(target);
                }
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }

            return target;
        }

        private static float ReadField(EnergyStorage storage)
        {
            try
            {
                object value = CapacityField.GetValue(storage);
                if (value is float)
                {
                    return (float)value;
                }
            }
            catch
            {
            }
            return 0f;
        }

        private static void CollectStorages(List<EnergyStorage> buffer)
        {
            buffer.Clear();
            try
            {
                EnergyStorage[] all = Resources.FindObjectsOfTypeAll<EnergyStorage>();
                for (int i = 0; i < all.Length; i++)
                {
                    EnergyStorage storage = all[i];
                    if (storage == null)
                    {
                        continue;
                    }
                    GameObject go = storage.gameObject;
                    if (go == null)
                    {
                        continue;
                    }
                    // Skip prefab assets, only touch objects living in a loaded scene.
                    if (!go.scene.IsValid() || !go.scene.isLoaded)
                    {
                        continue;
                    }
                    buffer.Add(storage);
                }
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------ UI

        private void ShowToast(string text)
        {
            _toastText = text;
            _toastUntil = Time.unscaledTime + 2.5f;
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

                float width = 360f;
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
                    ModLog.LogError("[Energy] " + e);
                }
            }
            catch
            {
            }
        }
    }
}
