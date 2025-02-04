/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("Overflow To Backpack", "VisEntities", "1.0.0")]
    [Description("Sends overflow items to your backpack when your inventory is full.")]
    public class OverflowToBackpack : RustPlugin
    {
        #region Fields

        private static OverflowToBackpack _plugin;
        private static Configuration _config;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Enable Gathered Resources")]
            public bool EnableGatheredResources { get; set; }

            [JsonProperty("Enable Collectibles")]
            public bool EnableCollectibles { get; set; }

            [JsonProperty("Enable Dropped Items")]
            public bool EnableDroppedItems { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                EnableGatheredResources = true,
                EnableCollectibles = true,
                EnableDroppedItems = true
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();

            if (!_config.EnableGatheredResources)
            {
                Unsubscribe(nameof(OnDispenserGather));
                Unsubscribe(nameof(OnDispenserBonus));
            }

            if (!_config.EnableCollectibles)
                Unsubscribe(nameof(OnCollectiblePickup));

            if (!_config.EnableDroppedItems)
                Unsubscribe(nameof(OnItemPickup));
        }

        private void Unload()
        {
            _config = null;
            _plugin = null;
        }

        private object OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
                return null;

            if (!PlayerInventoryFull(player))
                return null;

            int originalAmount = item.amount;
            Item newItem = ItemManager.Create(item.info, originalAmount, item.skin);
            if (newItem != null)
            {
                bool moved = TryMoveItemToBackpack(player, newItem, originalAmount);
                if (moved)
                    return true;
                else
                    newItem.Remove();
            }
            return null;
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null || item == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
                return;

            if (!PlayerInventoryFull(player))
                return;

            int originalAmount = item.amount;
            _plugin.NextTick(() =>
            {
                bool moved = TryMoveItemToBackpack(player, item, originalAmount);
            });
        }

        private object OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (player == null || collectible == null || collectible.itemList == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
                return null;

            if (!PlayerInventoryFull(player))
                return null;

            foreach (var ia in collectible.itemList)
            {
                int originalAmount = (int)ia.amount;
                Item newItem = ItemManager.Create(ia.itemDef, originalAmount, 0UL, true);
                if (newItem != null)
                {
                    bool moved = TryMoveItemToBackpack(player, newItem, originalAmount);
                    if (!moved)
                        newItem.Remove();
                }
            }
            collectible.Kill();
            return true;
        }

        private object OnItemPickup(Item item, BasePlayer player)
        {
            if (player == null || item == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
                return null;

            if (!PlayerInventoryFull(player))
                return null;

            int originalAmount = item.amount;
            bool moved = TryMoveItemToBackpack(player, item, originalAmount);
            if (moved)
                return true;

            return null;
        }

        #endregion Oxide Hooks

        #region Inventory Utilities

        private bool PlayerInventoryFull(BasePlayer player)
        {
            ItemContainer main = player.inventory.containerMain;
            ItemContainer belt = player.inventory.containerBelt;

            bool mainFull = (main == null || main.itemList.Count >= main.capacity);
            bool beltFull = (belt == null || belt.itemList.Count >= belt.capacity);

            return mainFull && beltFull;
        }

        private bool TryMoveItemToBackpack(BasePlayer player, Item item, int amount)
        {
            Item backpack = player.inventory.GetBackpackWithInventory();
            if (backpack == null || backpack.contents.itemList.Count >= backpack.contents.capacity)
                return false;

            bool moved = item.MoveToContainer(backpack.contents, allowStack: true);
            if (moved)
            {
                ShowToast(player, Lang.BackpackReceived, GameTip.Styles.Blue_Normal, amount, item.info.displayName.translated);
            }
            return moved;
        }

        #endregion Inventory Utilities

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "overflowtobackpack.use";
            private static readonly List<string> _permissions = new List<string>
            {
                USE,
            };

            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Localization

        private class Lang
        {
            public const string NoPermission = "NoPermission";
            public const string BackpackReceived = "BackpackReceived";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.NoPermission] = "You do not have permission to use this command.",
                [Lang.BackpackReceived] = "Your inventory is full! {0} {1} has been moved to your backpack.",

            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string message = _plugin.lang.GetMessage(messageKey, _plugin, player.UserIDString);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void MessagePlayer(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);
            _plugin.SendReply(player, message);
        }

        public static void ShowToast(BasePlayer player, string messageKey, GameTip.Styles style = GameTip.Styles.Blue_Normal, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);
            player.SendConsoleCommand("gametip.showtoast", (int)style, message);
        }

        #endregion Localization
    }
}