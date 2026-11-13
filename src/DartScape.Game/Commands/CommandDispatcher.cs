using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;
using DartScape.Game.Definitions;
using DartScape.Game;

namespace DartScape.Game.Commands
{
    /// <summary>
    /// The <c>::command</c> console.
    /// </summary>
    /// <remarks>
    /// Commands are parsed by hand rather than by reflection: there are a dozen of them,
    /// they need precise argument validation, and a reflection dispatcher on a game server
    /// is a liability (it happily invokes anything with the right attribute). Each command
    /// declares its own minimum privilege and validates its own arguments.
    /// </remarks>
    public static class CommandDispatcher
    {
        /// <summary>Result of running a command, for the admin panel and console.</summary>
        public sealed class Result
        {
            public bool Success;
            public readonly List<string> Lines = new List<string>();

            public Result(bool success)
            {
                Success = success;
            }

            public Result Add(string line)
            {
                Lines.Add(line);
                return this;
            }
        }

        private delegate void CommandHandler(World world, Player player, string[] args, Result result);

        private sealed class Command
        {
            public string Name;
            public string Usage;
            public string Summary;
            public int MinPrivilege;
            public CommandHandler Handler;
        }

        private static readonly Dictionary<string, Command> Commands = BuildTable();

        private static Dictionary<string, Command> BuildTable()
        {
            var table = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);

            Add(table, "help", "::help", "List commands you can use.", 0, Help);
            Add(table, "pos", "::pos", "Show your coordinates.", 0, Pos);
            Add(table, "players", "::players", "List online players.", 0, Players);
            Add(table, "stats", "::stats [skill]", "Show your skill levels.", 0, Stats);
            Add(table, "item", "::item <id> [amount]", "Spawn an item.", 2, Item);
            Add(table, "npc", "::npc <id>", "Spawn an NPC next to you.", 2, NpcCommand);
            Add(table, "tele", "::tele <x> <y> [plane]", "Teleport to coordinates.", 2, Tele);
            Add(table, "anim", "::anim <id>", "Play an animation.", 1, Anim);
            Add(table, "gfx", "::gfx <id> [height] [delay]", "Play a graphic.", 1, Gfx);
            Add(table, "hp", "::hp [amount]", "Set or restore hitpoints.", 2, Hp);
            Add(table, "xp", "::xp <skill> <amount>", "Grant experience.", 2, Xp);
            Add(table, "level", "::level <skill> <level>", "Set a skill level.", 2, Level);
            Add(table, "kick", "::kick <player> [reason]", "Disconnect a player.", 1, Kick);
            Add(table, "broadcast", "::broadcast <text>", "Message everyone.", 1, Broadcast);
            Add(table, "save", "::save [player]", "Save characters to disk.", 2, Save);
            Add(table, "reload", "::reload", "Reload item and NPC definitions.", 2, Reload);
            Add(table, "rate", "::rate <multiplier>", "Set the experience rate.", 2, Rate);
            Add(table, "privilege", "::privilege <player> <level>", "Set a player's privilege.", 2, Privilege);

            return table;
        }

        private static void Add(Dictionary<string, Command> table, string name, string usage,
            string summary, int minPrivilege, CommandHandler handler)
        {
            table[name] = new Command
            {
                Name = name,
                Usage = usage,
                Summary = summary,
                MinPrivilege = minPrivilege,
                Handler = handler
            };
        }

        /// <summary>
        /// Parses and runs <paramref name="text"/> (with or without a leading <c>::</c>).
        /// </summary>
        public static Result Execute(World world, Player player, string text)
        {
            var result = new Result(false);

            if (string.IsNullOrEmpty(text)) return result;

            string trimmed = text.Trim();
            if (trimmed.StartsWith("::", StringComparison.Ordinal)) trimmed = trimmed.Substring(2);

            string[] parts = SplitArguments(trimmed);
            if (parts.Length == 0) return result;

            Command command;
            if (!Commands.TryGetValue(parts[0], out command))
            {
                if (player != null)
                {
                    world.SendMessage(player, "Unknown command. Type ::help.");
                }
                result.Add("Unknown command: " + parts[0]);
                return result;
            }

            int privilege = player == null ? 2 : player.Privilege;
            if (privilege < command.MinPrivilege)
            {
                if (player != null) world.SendMessage(player, "You don't have permission to use that command.");
                result.Add("Permission denied: " + command.Name);
                return result;
            }

            var args = new string[Math.Max(0, parts.Length - 1)];
            Array.Copy(parts, 1, args, 0, args.Length);

            try
            {
                command.Handler(world, player, args, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Add("Command failed: " + ex.Message);
                Log.Exception("Commands", ex, "Command " + command.Name);
            }

            if (player != null)
            {
                foreach (string line in result.Lines) world.SendMessage(player, line);
            }

            return result;
        }

        /// <summary>Splits on whitespace, honouring double quotes.</summary>
        private static string[] SplitArguments(string text)
        {
            var parts = new List<string>(8);
            var current = new StringBuilder(32);
            bool inQuotes = false;

            foreach (char c in text)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && (c == ' ' || c == '\t'))
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Length = 0;
                    }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) parts.Add(current.ToString());
            return parts.ToArray();
        }

        // -- commands ---------------------------------------------------------

        private static void Help(World world, Player player, string[] args, Result result)
        {
            int privilege = player == null ? 2 : player.Privilege;
            result.Add("Commands available to you:");

            var names = new List<string>(Commands.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string name in names)
            {
                Command command = Commands[name];
                if (command.MinPrivilege > privilege) continue;
                result.Add("  " + command.Usage + " - " + command.Summary);
            }
        }

        private static void Pos(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }
            result.Add("You are at " + player.Position.X + ", " + player.Position.Y + ", plane " + player.Position.Plane + ".");
        }

        private static void Players(World world, Player player, string[] args, Result result)
        {
            List<Player> online = world.OnlinePlayers();
            result.Add(online.Count + " player(s) online:");

            foreach (Player p in online)
            {
                result.Add("  " + p.Name + " - combat " + p.CombatLevel + ", total " + p.TotalLevel +
                           " at " + p.Position.X + "," + p.Position.Y);
            }
        }

        private static void Stats(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }

            if (args.Length > 0)
            {
                int skill = Skills.IndexOf(args[0]);
                if (skill < 0) { result.Add("Unknown skill: " + args[0]); return; }

                result.Add(Skills.Names[skill] + ": level " + player.BaseLevel[skill] +
                           " (" + player.CurrentLevel[skill] + "), " + Skills.FormatExperience(player.Experience[skill]) + " xp");
                return;
            }

            result.Add("Combat " + player.CombatLevel + ", total level " + player.TotalLevel + ".");
            for (int i = 0; i < Skills.Count; i++)
            {
                result.Add("  " + Skills.Names[i] + ": " + player.BaseLevel[i] + " (" +
                           Skills.FormatExperience(player.Experience[i]) + ")");
            }
        }

        private static void Item(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }
            if (args.Length < 1) { result.Add("Usage: ::item <id> [amount]"); return; }

            int itemId;
            if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId))
            {
                result.Add("Item id must be a number.");
                return;
            }

            int amount = 1;
            if (args.Length > 1) int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount);
            if (amount < 1) amount = 1;

            ItemDefinition def = DefinitionLoader.Item(itemId);
            if (def == null)
            {
                result.Add("No item with id " + itemId + ".");
                return;
            }

            int added = player.Inventory.Add(itemId, amount);
            world.SendInventory(player);

            if (added <= 0)
            {
                result.Add("Your inventory is full.");
                return;
            }

            result.Add("Gave " + added + "x " + def.Name + ".");
        }

        private static void NpcCommand(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }
            if (args.Length < 1) { result.Add("Usage: ::npc <id>"); return; }

            int npcId;
            if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out npcId))
            {
                result.Add("NPC id must be a number.");
                return;
            }

            NpcDefinition def = DefinitionLoader.Npc(npcId);
            if (def == null)
            {
                result.Add("No NPC with id " + npcId + ".");
                return;
            }

            Npc npc = world.SpawnNpc(npcId, player.Position.Translate(1, 0));
            result.Add(npc == null ? "Could not spawn that NPC." : "Spawned " + def.Name + ".");
        }

        private static void Tele(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }
            if (args.Length < 2) { result.Add("Usage: ::tele <x> <y> [plane]"); return; }

            int x, y, plane = 0;
            if (!int.TryParse(args[0], out x) || !int.TryParse(args[1], out y))
            {
                result.Add("Coordinates must be numbers.");
                return;
            }
            if (args.Length > 2) int.TryParse(args[2], out plane);

            if (x < 0 || x > 16383 || y < 0 || y > 16383 || plane < 0 || plane > 3)
            {
                result.Add("Coordinates out of range.");
                return;
            }

            player.Position = new Position(x, y, plane);
            player.LastPosition = player.Position;
            player.Teleported = true;
            player.StopWalking();
            player.View.Clear();

            result.Add("Teleported to " + x + ", " + y + ", plane " + plane + ".");
        }

        private static void Anim(World world, Player player, string[] args, Result result)
        {
            if (player == null || args.Length < 1) { result.Add("Usage: ::anim <id>"); return; }

            int id;
            if (!int.TryParse(args[0], out id)) { result.Add("Animation id must be a number."); return; }

            world.PlayAnimation(player, id);
            result.Add("Playing animation " + id + ".");
        }

        private static void Gfx(World world, Player player, string[] args, Result result)
        {
            if (player == null || args.Length < 1) { result.Add("Usage: ::gfx <id> [height] [delay]"); return; }

            int id, height = 0, delay = 0;
            if (!int.TryParse(args[0], out id)) { result.Add("Graphic id must be a number."); return; }
            if (args.Length > 1) int.TryParse(args[1], out height);
            if (args.Length > 2) int.TryParse(args[2], out delay);

            world.PlayGraphic(player, id, height, delay);
            result.Add("Playing graphic " + id + ".");
        }

        private static void Hp(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }

            if (args.Length == 0)
            {
                player.Hitpoints = player.MaxHitpoints;
                result.Add("Hitpoints restored to " + player.MaxHitpoints + ".");
            }
            else
            {
                int amount;
                if (!int.TryParse(args[0], out amount)) { result.Add("Amount must be a number."); return; }
                player.Hitpoints = Math.Max(1, Math.Min(player.MaxHitpoints, amount));
                result.Add("Hitpoints set to " + player.Hitpoints + ".");
            }

            world.SendSkills(player);
        }

        private static void Xp(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }
            if (args.Length < 2) { result.Add("Usage: ::xp <skill> <amount>"); return; }

            int skill = Skills.IndexOf(args[0]);
            if (skill < 0) { result.Add("Unknown skill: " + args[0]); return; }

            int amount;
            if (!int.TryParse(args[1], out amount) || amount <= 0) { result.Add("Amount must be a positive number."); return; }

            int level = player.AddExperience(skill, amount, world.Config.ExperienceRate);
            world.SendSkillUpdate(player, skill);

            result.Add("Gave " + amount + " " + Skills.Names[skill] + " experience." +
                       (level > 0 ? " Now level " + level + "." : ""));
        }

        private static void Level(World world, Player player, string[] args, Result result)
        {
            if (player == null) { result.Add("No player context."); return; }
            if (args.Length < 2) { result.Add("Usage: ::level <skill> <level>"); return; }

            int skill = Skills.IndexOf(args[0]);
            if (skill < 0) { result.Add("Unknown skill: " + args[0]); return; }

            int level;
            if (!int.TryParse(args[1], out level) || level < 1 || level > Skills.MaxLevel)
            {
                result.Add("Level must be between 1 and " + Skills.MaxLevel + ".");
                return;
            }

            player.Experience[skill] = Skills.ExperienceAt(level);
            player.RecalculateLevels();
            player.CurrentLevel[skill] = level;
            world.SendSkills(player);

            result.Add(Skills.Names[skill] + " set to " + level + ".");
        }

        private static void Kick(World world, Player player, string[] args, Result result)
        {
            if (args.Length < 1) { result.Add("Usage: ::kick <player> [reason]"); return; }

            Player target = world.FindPlayer(args[0]);
            if (target == null) { result.Add("No player called " + args[0] + " is online."); return; }

            string reason = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "Kicked by an administrator";
            world.SendMessage(target, reason);
            if (target.Endpoint != null) target.Endpoint.Disconnect(reason);

            result.Add("Kicked " + target.Name + ".");
        }

        private static void Broadcast(World world, Player player, string[] args, Result result)
        {
            if (args.Length < 1) { result.Add("Usage: ::broadcast <text>"); return; }

            string text = string.Join(" ", args);
            int delivered = world.BroadcastMessage(text);
            world.AddEvent("broadcast", text);

            result.Add("Broadcast to " + delivered + " player(s).");
        }

        private static void Save(World world, Player player, string[] args, Result result)
        {
            var store = world.Config == null ? null : GameServices.Store;
            if (store == null) { result.Add("No player store available."); return; }

            if (args.Length > 0)
            {
                Player target = world.FindPlayer(args[0]);
                if (target == null) { result.Add("No player called " + args[0] + " is online."); return; }
                result.Add(store.Save(target) ? "Saved " + target.Name + "." : "Save failed.");
                return;
            }

            int count = store.SaveAll(world);
            result.Add("Saved " + count + " character(s).");
        }

        private static void Reload(World world, Player player, string[] args, Result result)
        {
            if (GameServices.Cache == null) { result.Add("No cache loaded."); return; }

            bool ok = DefinitionLoader.Load(GameServices.Cache);
            result.Add(ok
                ? "Reloaded " + DefinitionLoader.Items.Length + " items and " + DefinitionLoader.Npcs.Length + " NPCs."
                : "Reload failed; see the log.");
        }

        private static void Rate(World world, Player player, string[] args, Result result)
        {
            if (args.Length < 1) { result.Add("Usage: ::rate <multiplier>"); return; }

            double rate;
            if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out rate) || rate <= 0)
            {
                result.Add("Multiplier must be a positive number.");
                return;
            }

            world.Config.ExperienceRate = rate;
            result.Add("Experience rate set to x" + rate.ToString("0.##", CultureInfo.InvariantCulture) + ".");
        }

        private static void Privilege(World world, Player player, string[] args, Result result)
        {
            if (args.Length < 2) { result.Add("Usage: ::privilege <player> <level>"); return; }

            Player target = world.FindPlayer(args[0]);
            if (target == null) { result.Add("No player called " + args[0] + " is online."); return; }

            int level;
            if (!int.TryParse(args[1], out level) || level < 0 || level > 2)
            {
                result.Add("Level must be 0, 1 or 2.");
                return;
            }

            target.Privilege = level;
            if (GameServices.Store != null) GameServices.Store.SetPrivilege(target.NameKey, level);

            result.Add(target.Name + " is now privilege " + level + ".");
        }
    }
}
