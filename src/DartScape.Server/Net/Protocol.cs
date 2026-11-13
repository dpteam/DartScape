using System;
using System.Collections.Generic;

namespace DartScape.Server.Net
{
    /// <summary>
    /// Opcodes and packet sizes for the DartScape 317-derived protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fixed size table is essential: without it the server cannot read a packet it
    /// does not yet have a handler for, so one unimplemented feature would desynchronise
    /// the whole connection. Size <c>-1</c> means "the first byte of the payload is the
    /// length", <c>-2</c> means "the first two bytes are the length".
    /// </para>
    /// <para>
    /// The inbound table is the stock 317 one, because the client is the authority on
    /// what it sends. The outbound table only needs to be consistent with our own client.
    /// </para>
    /// </remarks>
    public static class Protocol
    {
        /// <summary>Protocol revision reported by clients.</summary>
        public const int Revision = 317;

        /// <summary>Size of the RSA login block in bytes (1024-bit key).</summary>
        public const int LoginBlockSize = 128;

        /// <summary>Maximum size of a single inbound packet payload.</summary>
        public const int MaxPacketSize = 5000;

        /// <summary>Number of skills the client renders.</summary>
        public const int SkillCount = 21;

        // -- inbound opcodes (client -> server) -------------------------------

        public const int InActivity = 0;
        public const int InRegionLoaded = 121;
        public const int InChat = 4;
        public const int InCommand = 103;
        public const int InWalkMap = 164;
        public const int InWalkMinimap = 248;
        public const int InWalkEntity = 98;
        public const int InWalkFollow = 36;
        public const int InButton = 185;
        public const int InItemAction = 122;
        public const int InItemOption = 41;
        public const int InItemOnItem = 53;
        public const int InItemOnObject = 192;
        public const int InItemOnNpc = 57;
        public const int InItemOnPlayer = 14;
        public const int InItemOnGround = 25;
        public const int InDropItem = 87;
        public const int InSwapItems = 214;
        public const int InPickupItem = 236;
        public const int InNpcAction = 155;
        public const int InNpcAction2 = 17;
        public const int InNpcAction3 = 21;
        public const int InNpcAction4 = 18;
        public const int InObjectAction = 132;
        public const int InObjectAction2 = 252;
        public const int InObjectAction3 = 70;
        public const int InObjectAction4 = 234;
        public const int InPlayerAction = 128;
        public const int InPlayerAction2 = 153;
        public const int InPlayerAction3 = 14;
        public const int InPlayerAction4 = 39;
        public const int InFollow = 139;
        public const int InIdle = 202;
        public const int InFocusChange = 3;
        public const int InMouseClick = 241;
        public const int InCameraMove = 86;
        public const int InEnterAmount = 208;
        public const int InInterfaceClosed = 130;
        public const int InSwitchTab = 43;
        public const int InClanChat = 89;

        // -- outbound opcodes (server -> client) ------------------------------

        public const int OutPlayerUpdate = 81;
        public const int OutNpcUpdate = 65;
        public const int OutGroundItems = 84;
        public const int OutGroundItemAdd = 44;
        public const int OutGroundItemRemove = 156;
        public const int OutGroundItemAmount = 84;
        public const int OutMessage = 253;
        public const int OutLogout = 109;
        public const int OutSetRegion = 73;
        public const int OutMapRegion = 60;
        public const int OutPlayerPosition = 85;
        public const int OutSetSidebar = 71;
        public const int OutShowInterface = 97;
        public const int OutCloseInterface = 219;
        public const int OutSetItems = 53;
        public const int OutUpdateItem = 90;
        public const int OutSetSkills = 134;
        public const int OutUpdateSkill = 49;
        public const int OutAnimation = 45;
        public const int OutSound = 105;
        public const int OutChat = 196;
        public const int OutMultiway = 210;
        public const int OutSystemUpdate = 114;
        public const int OutMinimapState = 99;
        public const int OutObjectAdd = 151;
        public const int OutObjectRemove = 101;
        public const int OutObjectAnimation = 160;
        public const int OutProjectile = 117;
        public const int OutSpotAnimation = 4;
        public const int OutRunEnergy = 110;
        public const int OutWeight = 240;
        public const int OutPlayerOption = 104;
        public const int OutScrollbar = 79;

        // -- inbound packet sizes ---------------------------------------------

        /// <summary>
        /// Payload size per inbound opcode. <c>-1</c> = one length byte,
        /// <c>-2</c> = two length bytes, <c>0</c> = unknown (treated as a single byte).
        /// </summary>
        public static readonly int[] InboundSizes = BuildInboundSizes();

        private static int[] BuildInboundSizes()
        {
            // Stock 317 table. Anything not listed defaults to "unknown", which the
            // reader treats as a one byte payload and logs; that is safer than guessing.
            var sizes = new int[256];
            for (int i = 0; i < sizes.Length; i++) sizes[i] = 0;

            sizes[InActivity] = 0;
            sizes[InChat] = -1;
            sizes[InCommand] = -1;
            sizes[InRegionLoaded] = 0;
            sizes[InWalkMap] = -1;
            sizes[InWalkMinimap] = -1;
            sizes[InWalkEntity] = -1;
            sizes[InWalkFollow] = -1;
            sizes[InButton] = 2;
            sizes[InItemAction] = 6;
            sizes[InItemOption] = 6;
            sizes[InItemOnItem] = 4;
            sizes[InItemOnObject] = 12;
            sizes[InItemOnNpc] = 6;
            sizes[InItemOnPlayer] = 8;
            sizes[InItemOnGround] = 10;
            sizes[InDropItem] = 6;
            sizes[InSwapItems] = 4;
            sizes[InPickupItem] = 6;
            sizes[InNpcAction] = 2;
            sizes[InNpcAction2] = 2;
            sizes[InNpcAction3] = 2;
            sizes[InNpcAction4] = 2;
            sizes[InObjectAction] = 6;
            sizes[InObjectAction2] = 6;
            sizes[InObjectAction3] = 6;
            sizes[InObjectAction4] = 6;
            sizes[InPlayerAction] = 2;
            sizes[InPlayerAction2] = 2;
            sizes[InPlayerAction4] = 2;
            sizes[InFollow] = 2;
            sizes[InIdle] = 0;
            sizes[InFocusChange] = 1;
            sizes[InMouseClick] = 0;
            sizes[InCameraMove] = 4;
            sizes[InEnterAmount] = 4;
            sizes[InInterfaceClosed] = 0;
            sizes[InSwitchTab] = 2;
            sizes[InClanChat] = -1;

            // A few opcodes that the stock client sends but that carry no useful data.
            sizes[226] = 0;
            sizes[78] = 0;
            sizes[189] = 0;
            sizes[226] = 0;

            return sizes;
        }

        /// <summary>Human readable name of an inbound opcode, for logs.</summary>
        public static string NameOf(int opcode)
        {
            switch (opcode)
            {
                case InActivity: return "activity";
                case InRegionLoaded: return "region-loaded";
                case InChat: return "chat";
                case InCommand: return "command";
                case InWalkMap: return "walk-map";
                case InWalkMinimap: return "walk-minimap";
                case InWalkEntity: return "walk-entity";
                case InWalkFollow: return "walk-follow";
                case InButton: return "button";
                case InItemAction: return "item-action";
                case InItemOption: return "item-option";
                case InItemOnItem: return "item-on-item";
                case InItemOnObject: return "item-on-object";
                case InItemOnNpc: return "item-on-npc";
                case InItemOnGround: return "item-on-ground";
                case InDropItem: return "drop-item";
                case InSwapItems: return "swap-items";
                case InPickupItem: return "pickup-item";
                case InNpcAction: return "npc-action";
                case InObjectAction: return "object-action";
                case InPlayerAction: return "player-action";
                case InFollow: return "follow";
                case InIdle: return "idle";
                case InEnterAmount: return "enter-amount";
                case InSwitchTab: return "switch-tab";
                default: return "opcode-" + opcode.ToString();
            }
        }
    }
}
