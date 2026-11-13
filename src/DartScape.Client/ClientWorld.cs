using System;
using System.Collections.Generic;

namespace DartScape.Client
{
    /// <summary>A player as the client knows it.</summary>
    public sealed class ClientPlayer
    {
        public int Index;
        public string Name = "";
        public int X;
        public int Y;
        public int Plane;

        /// <summary>Previous tile, for smooth interpolation between ticks.</summary>
        public int LastX;
        public int LastY;

        public int CombatLevel;
        public int Privilege;
        public int Animation = -1;
        public int Graphic = -1;
        public string OverheadText;
        public int OverheadUntilMs;
        public int DamageToShow;
        public int Hitpoints;
        public int MaxHitpoints;

        /// <summary>Appearance: 12 equipment visuals then 5 colours then 7 body parts.</summary>
        public int[] EquipmentIds = new int[12];
        public int[] Colours = new int[5];
        public int[] BodyParts = new int[7];
        public int Gender;
        public int HeadIcon = -1;
        public bool Skulled;
        public bool AppearanceReceived;

        public bool IsLocal;
    }

    /// <summary>An NPC as the client knows it.</summary>
    public sealed class ClientNpc
    {
        public int Index;
        public int DefinitionId;
        public string Name = "";
        public int CombatLevel;
        public int Size = 1;
        public int X;
        public int Y;
        public int Plane;
        public int LastX;
        public int LastY;
        public int Animation = -1;
        public int Graphic = -1;
        public int DamageToShow;
        public int Hitpoints;
        public int MaxHitpoints;
        public string OverheadText;
        public int OverheadUntilMs;
    }

    /// <summary>A stack of items on the ground.</summary>
    public sealed class ClientGroundItem
    {
        public int ItemId;
        public int Amount;
        public int X;
        public int Y;
        public int Plane;
        public string Name = "";
    }

    /// <summary>A line in the chat box.</summary>
    public sealed class ChatLine
    {
        public string Text;
        public int Type;
        public int Colour;
        public int AtMs;
    }

    /// <summary>
    /// Everything the client knows about the world, mutated by incoming packets and read
    /// by the renderer.
    /// </summary>
    public sealed class ClientWorld
    {
        /// <summary>Maximum player and NPC slots, matching the server.</summary>
        public const int MaxPlayers = 2048;
        public const int MaxNpcs = 32768;

        public readonly ClientPlayer[] Players = new ClientPlayer[MaxPlayers];
        public readonly ClientNpc[] Npcs = new ClientNpc[MaxNpcs];
        public readonly List<ClientGroundItem> GroundItems = new List<ClientGroundItem>();

        public readonly int[] Experience = new int[21];
        public readonly int[] BaseLevel = new int[21];
        public readonly int[] CurrentLevel = new int[21];

        public readonly ItemSlot[] Inventory = new ItemSlot[28];
        public int MyIndex = -1;
        public int MyPrivilege;
        public int RunEnergy = 10000;

        public readonly List<ChatLine> Chat = new List<ChatLine>(100);
        public readonly List<string> ChatBuffer = new List<string>(8);

        /// <summary>Current player's tile, in world coordinates.</summary>
        public int MyX;
        public int MyY;
        public int MyPlane;

        /// <summary>Camera offset in tiles, adjusted by arrow keys / drag.</summary>
        public int CameraOffsetX;
        public int CameraOffsetY;

        /// <summary>Region base used to convert local map coordinates to world ones.</summary>
        public int BaseX = 3200;
        public int BaseY = 3200;

        /// <summary>Open interface id, or -1.</summary>
        public int OpenInterface = -1;

        /// <summary>Selected sidebar tab: 0 skills, 1 quests, 2 inventory, 3 equipment, 4 prayer, 5 magic.</summary>
        public int Tab;

        /// <summary>Currently selected inventory slot, or -1.</summary>
        public int SelectedSlot = -1;

        /// <summary>Server tick counter, used for animation timing.</summary>
        public int Tick;

        public struct ItemSlot
        {
            public int Id;
            public int Amount;

            public bool IsEmpty
            {
                get { return Id <= 0 || Amount <= 0; }
            }
        }

        public ClientPlayer Me
        {
            get { return MyIndex >= 0 && MyIndex < MaxPlayers ? Players[MyIndex] : null; }
        }

        /// <summary>Adds a chat line, keeping the buffer bounded.</summary>
        public void AddChatMessage(string text, int type = 0)
        {
            if (string.IsNullOrEmpty(text)) return;

            Chat.Add(new ChatLine
            {
                Text = text,
                Type = type,
                AtMs = Environment.TickCount
            });

            while (Chat.Count > 200) Chat.RemoveAt(0);
        }

        /// <summary>Clears everything on disconnect.</summary>
        public void Reset()
        {
            for (int i = 0; i < Players.Length; i++) Players[i] = null;
            for (int i = 0; i < Npcs.Length; i++) Npcs[i] = null;
            GroundItems.Clear();
            MyIndex = -1;
            OpenInterface = -1;
            SelectedSlot = -1;
            for (int i = 0; i < Inventory.Length; i++) Inventory[i] = new ItemSlot();
        }

        /// <summary>Finds the nearest ground item to a world tile.</summary>
        public ClientGroundItem GroundItemAt(int x, int y, int plane)
        {
            for (int i = 0; i < GroundItems.Count; i++)
            {
                ClientGroundItem item = GroundItems[i];
                if (item.X == x && item.Y == y && item.Plane == plane) return item;
            }
            return null;
        }

        /// <summary>Finds an NPC at a tile, accounting for its size.</summary>
        public ClientNpc NpcAt(int x, int y, int plane)
        {
            for (int i = 0; i < Npcs.Length; i++)
            {
                ClientNpc npc = Npcs[i];
                if (npc == null || npc.Plane != plane) continue;
                if (x >= npc.X && x < npc.X + npc.Size && y >= npc.Y && y < npc.Y + npc.Size) return npc;
            }
            return null;
        }

        /// <summary>Finds a player at a tile.</summary>
        public ClientPlayer PlayerAt(int x, int y, int plane)
        {
            for (int i = 0; i < Players.Length; i++)
            {
                ClientPlayer player = Players[i];
                if (player == null || player.Plane != plane) continue;
                if (player.X == x && player.Y == y) return player;
            }
            return null;
        }
    }
}
