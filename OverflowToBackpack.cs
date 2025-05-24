/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Oxide.Core;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Overflow To Backpack", "VisEntities", "1.3.1")]
    [Description("Sends overflow items to your backpack when your inventory is full.")]
    public class OverflowToBackpack : RustPlugin
    {
        #region Fields

        private static OverflowToBackpack _plugin;
        private static Configuration _config;
        private StoredData _storedData;

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
            
            [JsonProperty("Enable Looted Items")]
            public bool EnableLootedItems { get; set; }

            [JsonProperty("Send Game Tip Notification")]
            public bool SendGameTipNotification { get; set; }

            [JsonProperty("Overflow Toggle Chat Command")]
            public string OverflowToggleChatCommand { get; set; }
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

            if (string.Compare(_config.Version, "1.1.0") < 0)
            {
                _config.EnableLootedItems = defaultConfig.EnableLootedItems;
            }

            if (string.Compare(_config.Version, "1.2.0") < 0)
            {
                _config.SendGameTipNotification = defaultConfig.SendGameTipNotification;
            }

            if (string.Compare(_config.Version, "1.3.0") < 0)
            {
                _config.OverflowToggleChatCommand = defaultConfig.OverflowToggleChatCommand;
            }

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
                EnableDroppedItems = true,
                EnableLootedItems = true,
                SendGameTipNotification = true,
                OverflowToggleChatCommand = "overflow"
            };
        }

        #endregion Configuration

        #region Stored Data

        public class StoredData
        {
            [JsonProperty("Overflow Enabled")]
            public Dictionary<ulong, bool> OverflowEnabled = new Dictionary<ulong, bool>();
        }

        #endregion Stored Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
            _storedData = DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());
            cmd.AddChatCommand(_config.OverflowToggleChatCommand, this, nameof(cmdToggleOverflow));

            if (!_config.EnableGatheredResources)
            {
                Unsubscribe(nameof(OnDispenserGather));
                Unsubscribe(nameof(OnDispenserBonus));
            }

            if (!_config.EnableCollectibles)
                Unsubscribe(nameof(OnCollectiblePickup));

            if (!_config.EnableDroppedItems)
                Unsubscribe(nameof(OnItemPickup));

            if (!_config.EnableLootedItems)
                Unsubscribe(nameof(CanMoveItem));
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

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !OverflowEnabledFor(player))
                return null;

            if (!HasBackpack(player))
                return null;

            if (!PlayerInventoryFull(player, item))
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

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !OverflowEnabledFor(player))
                return;

            if (!HasBackpack(player))
                return;

            if (!PlayerInventoryFull(player, item))
                return;

            int originalAmount = item.amount;
            _plugin.NextTick(() =>
            {
                if (player == null || item == null || !HasBackpack(player))
                    return;

                bool moved = TryMoveItemToBackpack(player, item, originalAmount);
            });
        }

        private object OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (player == null || collectible == null || collectible.itemList == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !OverflowEnabledFor(player))
                return null;

            Item backpackItem = player.inventory.GetBackpackWithInventory();
            if (backpackItem == null || backpackItem.contents == null)
                return null;

            ItemContainer backpack = backpackItem.contents;

            bool needOverride = false;
            int freeSlots = backpack.capacity - backpack.itemList.Count;

            Dictionary<ItemDefinition, int> stackSpace = new Dictionary<ItemDefinition, int>();
            foreach (Item existing in backpack.itemList)
            {
                if (existing.amount >= existing.info.stackable) continue;

                int spare = existing.info.stackable - existing.amount;
                if (stackSpace.TryGetValue(existing.info, out int current))
                    stackSpace[existing.info] = current + spare;
                else
                    stackSpace.Add(existing.info, spare);
            }

            foreach (var ia in collectible.itemList)
            {
                int amount = (int)ia.amount;
                var itemDef = ia.itemDef;

                Item probe = ItemManager.Create(itemDef, amount, 0UL, true);
                bool invFull = PlayerInventoryFull(player, probe);
                probe.Remove();

                if (!invFull)
                    continue;

                needOverride = true;

                if (stackSpace.TryGetValue(itemDef, out int spareInStacks) && spareInStacks > 0)
                {
                    int used = Mathf.Min(spareInStacks, amount);
                    amount -= used;
                    stackSpace[itemDef] = spareInStacks - used;
                }

                if (amount > 0)
                {
                    int stackSize = itemDef.stackable;
                    int slotsNeeded = Mathf.CeilToInt(amount / (float)stackSize);

                    freeSlots -= slotsNeeded;
                    if (freeSlots < 0)
                        return null;
                }
            }

            if (!needOverride)
                return null;

            foreach (var ia in collectible.itemList)
            {
                int amount = (int)ia.amount;
                Item newItem = ItemManager.Create(ia.itemDef, amount, 0UL, true);
                if (newItem == null) continue;

                if (!TryMoveItemToBackpack(player, newItem, amount))
                    newItem.Remove();
            }

            collectible.Kill();
            return true;
        }

        private object OnItemPickup(Item item, BasePlayer player)
        {
            if (player == null || item == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !OverflowEnabledFor(player))
                return null;

            if (!HasBackpack(player))
                return null;

            if (!PlayerInventoryFull(player, item))
                return null;

            int originalAmount = item.amount;
            bool moved = TryMoveItemToBackpack(player, item, originalAmount);
            if (moved)
                return true;

            return null;
        }

        private object CanMoveItem(Item item, PlayerInventory playerInventory)
        {
            if (item == null || playerInventory == null)
                return null;

            BasePlayer player = playerInventory.baseEntity;
            if (player == null)
                return null;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE) || !OverflowEnabledFor(player))
                return null;

            if (!HasBackpack(player))
                return null;

            ItemContainer sourceContainer = item.parent;
            bool isFromPlayerInventory = sourceContainer == player.inventory.containerMain ||
                                         sourceContainer == player.inventory.containerBelt ||
                                         (player.inventory.GetBackpackWithInventory() != null &&
                                          sourceContainer == player.inventory.GetBackpackWithInventory().contents);

            if (isFromPlayerInventory)
                return null;

            if (!PlayerInventoryFull(player, item))
                return null;

            bool moved = TryMoveItemToBackpack(player, item, item.amount);
            if (moved)
                return true;

            return null;
        }

        #endregion Oxide Hooks

        #region Inventory Utilities

        private bool HasBackpack(BasePlayer player)
        {
            return player.inventory.GetBackpackWithInventory() != null;
        }

        private bool PlayerInventoryFull(BasePlayer player, Item item)
        {
            if (ContainerHasSpaceForItem(player.inventory.containerMain, item))
                return false;

            if (ContainerHasSpaceForItem(player.inventory.containerBelt, item))
                return false;
            return true;
        }

        private bool ContainerHasSpaceForItem(ItemContainer container, Item item)
        {
            foreach (Item existing in container.itemList)
            {
                if (existing.info == item.info && existing.amount < existing.info.stackable)
                    return true;
            }

            if (container.itemList.Count < container.capacity)
                return true;

            return false;
        }

        private bool TryMoveItemToBackpack(BasePlayer player, Item item, int amount)
        {
            Item backpack = player.inventory.GetBackpackWithInventory();
            if (backpack == null)
                return false;

            if (!ContainerHasSpaceForItem(backpack.contents, item))
                return false;

            bool moved = item.MoveToContainer(backpack.contents, allowStack: true);
            if (moved)
            {
                if (_config.SendGameTipNotification)
                {
                    ShowToast(player, Lang.BackpackReceived, GameTip.Styles.Blue_Normal, amount, item.info.displayName.translated);
                }
            }
            return moved;
        }

        #endregion Inventory Utilities

        #region Helper Functions

        private bool OverflowEnabledFor(BasePlayer player)
        {
            if (!_storedData.OverflowEnabled.TryGetValue(player.userID, out bool isEnabled))
            {
                isEnabled = true;
                _storedData.OverflowEnabled[player.userID] = true;
                DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);
            }
            return isEnabled;
        }

        #endregion Helper Functions

        #region Helper Classes

        public static class DataFileUtil
        {
            private const string FOLDER = "";

            public static void EnsureFolderCreated()
            {
                string path = Path.Combine(Interface.Oxide.DataDirectory, FOLDER);

                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }

            public static string GetFilePath(string filename = null)
            {
                if (filename == null)
                    filename = _plugin.Name;

                return Path.Combine(FOLDER, filename);
            }

            public static string[] GetAllFilePaths(bool filenameOnly = false)
            {
                string[] filePaths = Interface.Oxide.DataFileSystem.GetFiles(FOLDER);

                for (int i = 0; i < filePaths.Length; i++)
                {
                    filePaths[i] = filePaths[i].Substring(0, filePaths[i].Length - 5);

                    if (filenameOnly)
                    {
                        filePaths[i] = Path.GetFileName(filePaths[i]);
                    }
                }
                return filePaths;
            }

            public static bool Exists(string filePath)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filePath);
            }

            public static T Load<T>(string filePath) where T : class, new()
            {
                T data = Interface.Oxide.DataFileSystem.ReadObject<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static T LoadIfExists<T>(string filePath) where T : class, new()
            {
                if (Exists(filePath))
                    return Load<T>(filePath);
                else
                    return null;
            }

            public static T LoadOrCreate<T>(string filePath) where T : class, new()
            {
                T data = LoadIfExists<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static void Save<T>(string filePath, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject<T>(filePath, data);
            }

            public static void Delete(string filePath)
            {
                Interface.Oxide.DataFileSystem.DeleteDataFile(filePath);
            }
        }

        #endregion Helper Classes

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

        #region Commands

        private void cmdToggleOverflow(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
            {
                MessagePlayer(player, Lang.NoPermission);
                return;
            }

            ulong userId = player.userID;

            bool oldValue = OverflowEnabledFor(player);
            bool newValue = !oldValue;

            _storedData.OverflowEnabled[userId] = newValue;
            DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);

            if (newValue)
                MessagePlayer(player, Lang.ToggleOn);
            else
                MessagePlayer(player, Lang.ToggleOff);
        }
        
        #endregion Commands

        #region Localization

        private class Lang
        {
            public const string NoPermission = "NoPermission";
            public const string BackpackReceived = "BackpackReceived";
            public const string ToggleOn = "ToggleOn";
            public const string ToggleOff = "ToggleOff";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.NoPermission] = "You do not have permission to use this command.",
                [Lang.BackpackReceived] = "Your inventory is full! {0} {1} has been moved to your backpack.",
                [Lang.ToggleOn] = "Overflow to backpack is now enabled.",
                [Lang.ToggleOff] = "Overflow to backpack is now disabled."

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