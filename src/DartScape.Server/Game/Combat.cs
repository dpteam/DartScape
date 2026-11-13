using System;
using DartScape.Core.Diagnostics;
using DartScape.Server.Definitions;

namespace DartScape.Server.Game
{
    /// <summary>
    /// Melee combat resolution, experience awards and death handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The formulas are the classic ones, kept in one place so they can be tuned without
    /// hunting through the tick loop. Everything here is deterministic given the world's
    /// random source, which makes combat reproducible in a bug report.
    /// </para>
    /// <para>
    /// Only melee is implemented. Ranged and magic need projectiles and spell data that
    /// the cache revision in use does not carry, and a half-working implementation would
    /// be worse than none. The structure — a single <c>Resolve</c> that both directions
    /// share — is what makes adding them a contained change.
    /// </para>
    /// </remarks>
    public static class Combat
    {
        /// <summary>Ticks between melee attacks, indexed by weapon speed. Unarmed is 4.</summary>
        public const int DefaultAttackSpeed = 4;

        /// <summary>Called when a player's melee attack lands on an NPC.</summary>
        public static void PlayerAttacksNpc(World world, Player player, Npc npc)
        {
            if (world == null || player == null || npc == null || npc.IsDead) return;

            player.FaceTile = npc.Position;
            player.InteractingNpcIndex = npc.Index;
            player.UpdateMask |= PlayerUpdateFlags.InteractingEntity;

            world.PlayAnimation(player, AttackAnimation(player));

            int attack = player.CurrentLevel[Skills.Attack];
            int strength = player.CurrentLevel[Skills.Strength];
            int defence = npc.Definition == null ? 1 : npc.Definition.Defence;

            int damage = Resolve(attack, strength, defence, out bool hit);

            player.AttackDelay = AttackSpeed(player);

            if (hit && damage > 0)
            {
                world.ApplyDamageToNpc(npc, damage, 1);
                npc.LastAttacker = player.Index;
                if (!npc.IsDead) npc.TargetPlayer = player.Index;

                int xp = (int)Math.Round(4 * damage + 1.0);
                Award(player, Skills.Attack, xp / 3, world);
                Award(player, Skills.Strength, xp / 3, world);
                Award(player, Skills.Hitpoints, xp / 3, world);
                player.LastDamageDealt = damage;
            }
            else
            {
                Award(player, Skills.Attack, 1, world);
                Award(player, Skills.Hitpoints, 1, world);
            }

            if (npc.IsDead) HandleNpcDeath(world, player, npc);
        }

        /// <summary>Called when a player attacks another player.</summary>
        public static void PlayerAttacksPlayer(World world, Player player, Player target)
        {
            if (world == null || player == null || target == null) return;
            if (target.IsDead || !target.Online) return;

            player.FaceTile = target.Position;
            player.InteractingPlayerIndex = target.Index;
            player.UpdateMask |= PlayerUpdateFlags.InteractingEntity;
            target.InteractingPlayerIndex = player.Index;
            target.UpdateMask |= PlayerUpdateFlags.InteractingEntity;

            world.PlayAnimation(player, AttackAnimation(player));

            int damage = Resolve(
                player.CurrentLevel[Skills.Attack],
                player.CurrentLevel[Skills.Strength],
                target.CurrentLevel[Skills.Defence],
                out bool hit);

            player.AttackDelay = AttackSpeed(player);

            if (hit && damage > 0)
            {
                world.ApplyDamageToPlayer(target, damage, 1);
                target.RecentAttackers.Add(player.Name);
                if (target.RecentAttackers.Count > 8) target.RecentAttackers.RemoveAt(0);

                int xp = (int)Math.Round(4 * damage + 1.0);
                Award(player, Skills.Attack, xp / 3, world);
                Award(player, Skills.Strength, xp / 3, world);
                Award(player, Skills.Hitpoints, xp / 3, world);

                // Skulling: attacking an innocent player marks you for three minutes.
                if (target.SkullTicks <= 0 && player.SkullTicks <= 0)
                {
                    player.SkullTicks = 300;
                    world.SendMessage(player, "You have been skulled!");
                }
            }

            if (target.IsDead)
            {
                target.CombatTargetPlayer = -1;
                player.CombatTargetPlayer = -1;
            }
        }

        /// <summary>Called when an NPC's attack lands on a player.</summary>
        public static void NpcAttacksPlayer(World world, Npc npc, Player target)
        {
            if (world == null || npc == null || target == null || target.IsDead) return;

            int attack = npc.Definition == null ? 1 : npc.Definition.Attack;
            int strength = npc.Definition == null ? 1 : Math.Max(1, npc.Definition.CombatLevel / 2);
            int defence = target.CurrentLevel[Skills.Defence];

            int damage = Resolve(attack, strength, defence, out bool hit);

            npc.AttackDelay = npc.Definition == null ? 4 : Math.Max(2, npc.Definition.AttackSpeed);

            if (hit && damage > 0)
            {
                world.ApplyDamageToPlayer(target, damage, 1);
                target.RecentAttackers.Add(npc.Name);
                if (target.RecentAttackers.Count > 8) target.RecentAttackers.RemoveAt(0);
                Award(target, Skills.Hitpoints, (int)Math.Round(1.33 * damage), world);
            }

            if (target.IsDead)
            {
                target.CombatTargetNpc = -1;
                npc.TargetPlayer = -1;
            }
        }

        /// <summary>
        /// The core melee roll. Returns the damage dealt and reports whether the attack
        /// connected at all (a miss still costs a swing).
        /// </summary>
        private static int Resolve(int attackLevel, int strengthLevel, int defenceLevel, out bool hit)
        {
            // Effective levels with the classic +8 bonus.
            int attack = attackLevel + 8;
            int strength = strengthLevel + 8;
            int defence = defenceLevel + 8;

            double attackRoll = attack * (64 + 0);
            double defenceRoll = defence * (64 + 0);

            double hitChance = attackRoll > defenceRoll
                ? 1.0 - (defenceRoll + 2.0) / (2.0 * (attackRoll + 1.0))
                : attackRoll / (2.0 * (defenceRoll + 1.0));

            hit = Rng.NextDouble() < hitChance;
            if (!hit) return 0;

            int maxHit = Math.Max(1, (int)((strength * (64 + 0) + 320) / 640.0));
            int damage = Rng.Next(1, maxHit + 1);
            return damage;
        }

        private static void Award(Player player, int skill, int amount, World world)
        {
            if (amount <= 0) return;

            int newLevel = player.AddExperience(skill, amount, world.Config.ExperienceRate);
            if (newLevel > 0)
            {
                // The tick loop sends the dialogue; nothing else to do here.
            }
            world.SendSkillUpdate(player, skill);
        }

        /// <summary>Attack animation for the player's equipped weapon.</summary>
        private static int AttackAnimation(Player player)
        {
            ItemStack weapon = player.Equipment[3];     // slot 3 is the weapon
            if (weapon.IsEmpty) return 422;             // unarmed punch

            ItemDefinition def = DefinitionLoader.Item(weapon.Id);
            if (def == null) return 422;

            // A small, honest mapping for the common weapon classes.
            string name = (def.Name ?? string.Empty).ToLowerInvariant();
            if (name.Contains("2h") || name.Contains("godsword")) return 704;
            if (name.Contains("scimitar") || name.Contains("sword")) return 390;
            if (name.Contains("dagger")) return 386;
            if (name.Contains("mace") || name.Contains("warhammer")) return 401;
            if (name.Contains("axe") || name.Contains("battleaxe")) return 395;
            if (name.Contains("spear") || name.Contains("halberd")) return 440;
            if (name.Contains("staff")) return 419;
            return 390;
        }

        private static int AttackSpeed(Player player)
        {
            ItemStack weapon = player.Equipment[3];
            if (weapon.IsEmpty) return DefaultAttackSpeed;

            ItemDefinition def = DefinitionLoader.Item(weapon.Id);
            if (def == null) return DefaultAttackSpeed;

            string name = (def.Name ?? string.Empty).ToLowerInvariant();
            if (name.Contains("dagger")) return 4;
            if (name.Contains("scimitar")) return 4;
            if (name.Contains("2h") || name.Contains("godsword")) return 6;
            if (name.Contains("battleaxe")) return 6;
            if (name.Contains("warhammer")) return 6;
            return 5;
        }

        private static void HandleNpcDeath(World world, Player killer, Npc npc)
        {
            if (npc == null) return;

            NpcDefinition def = npc.Definition;

            // Experience for the kill itself.
            if (def != null && def.Hitpoints > 0)
            {
                int hitpointsXp = def.Hitpoints * 4 / 3;
                Award(killer, Skills.Hitpoints, hitpointsXp, world);
            }

            // Drops.
            if (def != null && def.Drops != null)
            {
                foreach (int itemId in def.Drops)
                {
                    if (itemId <= 0) continue;
                    if (Rng.NextDouble() > 0.35) continue;

                    ItemDefinition item = DefinitionLoader.Item(itemId);
                    int amount = 1;
                    if (item != null && item.Stackable) amount = Rng.Next(1, 6);

                    world.AddGroundItem(itemId, amount, npc.Position, killer.NameKey, 120000, 200);
                }
            }

            world.AddEvent("kill", killer.Name + " defeated " + npc.Name);
            Log.Debug("Combat", "{0} killed {1}.", killer.Name, npc.Name);
        }
    }

    /// <summary>
    /// The world's random source, kept separate so it can be seeded for tests and
    /// replaced with a deterministic generator when replaying a packet log.
    /// </summary>
    public static class Rng
    {
        private static readonly Random Source = new Random();

        public static int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return Source.Next(minInclusive, maxExclusive);
        }

        public static int Next(int maxExclusive)
        {
            return maxExclusive <= 0 ? 0 : Source.Next(maxExclusive);
        }

        public static double NextDouble()
        {
            return Source.NextDouble();
        }

        public static bool Chance(double probability)
        {
            return Source.NextDouble() < probability;
        }
    }
}
