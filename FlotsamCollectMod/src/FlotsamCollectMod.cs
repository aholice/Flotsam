// =============================================================================
// Flotsam 收集物数量 Mod（Flotsam Collect Quantity Mod）v1.0.0
// -----------------------------------------------------------------------------
// 【功能】
//   1) 收集倍率：从世界里打捞 / 捡到 / 钓到的物品，交回船舱或仓库时变成 N 个（默认 2）。
//   2) 数量面板：游戏内按 F7 打开物品清单，点 +N / +1 / +99 直接把物品放进船舱或仓库。
//
// 【目标游戏】
//   Flotsam 1.1.0f5（Epic 版），Unity 6000.3.28f1，Mono 后端。
//   依赖 BepInEx 5.4.23.x（提供 BepInEx.dll 与 HarmonyX 的 0Harmony.dll）。
//
// 【核心原理】
//   只挂一个 Harmony 前缀补丁：Inventory.OnItemAdded(Item, SubInventory)。
//   - 这个私有方法在"物品被塞进某个容器"之后立刻执行，是所有物品流转的必经之路。
//   - 补丁跑在方法体之前（Prefix），此时 item.Inventory 还指向上一个容器，
//     所以能判断来源：InventoryType.Flotsam = 刚从世界里捡起来的东西。
//   - 世界捡起的物品会被记进 InFlight 集合；等它最终被放进 Boat / Storage（船/仓库）时，
//     就地再补 (Multiplier - 1) 个同样的物品（先检查目标容器还有没有空间）。
//   - 只对"从世界收集来的物品"生效：玩家在仓库之间倒腾物品、建筑生产物品都不会触发，
//     避免出现无限刷物品的循环。
//
// 【编译】
//   见仓库根目录 README.md；脚本 build.ps1 直接用游戏的 DLL 作引用，不需要 Unity 或 .NET SDK。
// =============================================================================

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FlotsamCollectMod
{
    /// <summary>
    /// 插件入口。负责：绑定配置、打 Harmony 补丁、处理 F7 热键、绘制数量面板。
    /// </summary>
    [BepInPlugin(Guid, PluginName, Version)]
    public class CollectModPlugin : BaseUnityPlugin
    {
        // BepInEx 插件的唯一标识 / 显示名 / 版本
        public const string Guid = "com.dsh.flotsam.collectmod";
        public const string PluginName = "Flotsam Collect Quantity Mod";
        public const string Version = "1.0.0";

        /// <summary>收集倍率配置项（写进 BepInEx\config\com.dsh.flotsam.collectmod.cfg）。</summary>
        internal static ConfigEntry<int> MultiplierEntry;

        /// <summary>打开/关闭数量面板的热键配置项（默认 F7）。</summary>
        internal static ConfigEntry<KeyboardShortcut> PanelKeyEntry;

        /// <summary>插件日志器，方便补丁类里也能写日志。</summary>
        internal static ManualLogSource ModLog;

        /// <summary>运行期使用的倍率（从配置读出来并夹紧后缓存，避免每帧读配置）。</summary>
        internal static int Multiplier = 2;

        // IMGUI 窗口 id（随便一个唯一数字即可，避免和游戏/其它插件窗口冲突）
        private const int WindowId = 0x464C4F54;

        // 面板窗口的位置与大小（可拖动，GUILayout.Window 会自己记住）
        private Rect _windowRect = new Rect(360f, 90f, 600f, 620f);

        private bool _showPanel;                 // 面板当前是否打开
        private Font _font;                      // IMGUI 用的中文字体（系统字体动态加载）
        private Vector2 _scroll;                 // 物品列表的滚动位置
        private string _filter = string.Empty;   // 搜索框内容
        private string _amountText = "10";       // "+N" 按钮里的 N
        private int _lastGuiErrorFrame = -1000;  // UI 异常限流用（避免每帧刷屏）
        private ItemProperties[] _cachedItems;   // 缓存的物品表（按名字排序）
        private string[] _cachedNames;           // 与 _cachedItems 一一对应的显示名
        private bool _drewOnce;                  // 是否已经打过"面板已绘制"诊断日志

        /// <summary>
        /// Unity 生命周期：插件加载时调用一次。
        /// 这里完成配置绑定、补丁注入和启动日志。
        /// </summary>
        private void Awake()
        {
            ModLog = Logger;

            // 收集倍率：1 = 原版，上限 20（再多也基本是仓库先满）
            MultiplierEntry = Config.Bind(
                "Collection", "Multiplier", 2,
                new ConfigDescription(
                    "Every item collected from the world is delivered this many times into your boat/storage. 1 = vanilla.",
                    new AcceptableValueRange<int>(1, 20)));

            // 面板热键（默认 F7，和船速 Mod 的 F6、电量 Mod 的 F5 错开）
            PanelKeyEntry = Config.Bind(
                "Interface", "PanelKey", new KeyboardShortcut(KeyCode.F7),
                "Key that opens the item quantity panel.");

            // 诊断用：进游戏自动打开面板，方便确认插件真的加载了
            bool openAtStart = Config.Bind(
                "Interface", "OpenPanelAtStart", false,
                "Open the quantity panel automatically (mostly useful to verify the mod loaded).").Value;

            Multiplier = Mathf.Clamp(MultiplierEntry.Value, 1, 20);

            if (openAtStart)
            {
                _showPanel = true;
                EnsureFont();
            }

            // 打补丁：PatchAll 会扫描 ItemCollectPatch 上声明的目标方法。
            // 如果游戏更新导致目标方法不存在，这里会抛异常——只记日志，不影响游戏本体运行。
            try
            {
                Harmony harmony = new Harmony(Guid);
                harmony.PatchAll(typeof(ItemCollectPatch));
                Logger.LogInfo("[CollectMod] patch applied. Multiplier = " + Multiplier);
            }
            catch (Exception e)
            {
                Logger.LogError("[CollectMod] could not apply patch (game build mismatch?): " + e);
            }
        }

        /// <summary>
        /// 每帧处理热键。GameSpeed 暂停时也要能开面板，所以放 Update 而不是 FixedUpdate。
        /// </summary>
        private void Update()
        {
            try
            {
                if (PanelKeyEntry != null && PanelKeyEntry.Value.IsDown())
                {
                    _showPanel = !_showPanel;
                    if (_showPanel)
                    {
                        EnsureFont();
                        // 每次打开都重新取一次物品表：读档前后物品表内容可能变化
                        _cachedItems = null;
                    }
                }
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }
        }

        /// <summary>
        /// 加载一个支持中文的系统字体给 IMGUI 用。
        /// Unity 默认 IMGUI 字体不含 CJK 字形，中文会显示成方块；
        /// Font.CreateDynamicFontFromOSFont 会走系统字体，能正常渲染中日韩文字。
        /// </summary>
        private void EnsureFont()
        {
            if (_font != null)
            {
                return;
            }
            try
            {
                _font = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun", "Arial Unicode MS" },
                    15);
            }
            catch
            {
                // 找不到中文字体就退回默认字体（英文仍可读），不抛给上层
                _font = null;
            }
        }

        /// <summary>
        /// IMGUI 绘制入口。只在面板打开时工作；
        /// 整段 try/catch，任何 UI 异常只写一次日志并自动关闭面板，绝不刷屏影响帧率。
        /// </summary>
        private void OnGUI()
        {
            if (!_showPanel)
            {
                return;
            }
            try
            {
                if (_font != null && GUI.skin != null && GUI.skin.font != _font)
                {
                    GUI.skin.font = _font;
                }
                _windowRect = GUILayout.Window(WindowId, _windowRect, DrawPanel, "Flotsam 收集物数量修改器  (F7 关闭)");
                if (!_drewOnce)
                {
                    _drewOnce = true;
                    if (ModLog != null)
                    {
                        ModLog.LogInfo("[CollectMod] panel drawn, items available: " + (_cachedItems == null ? -1 : _cachedItems.Length) + ", cjk font: " + (_font != null));
                    }
                }
            }
            catch (Exception e)
            {
                if (Time.frameCount - _lastGuiErrorFrame > 300)
                {
                    _lastGuiErrorFrame = Time.frameCount;
                    if (ModLog != null)
                    {
                        ModLog.LogError("[CollectMod] UI error: " + e);
                    }
                }
                _showPanel = false;
            }
        }

        /// <summary>
        /// 面板内容：顶栏（倍数 / 每次添加数量 / 搜索）+ 可滚动的物品列表。
        /// </summary>
        private void DrawPanel(int id)
        {
            Community community = Community.PlayerCommunity;
            GUILayout.BeginVertical();

            // 主菜单里 PlayerCommunity 为 null，此时只提示，不画列表
            if (community == null)
            {
                GUILayout.Label("请先载入存档（当前不在游戏中）。");
                GUILayout.EndVertical();
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
                return;
            }

            GUILayout.Label("收集倍率：" + Multiplier + "（改 BepInEx\\config 里的 .cfg 后重启游戏生效）");

            int amount = ParseAmount(_amountText);
            GUILayout.BeginHorizontal();
            GUILayout.Label("每次添加:", GUILayout.Width(70f));
            _amountText = GUILayout.TextField(_amountText, GUILayout.Width(50f));
            GUILayout.Label("搜索:", GUILayout.Width(40f));
            _filter = GUILayout.TextField(_filter ?? string.Empty, GUILayout.Width(150f));
            GUILayout.Label("点按钮 = 直接放进船舱/仓库", GUILayout.Width(200f));
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            if (_cachedItems == null)
            {
                RefreshItemCache();
            }

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(520f));
            if (_cachedItems == null || _cachedItems.Length == 0)
            {
                GUILayout.Label("物品列表为空：请在游戏里载入存档后再打开。");
            }
            else
            {
                string filter = (_filter ?? string.Empty).Trim();
                for (int i = 0; i < _cachedItems.Length; i++)
                {
                    ItemProperties props = _cachedItems[i];
                    if (props == null)
                    {
                        continue;
                    }
                    string name = _cachedNames[i];
                    if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(name, GUILayout.Width(250f));
                    if (GUILayout.Button("+" + amount, GUILayout.Width(70f)))
                    {
                        Grant(props, amount);
                    }
                    if (GUILayout.Button("+1", GUILayout.Width(45f)))
                    {
                        Grant(props, 1);
                    }
                    if (GUILayout.Button("+99", GUILayout.Width(45f)))
                    {
                        Grant(props, 99);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        /// <summary>
        /// 取一次全部物品定义并缓存（按显示名排序）。
        ///
        /// 注意：不能用 ItemProperties.Identified —— 那个静态列表在正式版里从来没被填充过（永远是空的）。
        /// 正确来源是游戏自己的物品表 GameManager.Settings.ItemSettings.ItemProperties，
        /// 和造物/生成器面板用的是同一份数据。
        /// </summary>
        private void RefreshItemCache()
        {
            try
            {
                ItemProperties[] source = null;
                try
                {
                    if (GameManager.Settings != null && GameManager.Settings.ItemSettings != null)
                    {
                        source = GameManager.Settings.ItemSettings.ItemProperties;
                    }
                }
                catch
                {
                    source = null;
                }

                if (source == null || source.Length == 0)
                {
                    _cachedItems = new ItemProperties[0];
                    _cachedNames = new string[0];
                    return;
                }

                List<ItemProperties> items = new List<ItemProperties>(source.Length);
                List<string> names = new List<string>(source.Length);
                for (int i = 0; i < source.Length; i++)
                {
                    ItemProperties p = source[i];
                    if (p == null)
                    {
                        continue;
                    }
                    items.Add(p);
                    names.Add(SafeName(p));
                }

                // 物品数量不到两百个，直接插入排序（稳定、无额外分配、代码也短）
                for (int i = 1; i < items.Count; i++)
                {
                    int j = i;
                    while (j > 0 && string.CompareOrdinal(names[j - 1], names[j]) > 0)
                    {
                        string swapName = names[j];
                        names[j] = names[j - 1];
                        names[j - 1] = swapName;

                        ItemProperties swapItem = items[j];
                        items[j] = items[j - 1];
                        items[j - 1] = swapItem;
                        j--;
                    }
                }

                _cachedItems = items.ToArray();
                _cachedNames = names.ToArray();
            }
            catch (Exception e)
            {
                _cachedItems = new ItemProperties[0];
                _cachedNames = new string[0];
                LogThrottled(e);
            }
        }

        /// <summary>
        /// 取物品显示名：优先本地化名（中文），失败则退回资源名，再失败给个占位符。
        /// 每个环节都单独 try，避免某个物品的本地化数据缺失把整个面板搞崩。
        /// </summary>
        private static string SafeName(ItemProperties props)
        {
            try
            {
                string localized = props.LocalizedName;
                if (!string.IsNullOrEmpty(localized))
                {
                    return localized;
                }
            }
            catch
            {
            }
            try
            {
                return props.name;
            }
            catch
            {
                return "<unknown>";
            }
        }

        /// <summary>解析"+N"输入框，非法输入回退到 10，并夹紧到 1~9999。</summary>
        private static int ParseAmount(string text)
        {
            int value;
            if (!string.IsNullOrEmpty(text) && int.TryParse(text.Trim(), out value))
            {
                return Mathf.Clamp(value, 1, 9999);
            }
            return 10;
        }

        /// <summary>
        /// 把指定物品放进玩家的船 / 仓库。先放船（通常空间大、又在手边），再放各存储建筑。
        /// 返回实际放进去的数量（放不下就少放，不会硬塞爆容量）。
        /// </summary>
        internal static int Grant(ItemProperties props, int amount)
        {
            if (props == null || amount <= 0)
            {
                return 0;
            }

            Community community = Community.PlayerCommunity;
            if (community == null)
            {
                return 0;
            }

            int remaining = amount;

            // 1) 所有船（ReturnAllBoats(false) = 包含建造中的船，避免刚开始没地方放）
            try
            {
                List<Boat> boats = community.ReturnAllBoats(false);
                if (boats != null)
                {
                    for (int i = 0; i < boats.Count && remaining > 0; i++)
                    {
                        Boat boat = boats[i];
                        if (boat == null || boat.Buildable == null)
                        {
                            continue;
                        }
                        remaining -= AddToInventory(boat.Buildable.Inventory, props, remaining);
                    }
                }
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }

            // 2) 所有仓储建筑
            try
            {
                List<Storage> storages = community.Storages;
                if (storages != null)
                {
                    for (int i = 0; i < storages.Count && remaining > 0; i++)
                    {
                        Storage storage = storages[i];
                        if (storage == null || storage.Buildable == null)
                        {
                            continue;
                        }
                        remaining -= AddToInventory(storage.Buildable.Inventory, props, remaining);
                    }
                }
            }
            catch (Exception e)
            {
                LogThrottled(e);
            }

            return amount - remaining;
        }

        /// <summary>
        /// 往单个容器里放 count 个物品。
        /// 每次都先用 FitsInInventory 预判空间：游戏自己的 AddItem 在放不下时会 Debug.LogException，
        /// 直接硬塞会刷一屏红字，所以这里放不下就提前 break。
        /// </summary>
        private static int AddToInventory(Inventory inventory, ItemProperties props, int count)
        {
            if (inventory == null || props == null || count <= 0)
            {
                return 0;
            }

            int added = 0;
            for (int i = 0; i < count; i++)
            {
                Item item = new Item(props);
                try
                {
                    if (!inventory.FitsInInventory(item, SubInventoryType.Storage))
                    {
                        break;
                    }
                    if (!inventory.AddItem(item, SubInventoryType.Storage))
                    {
                        break;
                    }
                }
                catch
                {
                    break;
                }
                added++;
            }
            return added;
        }

        /// <summary>统一的错误日志出口（补丁类里也会调用）。</summary>
        internal static void LogThrottled(Exception e)
        {
            try
            {
                if (ModLog != null)
                {
                    ModLog.LogError("[CollectMod] " + e);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 核心补丁：Inventory.OnItemAdded(Item, SubInventory) 的前缀。
    ///
    /// 为什么用前缀而不是后缀：
    ///   OnItemAdded 方法体第一件事就是 item.SetInventory(this, ...)，把 item.Inventory 改成新容器。
    ///   只有在前缀里读 item.Inventory，拿到的才是"物品从哪来"，也就是判断它是不是刚从世界里捡起来的。
    ///
    /// 两段式逻辑：
    ///   1) 来源是 Flotsam（打捞点 / 海面漂浮物 / 鱼群）→ 记进 InFlight，等它被交付；
    ///   2) 目的地是 Boat / Storage 且物品在 InFlight 里 → 说明"收集物到货了"，补 (倍率-1) 个。
    /// 这样仓库之间搬东西（来源是 Storage）不会触发复制，避免无限刷物品。
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "OnItemAdded", new Type[] { typeof(Item), typeof(SubInventory) })]
    internal static class ItemCollectPatch
    {
        // 已经从世界里捡起来、但还没交付到船/仓库的物品实例（用引用比较，Item 没重写 Equals）
        private static readonly HashSet<Item> InFlight = new HashSet<Item>();

        // 递归保护：补物品时会再次触发 OnItemAdded，不加锁会无限套娃
        private static bool _busy;

        private static bool _sawWorldPickup;   // 是否已经打过"检测到世界拾取"日志
        private static int _dupeCount;         // 累计额外复制了多少个物品（诊断用）
        private static int _lastReport;        // 上次打日志时的累计值

        /// <summary>
        /// 补丁主体。参数名必须和游戏方法一致（__instance = 目标 Inventory 实例）。
        /// </summary>
        [HarmonyPrefix]
        private static void Prefix(Inventory __instance, Item item, SubInventory subInventory)
        {
            // 正在复制物品时，游戏为复制品再次调用 OnItemAdded，这里直接跳过
            if (_busy)
            {
                return;
            }

            try
            {
                int multiplier = CollectModPlugin.Multiplier;
                if (multiplier <= 1 || __instance == null || item == null || subInventory == null)
                {
                    return;
                }

                // ---- 第一段：判断来源，是"世界里捡的"就登记在途 ----
                InventoryBase source = item.Inventory;
                if (source != null && source.Type == InventoryType.Flotsam)
                {
                    InFlight.Remove(item);
                    InFlight.Add(item);
                    if (!_sawWorldPickup)
                    {
                        _sawWorldPickup = true;
                        if (CollectModPlugin.ModLog != null)
                        {
                            CollectModPlugin.ModLog.LogInfo("[CollectMod] world pickup detected (" + item.Properties.name + "), multiplier active.");
                        }
                    }
                }

                // ---- 第二段：只在"交付进船/仓库"时复制 ----
                InventoryType destination = __instance.Type;
                if (destination != InventoryType.Boat && destination != InventoryType.Storage)
                {
                    return;
                }

                // 不属于"从世界收集"的物品（比如仓库互搬），直接放过
                if (!InFlight.Remove(item))
                {
                    return;
                }

                // 任务物品、以及打捞/钓鱼用的标记物不能复制，否则会破坏任务进度
                ItemProperties props = item.Properties;
                if (props == null || props.IsQuestItem)
                {
                    return;
                }
                Item.Tags skip = Item.Tags.Quest | Item.Tags.SalvageMarker | Item.Tags.FishMarker;
                if ((props.Tags & skip) != 0)
                {
                    return;
                }

                // ---- 复制：原物品已进容器，这里只补倍率-1 个 ----
                int extra = 0;
                _busy = true;
                try
                {
                    for (int i = 1; i < multiplier; i++)
                    {
                        Item copy = new Item(props, __instance, subInventory.Type);
                        if (!__instance.FitsInInventory(copy, subInventory.Type))
                        {
                            break;   // 容器满了就不再放，避免游戏打异常日志
                        }
                        if (!__instance.AddItem(copy, subInventory.Type))
                        {
                            break;
                        }
                        extra++;
                    }
                }
                finally
                {
                    _busy = false;
                }

                if (extra > 0)
                {
                    _dupeCount += extra;
                    if (_dupeCount - _lastReport >= 25 && CollectModPlugin.ModLog != null)
                    {
                        _lastReport = _dupeCount;
                        CollectModPlugin.ModLog.LogInfo("[CollectMod] +" + extra + " x " + props.name + " (extra items total: " + _dupeCount + ")");
                    }
                }
            }
            catch (Exception e)
            {
                // 补丁里出任何问题都必须吞掉：Harmony 前缀抛异常会中断游戏原本的逻辑
                _busy = false;
                CollectModPlugin.LogThrottled(e);
            }
        }
    }
}
