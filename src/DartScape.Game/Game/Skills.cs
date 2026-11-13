using System;
using System.Globalization;

namespace DartScape.Game
{
    /// <summary>
    /// The twenty-one skills and their experience curve.
    /// </summary>
    /// <remarks>
    /// The curve is the authentic one: the experience required to reach level
    /// <c>L</c> is <c>sum(floor(i + 300 * 2^(i/7)))</c> for <c>i</c> from 1 to
    /// <c>L-1</c>, divided by four and floored. It is precomputed once at start-up
    /// because every experience gain needs the reverse lookup, and computing it per
    /// call would be a silly amount of floating point arithmetic in the tick loop.
    /// </remarks>
    public static class Skills
    {
        public const int Attack = 0;
        public const int Defence = 1;
        public const int Strength = 2;
        public const int Hitpoints = 3;
        public const int Ranged = 4;
        public const int Prayer = 5;
        public const int Magic = 6;
        public const int Cooking = 7;
        public const int Woodcutting = 8;
        public const int Fletching = 9;
        public const int Fishing = 10;
        public const int Firemaking = 11;
        public const int Crafting = 12;
        public const int Smithing = 13;
        public const int Mining = 14;
        public const int Herblore = 15;
        public const int Agility = 16;
        public const int Thieving = 17;
        public const int Slayer = 18;
        public const int Farming = 19;
        public const int Runecrafting = 20;

        public const int Count = 21;
        public const int MaxLevel = 99;
        public const int MaxExperience = 200000000;

        /// <summary>Display names, in skill-index order.</summary>
        public static readonly string[] Names =
        {
            "Attack", "Defence", "Strength", "Hitpoints", "Ranged", "Prayer", "Magic",
            "Cooking", "Woodcutting", "Fletching", "Fishing", "Firemaking", "Crafting",
            "Smithing", "Mining", "Herblore", "Agility", "Thieving", "Slayer", "Farming",
            "Runecrafting"
        };

        /// <summary>Lowercase identifiers used in JSON and commands.</summary>
        public static readonly string[] Keys =
        {
            "attack", "defence", "strength", "hitpoints", "ranged", "prayer", "magic",
            "cooking", "woodcutting", "fletching", "fishing", "firemaking", "crafting",
            "smithing", "mining", "herblore", "agility", "thieving", "slayer", "farming",
            "runecrafting"
        };

        /// <summary>Experience required to reach each level. Index 0 is unused.</summary>
        private static readonly int[] ExperienceForLevel = new int[MaxLevel + 1];

        /// <summary>Skill order as the 317 client expects it on the stats interface.</summary>
        public static readonly int[] ClientOrder =
        {
            Attack, Hitpoints, Mining, Strength, Agility, Smithing, Defence, Herblore,
            Fishing, Ranged, Thieving, Cooking, Prayer, Crafting, Firemaking, Magic,
            Fletching, Woodcutting, Runecrafting, Slayer, Farming
        };

        static Skills()
        {
            ExperienceForLevel[0] = 0;
            ExperienceForLevel[1] = 0;

            int points = 0;
            for (int level = 1; level < MaxLevel; level++)
            {
                int step = level + (int)(300.0 * Math.Pow(2.0, level / 7.0));
                points += step;
                ExperienceForLevel[level + 1] = points / 4;
            }
        }

        /// <summary>Total experience needed to be exactly <paramref name="level"/>.</summary>
        public static int ExperienceAt(int level)
        {
            if (level <= 1) return 0;
            if (level > MaxLevel) level = MaxLevel;
            return ExperienceForLevel[level];
        }

        /// <summary>Level implied by <paramref name="experience"/>, clamped to 1..99.</summary>
        public static int LevelFor(int experience)
        {
            if (experience <= 0) return 1;
            if (experience >= MaxExperience) return MaxLevel;

            // Binary search: the table is monotonic.
            int low = 1;
            int high = MaxLevel;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (ExperienceForLevel[mid] <= experience) low = mid;
                else high = mid - 1;
            }
            return low;
        }

        /// <summary>Experience still needed to reach the next level, or 0 at 99.</summary>
        public static int RemainingToNextLevel(int experience)
        {
            int level = LevelFor(experience);
            if (level >= MaxLevel) return 0;
            return ExperienceForLevel[level + 1] - experience;
        }

        /// <summary>Resolves a skill name or key to its index, or -1.</summary>
        public static int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;

            string needle = name.Trim().ToLowerInvariant();
            for (int i = 0; i < Count; i++)
            {
                if (Keys[i] == needle || Names[i].ToLowerInvariant() == needle) return i;
            }
            return -1;
        }

        /// <summary>Formats experience the way the client's tooltips do (1,234,567).</summary>
        public static string FormatExperience(int experience)
        {
            return experience.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
