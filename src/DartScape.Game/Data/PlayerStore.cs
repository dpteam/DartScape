using System;
using System.Collections.Generic;
using System.IO;
using DartScape.Core.Configuration;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;
using DartScape.Core.Security;
using DartScape.Game;

namespace DartScape.Game.Data
{
    /// <summary>
    /// Loads and saves player characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Persistence is deliberately boring: one INI-shaped text file per account under
    /// <c>data/characters</c>, written to a temporary file and then renamed, so a power
    /// cut during a save leaves the previous good file intact. A database would be
    /// nicer at ten thousand accounts and considerably worse at ten, and this project
    /// is one world on one machine.
    /// </para>
    /// <para>
    /// Accounts and characters live in the same file because there is exactly one
    /// character per account in this game вЂ” no character-select screen, no shared bank
    /// to reconcile.
    /// </para>
    /// </remarks>
    public sealed class PlayerStore
    {
        private readonly string _characterDir;
        private readonly string _accountDir;
        private readonly object _sync = new object();
        private readonly Dictionary<string, int> _privileges = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public PlayerStore(ServerConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");

            string root = config.Resolve(config.DataDir);
            _characterDir = Path.Combine(root, "characters");
            _accountDir = Path.Combine(root, "accounts");

            Directory.CreateDirectory(_characterDir);
            Directory.CreateDirectory(_accountDir);

            Log.Info("Store", "Characters: {0}", _characterDir);
        }

        /// <summary>Number of account files on disk.</summary>
        public int AccountCount
        {
            get
            {
                try
                {
                    return Directory.GetFiles(_accountDir, "*.ini").Length;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>Privilege level for a name key, 0 when unknown.</summary>
        public int GetPrivilege(string nameKey)
        {
            lock (_sync)
            {
                int privilege;
                return _privileges.TryGetValue(nameKey, out privilege) ? privilege : 0;
            }
        }

        /// <summary>Grants or revokes privileges and persists the change.</summary>
        public void SetPrivilege(string nameKey, int privilege)
        {
            lock (_sync)
            {
                _privileges[nameKey] = privilege;
            }

            IniConfig account = IniConfig.Load(AccountPath(nameKey));
            account.Set("account.privilege", privilege.ToString());
            account.Save();
        }

        private string AccountPath(string nameKey)
        {
            return Path.Combine(_accountDir, SafeName(nameKey) + ".ini");
        }

        private string CharacterPath(string nameKey)
        {
            return Path.Combine(_characterDir, SafeName(nameKey) + ".ini");
        }

        private static string SafeName(string nameKey)
        {
            // The name has already been validated to letters/digits/space/-/_, but a
            // file name still should not contain spaces or separators.
            var sb = new System.Text.StringBuilder(nameKey.Length);
            foreach (char c in nameKey)
            {
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Loads the character for <paramref name="name"/>, creating the account when it
        /// does not exist and registration is enabled.
        /// </summary>
        /// <returns>The player, or <c>null</c> when the login must be refused.</returns>
        public Player LoadOrCreate(string name, string password, out bool created)
        {
            created = false;

            string display = TextEncoding.DisplayName(name);
            string key = TextEncoding.NormaliseName(name);
            if (string.IsNullOrEmpty(key)) return null;

            string accountPath = AccountPath(key);
            string characterPath = CharacterPath(key);

            lock (_sync)
            {
                bool accountExists = File.Exists(accountPath);

                if (!accountExists)
                {
                    if (!AllowRegistration) return null;

                    if (!CreateAccount(key, display, password)) return null;
                    created = true;
                    Log.Info("Store", "Created account '{0}'.", display);
                }
                else
                {
                    IniConfig account = IniConfig.Load(accountPath);
                    string storedHash = account.GetString("account.password", "");
                    if (!PasswordHasher.Verify(password, storedHash))
                    {
                        Log.Info("Store", "Wrong password for '{0}'.", display);
                        return null;
                    }
                }

                var player = new Player
                {
                    Name = display,
                    NameKey = key,
                    NewAccount = created
                };

                if (File.Exists(characterPath))
                {
                    LoadCharacter(player, characterPath);
                }
                else
                {
                    ApplyStarterState(player);
                }

                IniConfig accountForPrivilege = IniConfig.Load(accountPath);
                player.Privilege = accountForPrivilege.GetInt("account.privilege", 0);
                _privileges[key] = player.Privilege;

                return player;
            }
        }

        private bool AllowRegistration
        {
            get { return _allowRegistration; }
        }

        private bool _allowRegistration = true;

        /// <summary>Enables or disables account creation at runtime.</summary>
        public void SetAllowRegistration(bool allow)
        {
            _allowRegistration = allow;
        }

        private bool CreateAccount(string key, string display, string password)
        {
            try
            {
                var account = IniConfig.Load(AccountPath(key));
                account.Set("account.name", display);
                account.Set("account.key", key);
                account.Set("account.password", PasswordHasher.Hash(password));
                account.Set("account.privilege", "0");
                account.Set("account.createdAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                account.Save();
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("Store", ex, "Could not create account " + key);
                return false;
            }
        }

        private void ApplyStarterState(Player player)
        {
            player.RecalculateLevels();
            player.RestoreStats();

            // A small starter kit so a new player is not defenceless.
            player.Inventory.Add(995, 250);        // coins
            player.Inventory.Add(1351, 1);         // bronze axe
            player.Inventory.Add(1265, 1);         // bronze pickaxe
            player.Inventory.Add(590, 1);          // tinderbox
            player.Inventory.Add(303, 1);          // small fishing net
            player.Inventory.Add(315, 10);         // shrimp
        }

        // -- serialisation ----------------------------------------------------

        private static void LoadCharacter(Player player, string path)
        {
            IniConfig file = IniConfig.Load(path);

            player.Position = new Position(
                file.GetInt("position.x", 3222),
                file.GetInt("position.y", 3218),
                file.GetInt("position.plane", 0));

            player.LastPosition = player.Position;
            player.Gender = file.GetInt("appearance.gender", 0);
            player.HeadIcon = file.GetInt("appearance.headIcon", -1);
            player.RunEnabled = file.GetBool("movement.run", false);
            player.RunEnergy = file.GetInt("movement.energy", 10000);

            for (int i = 0; i < 12; i++)
            {
                player.Appearance[i] = file.GetInt("appearance.part" + i, DefaultAppearance(i, player.Gender));
            }

            for (int i = 0; i < Skills.Count; i++)
            {
                player.Experience[i] = file.GetInt("skill." + i + ".xp", 0);
            }

            player.RecalculateLevels();
            player.Hitpoints = file.GetInt("skill.hitpoints.current", player.MaxHitpoints);
            if (player.Hitpoints <= 0) player.Hitpoints = player.MaxHitpoints;
            player.RestoreStats();

            LoadContainer(player.Inventory, file, "inventory");
            LoadContainer(player.Equipment, file, "equipment");
            LoadContainer(player.Bank, file, "bank");

            RebuildEquipmentVisuals(player);
            player.AppearanceDirty = true;
        }

        private static void LoadContainer(ItemContainer container, IniConfig file, string prefix)
        {
            container.Clear();
            for (int i = 0; i < container.Size; i++)
            {
                string raw = file.GetString(prefix + "." + i, null);
                if (string.IsNullOrEmpty(raw)) continue;

                int separator = raw.IndexOf(':');
                if (separator <= 0) continue;

                int id;
                int amount;
                if (!int.TryParse(raw.Substring(0, separator), out id)) continue;
                if (!int.TryParse(raw.Substring(separator + 1), out amount)) continue;

                container.Set(i, id, amount);
            }
        }

        /// <summary>Writes a player's state to disk atomically.</summary>
        public bool Save(Player player)
        {
            if (player == null) return false;

            lock (_sync)
            {
                try
                {
                    string path = CharacterPath(player.NameKey);
                    string temporary = path + ".tmp";

                    var file = IniConfig.Load(temporary);
                    file.Set("character.name", player.Name);
                    file.Set("character.key", player.NameKey);
                    file.Set("character.savedAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

                    file.Set("position.x", player.Position.X.ToString());
                    file.Set("position.y", player.Position.Y.ToString());
                    file.Set("position.plane", player.Position.Plane.ToString());

                    file.Set("appearance.gender", player.Gender.ToString());
                    file.Set("appearance.headIcon", player.HeadIcon.ToString());
                    for (int i = 0; i < 12; i++) file.Set("appearance.part" + i, player.Appearance[i].ToString());

                    file.Set("movement.run", player.RunEnabled ? "true" : "false");
                    file.Set("movement.energy", player.RunEnergy.ToString());

                    for (int i = 0; i < Skills.Count; i++)
                    {
                        file.Set("skill." + i + ".xp", player.Experience[i].ToString());
                    }
                    file.Set("skill.hitpoints.current", player.Hitpoints.ToString());

                    SaveContainer(player.Inventory, file, "inventory");
                    SaveContainer(player.Equipment, file, "equipment");
                    SaveContainer(player.Bank, file, "bank");

                    file.Save();

                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temporary, path);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Exception("Store", ex, "Could not save " + player.Name);
                    return false;
                }
            }
        }

        private static void SaveContainer(ItemContainer container, IniConfig file, string prefix)
        {
            for (int i = 0; i < container.Size; i++)
            {
                ItemStack stack = container[i];
                file.Set(prefix + "." + i, stack.IsEmpty ? "" : stack.Id + ":" + stack.Amount);
            }
        }

        /// <summary>Saves every online player. Called on a clean shutdown.</summary>
        public int SaveAll(World world)
        {
            int saved = 0;
            if (world == null) return 0;

            Player[] players = world.Players;
            for (int i = 0; i < players.Length; i++)
            {
                Player p = players[i];
                if (p == null || !p.Online) continue;
                if (Save(p)) saved++;
            }
            return saved;
        }

        /// <summary>Rebuilds the visual equipment array from the worn items.</summary>
        public static void RebuildEquipmentVisuals(Player player)
        {
            for (int i = 0; i < player.EquipmentVisual.Length; i++) player.EquipmentVisual[i] = ItemStack.Empty;

            // Client slot order: hat, cape, amulet, weapon, body, shield, arms(empty),
            // legs, head(empty), hands, feet, beard(empty).
            int[] mapping = { 0, 1, 2, 3, 4, 5, -1, 7, -1, 9, 10, -1 };

            for (int i = 0; i < mapping.Length; i++)
            {
                if (mapping[i] < 0) continue;
                player.EquipmentVisual[i] = player.Equipment[mapping[i]];
            }
        }

        /// <summary>Sensible default appearance so a new character is not naked-invisible.</summary>
        public static int DefaultAppearance(int index, int gender)
        {
            switch (index)
            {
                case 0: return 0;     // head model
                case 1: return 18;    // chest
                case 2: return 26;    // arms
                case 3: return 33;    // hands
                case 4: return 36;    // legs
                case 5: return 42;    // feet
                case 6: return 10;    // beard (male only)
                case 7: return 1;     // hair colour
                case 8: return 2;     // torso colour
                case 9: return 3;     // leg colour
                case 10: return 4;    // feet colour
                case 11: return 0;    // skin colour
                default: return 0;
            }
        }
    }
}
