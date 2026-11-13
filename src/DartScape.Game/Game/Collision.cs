using System;
using System.Collections.Generic;

namespace DartScape.Game
{
    /// <summary>
    /// Per-tile collision flags, matching the 317 wire format exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constants are named by the direction of <em>movement</em> they forbid, not by the
    /// side of the tile they sit on. That distinction is the one thing that is easy to get
    /// backwards: a flag on a tile records "you cannot move in this direction into this tile",
    /// which is the opposite of "this edge of the tile is a wall". Both descriptions are true,
    /// and mixing them produces a wall that blocks one way only.
    /// </para>
    /// <para>
    /// Two facts from the original that are easy to miss and expensive to get wrong:
    /// </para>
    /// <list type="bullet">
    /// <item>An interior tile starts as <c>0x1000000</c> — "void, no floor". The map loader
    /// clears that bit for every tile of every loaded block. A tile that was never loaded is
    /// therefore unwalkable, which is what stops a player walking into the blank space beyond
    /// the map.</item>
    /// <item>A movement test is not "is this bit set" but "does this mask match", where the mask
    /// includes the void bit, the blocked bit, a reserved bit, the solid bit and one direction
    /// bit. Testing only the solid bit lets a character walk into the void.</item>
    /// </list>
    /// </remarks>
    public sealed class CollisionMap
    {
        // -- direction bits: the movement they forbid -----------------------------

        /// <summary>Movement to the south-east is blocked.</summary>
        public const int BlockMoveSouthEast = 0x01;

        /// <summary>Movement to the south is blocked.</summary>
        public const int BlockMoveSouth = 0x02;

        /// <summary>Movement to the south-west is blocked.</summary>
        public const int BlockMoveSouthWest = 0x04;

        /// <summary>Movement to the west is blocked.</summary>
        public const int BlockMoveWest = 0x08;

        /// <summary>Movement to the north-west is blocked.</summary>
        public const int BlockMoveNorthWest = 0x10;

        /// <summary>Movement to the north is blocked.</summary>
        public const int BlockMoveNorth = 0x20;

        /// <summary>Movement to the north-east is blocked.</summary>
        public const int BlockMoveNorthEast = 0x40;

        /// <summary>Movement to the east is blocked.</summary>
        public const int BlockMoveEast = 0x80;

        // -- tile-level bits ------------------------------------------------------

        /// <summary>The tile is occupied by a solid object.</summary>
        public const int Solid = 0x100;

        /// <summary>Impenetrable: blocks projectiles as well as walkers.</summary>
        public const int Impenetrable = 0x20000;

        /// <summary>Whole tile blocked. Set by <see cref="MarkBlocked"/>.</summary>
        public const int Blocked = 0x200000;

        /// <summary>
        /// Reserved, present in every movement mask and never set by anything in the client.
        /// Treated as blocking, which is what the original's masks imply.
        /// </summary>
        public const int Reserved = 0x80000;

        /// <summary>Void: no floor here. Cleared per tile when a map block loads.</summary>
        public const int Void = 0x1000000;

        /// <summary>The part of a movement mask that is not direction-specific.</summary>
        public const int WalkMaskBase = Void | Blocked | Reserved | Solid;

        /// <summary>Every direction bit.</summary>
        public const int AllDirections =
            BlockMoveNorth | BlockMoveNorthEast | BlockMoveEast | BlockMoveSouthEast |
            BlockMoveSouth | BlockMoveSouthWest | BlockMoveWest | BlockMoveNorthWest;

        /// <summary>Size of the local map, in tiles, matching the client's scene.</summary>
        public const int Size = 104;

        private readonly int[] _flags = new int[Size * Size];

        /// <summary>World coordinate of tile (0, 0) in this map.</summary>
        public readonly int BaseX;

        /// <summary>World coordinate of tile (0, 0) in this map.</summary>
        public readonly int BaseY;

        public CollisionMap(int baseX, int baseY)
        {
            BaseX = baseX;
            BaseY = baseY;
            Reset();
        }

        /// <summary>
        /// Puts the map in the state the original starts from: a fully blocked border ring and
        /// a void interior.
        /// </summary>
        /// <remarks>
        /// The border matters. It is what stops a pathfinder walking off the edge of the local
        /// map, and it means the neighbour lookup in the walk test never needs a bounds check
        /// beyond the 104 limit.
        /// </remarks>
        public void Reset()
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    bool border = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;
                    _flags[y * Size + x] = border ? 0xFFFFFF : Void;
                }
            }
        }

        /// <summary>
        /// Clears the void bit, marking a tile as having a floor.
        /// </summary>
        /// <remarks>
        /// This is what the map loader does for every tile of every loaded block. A collision
        /// map that only marks obstacles and never marks floors produces a world where nothing
        /// can move at all, because every tile is still void.
        /// </remarks>
        public void MarkFloor(int worldX, int worldY)
        {
            int index = IndexOf(worldX, worldY);
            if (index < 0) return;

            _flags[index] &= ~Void;
        }

        /// <summary>Reads the flags of a world tile. Out-of-range reads are fully blocked.</summary>
        public int FlagsAt(int worldX, int worldY)
        {
            int index = IndexOf(worldX, worldY);
            return index < 0 ? 0xFFFFFF : _flags[index];
        }

        /// <summary>
        /// True when a tile can be stood on at all, ignoring edge walls.
        /// </summary>
        /// <remarks>
        /// A tile with a wall on one of its edges is still standable — the wall only blocks
        /// movement across that edge. This tests the base mask only, which is what "is there
        /// floor here that an entity could occupy" actually means.
        /// </remarks>
        public bool IsWalkable(int worldX, int worldY)
        {
            return (FlagsAt(worldX, worldY) & WalkMaskBase) == 0;
        }

        /// <summary>Sets flags on a world tile, replacing what was there.</summary>
        public void SetFlags(int worldX, int worldY, int flags)
        {
            int index = IndexOf(worldX, worldY);
            if (index < 0) return;

            _flags[index] = flags;
        }

        /// <summary>Adds flags to a world tile.</summary>
        public void AddFlags(int worldX, int worldY, int flags)
        {
            int index = IndexOf(worldX, worldY);
            if (index < 0) return;

            _flags[index] |= flags;
        }

        /// <summary>Marks a tile as fully occupied from every direction.</summary>
        public void MarkSolid(int worldX, int worldY)
        {
            AddFlags(worldX, worldY, Solid | AllDirections);
        }

        /// <summary>Marks a whole tile blocked without claiming it is solid.</summary>
        public void MarkBlocked(int worldX, int worldY)
        {
            AddFlags(worldX, worldY, Blocked);
        }

        /// <summary>Marks a tile as blocking projectiles but not walkers.</summary>
        public void MarkProjectileBlocked(int worldX, int worldY)
        {
            AddFlags(worldX, worldY, Impenetrable);
        }

        private int IndexOf(int worldX, int worldY)
        {
            int x = worldX - BaseX;
            int y = worldY - BaseY;

            if (x < 0 || y < 0 || x >= Size || y >= Size) return -1;

            return y * Size + x;
        }

        /// <summary>
        /// Marks a rectangular object that occupies <paramref name="sizeX"/> by
        /// <paramref name="sizeY"/> tiles, rotated by <paramref name="rotation"/>.
        /// </summary>
        /// <remarks>
        /// Rotation 1 or 3 swaps the footprint — that is what the original loader does, and
        /// getting it wrong makes every rotated object collide with the wrong tiles. The
        /// coordinate is the footprint's south-west corner.
        /// </remarks>
        public void MarkObject(int worldX, int worldY, int sizeX, int sizeY, int rotation, bool impenetrable)
        {
            if (rotation == 1 || rotation == 3)
            {
                int swap = sizeX;
                sizeX = sizeY;
                sizeY = swap;
            }

            int occupied = Solid;
            if (impenetrable) occupied |= Impenetrable;

            for (int dx = 0; dx < sizeX; dx++)
            {
                for (int dy = 0; dy < sizeY; dy++)
                {
                    AddFlags(worldX + dx, worldY + dy, occupied);
                }
            }
        }

        /// <summary>
        /// Marks a wall, using the original's tables.
        /// </summary>
        /// <param name="position">
        /// The object's config type: 0 = straight wall, 1 or 3 = diagonal wall, 2 = a wall with
        /// two sides. The tables are reproduced literally because the diagonal cases are not
        /// symmetric and deriving them from first principles gets them wrong.
        /// </param>
        /// <param name="rotation">The object's face, 0..3.</param>
        public void MarkWall(int worldX, int worldY, int position, int rotation, bool impenetrable)
        {
            int extra = impenetrable ? Impenetrable : 0;

            if (position == 0)
            {
                switch (rotation & 3)
                {
                    case 0:
                        // Wall on the west edge: eastward movement into this tile is blocked,
                        // and westward movement into the tile to the west.
                        AddFlags(worldX, worldY, BlockMoveEast | extra);
                        AddFlags(worldX - 1, worldY, BlockMoveWest | extra);
                        break;
                    case 1:
                        AddFlags(worldX, worldY, BlockMoveSouth | extra);
                        AddFlags(worldX, worldY + 1, BlockMoveNorth | extra);
                        break;
                    case 2:
                        AddFlags(worldX, worldY, BlockMoveWest | extra);
                        AddFlags(worldX + 1, worldY, BlockMoveEast | extra);
                        break;
                    default:
                        AddFlags(worldX, worldY, BlockMoveNorth | extra);
                        AddFlags(worldX, worldY - 1, BlockMoveSouth | extra);
                        break;
                }

                return;
            }

            if (position == 1 || position == 3)
            {
                switch (rotation & 3)
                {
                    case 0:
                        AddFlags(worldX, worldY, BlockMoveSouthEast | extra);
                        AddFlags(worldX - 1, worldY + 1, BlockMoveNorthWest | extra);
                        break;
                    case 1:
                        AddFlags(worldX, worldY, BlockMoveSouthWest | extra);
                        AddFlags(worldX + 1, worldY + 1, BlockMoveNorthEast | extra);
                        break;
                    case 2:
                        AddFlags(worldX, worldY, BlockMoveNorthWest | extra);
                        AddFlags(worldX + 1, worldY - 1, BlockMoveSouthEast | extra);
                        break;
                    default:
                        AddFlags(worldX, worldY, BlockMoveNorthEast | extra);
                        AddFlags(worldX - 1, worldY - 1, BlockMoveSouthWest | extra);
                        break;
                }

                return;
            }

            if (position == 2)
            {
                switch (rotation & 3)
                {
                    case 0:
                        AddFlags(worldX, worldY, BlockMoveEast | BlockMoveSouth | extra);
                        AddFlags(worldX - 1, worldY, BlockMoveWest | extra);
                        AddFlags(worldX, worldY + 1, BlockMoveNorth | extra);
                        break;
                    case 1:
                        AddFlags(worldX, worldY, BlockMoveWest | BlockMoveSouth | extra);
                        AddFlags(worldX, worldY + 1, BlockMoveNorth | extra);
                        AddFlags(worldX + 1, worldY, BlockMoveEast | extra);
                        break;
                    case 2:
                        AddFlags(worldX, worldY, BlockMoveNorth | BlockMoveWest | extra);
                        AddFlags(worldX + 1, worldY, BlockMoveEast | extra);
                        AddFlags(worldX, worldY - 1, BlockMoveSouth | extra);
                        break;
                    default:
                        AddFlags(worldX, worldY, BlockMoveEast | BlockMoveNorth | extra);
                        AddFlags(worldX, worldY - 1, BlockMoveSouth | extra);
                        AddFlags(worldX - 1, worldY, BlockMoveWest | extra);
                        break;
                }
            }
        }

        /// <summary>
        /// True when a step from one tile to an adjacent one is allowed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the original's test, transcribed. An orthogonal step checks one mask on the
        /// destination; a diagonal step checks three — the destination's diagonal mask plus both
        /// orthogonal neighbours' masks. That third check is what stops a character cutting the
        /// corner between two walls, and it is the single most visible difference between a real
        /// pathfinder and a straight line.
        /// </para>
        /// <para>
        /// The masks include the void bit, so a step into unloaded ground is refused without a
        /// separate bounds test.
        /// </para>
        /// </remarks>
        public bool CanStep(int fromX, int fromY, int toX, int toY)
        {
            int dx = toX - fromX;
            int dy = toY - fromY;

            if (dx < -1 || dx > 1 || dy < -1 || dy > 1) return false;
            if (dx == 0 && dy == 0) return false;

            int destination = FlagsAt(toX, toY);

            if (dx == 0)
            {
                if (dy > 0) return (destination & (WalkMaskBase | BlockMoveNorth)) == 0;
                return (destination & (WalkMaskBase | BlockMoveSouth)) == 0;
            }

            if (dy == 0)
            {
                if (dx > 0) return (destination & (WalkMaskBase | BlockMoveEast)) == 0;
                return (destination & (WalkMaskBase | BlockMoveWest)) == 0;
            }

            // Diagonal: destination's own diagonal bit, plus both orthogonal neighbours, so a
            // character cannot slip between the corners of two walls.
            int horizontal = FlagsAt(toX, fromY);
            int vertical = FlagsAt(fromX, toY);

            if (dx > 0 && dy > 0)
            {
                return (destination & (WalkMaskBase | BlockMoveNorthEast)) == 0
                    && (horizontal & (WalkMaskBase | BlockMoveEast)) == 0
                    && (vertical & (WalkMaskBase | BlockMoveNorth)) == 0;
            }

            if (dx < 0 && dy > 0)
            {
                return (destination & (WalkMaskBase | BlockMoveNorthWest)) == 0
                    && (horizontal & (WalkMaskBase | BlockMoveWest)) == 0
                    && (vertical & (WalkMaskBase | BlockMoveNorth)) == 0;
            }

            if (dx > 0 && dy < 0)
            {
                return (destination & (WalkMaskBase | BlockMoveSouthEast)) == 0
                    && (horizontal & (WalkMaskBase | BlockMoveEast)) == 0
                    && (vertical & (WalkMaskBase | BlockMoveSouth)) == 0;
            }

            return (destination & (WalkMaskBase | BlockMoveSouthWest)) == 0
                && (horizontal & (WalkMaskBase | BlockMoveWest)) == 0
                && (vertical & (WalkMaskBase | BlockMoveSouth)) == 0;
        }

        /// <summary>
        /// True when standing on (x, y) is close enough to act on a solid object occupying a
        /// footprint of the given size.
        /// </summary>
        /// <remarks>
        /// This is what makes a click on an NPC or a tree walk you to a tile <em>next to</em> it
        /// rather than onto it. The original calls this from the goal test inside the
        /// pathfinder, so the search stops as soon as it reaches any adjacent tile rather than
        /// running all the way to the target and failing.
        /// </remarks>
        public bool ReachedFacingObject(int x, int y, int objectX, int objectY,
            int sizeX, int sizeY, int surroundings)
        {
            int maxX = objectX + sizeX - 1;
            int maxY = objectY + sizeY - 1;

            if (x >= objectX && x <= maxX && y >= objectY && y <= maxY) return true;

            if (x == objectX - 1 && y >= objectY && y <= maxY
                && (FlagsAt(x, y) & BlockMoveWest) == 0 && (surroundings & BlockMoveWest) == 0) return true;

            if (x == maxX + 1 && y >= objectY && y <= maxY
                && (FlagsAt(x, y) & BlockMoveEast) == 0 && (surroundings & BlockMoveEast) == 0) return true;

            if (y == objectY - 1 && x >= objectX && x <= maxX
                && (FlagsAt(x, y) & BlockMoveSouth) == 0 && (surroundings & BlockMoveSouth) == 0) return true;

            if (y == maxY + 1 && x >= objectX && x <= maxX
                && (FlagsAt(x, y) & BlockMoveNorth) == 0 && (surroundings & BlockMoveNorth) == 0) return true;

            return false;
        }
    }

    /// <summary>
    /// Breadth-first pathfinding over a <see cref="CollisionMap"/>, following the original.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the original's algorithm, not A*: a plain BFS over the 104x104 local grid with a
    /// ring-buffer queue and the direction that reached each tile stored in a visited array. It
    /// is not the fastest possible search, and that is deliberate — it produces the paths players
    /// expect, and a "better" algorithm finds paths a tile shorter that feel wrong.
    /// </para>
    /// <para>
    /// Three behaviours are copied because they are visible in play: the goal test can be
    /// "adjacent to this object" rather than "on this tile", an unreachable target falls back to
    /// the closest reachable tile, and a path longer than 25 tiles is truncated rather than
    /// refused.
    /// </para>
    /// </remarks>
    public sealed class PathFinder
    {
        /// <summary>Longest path a single request may produce, matching the original.</summary>
        public const int MaxPathLength = 25;

        /// <summary>Queue capacity. The original uses 4000.</summary>
        private const int QueueCapacity = 4000;

        private const int Size = CollisionMap.Size;

        private readonly int[] _queueX = new int[QueueCapacity];
        private readonly int[] _queueY = new int[QueueCapacity];
        private readonly byte[] _visited = new byte[Size * Size];
        private readonly int[] _distance = new int[Size * Size];

        private readonly CollisionMap _map;

        /// <summary>True when the last search could not reach its target and stopped short.</summary>
        public bool LastTargetUnreachable { get; private set; }

        public PathFinder(CollisionMap map)
        {
            if (map == null) throw new ArgumentNullException("map");
            _map = map;
        }

        /// <summary>Finds a path to a tile, excluding the starting tile.</summary>
        public List<Position> Find(Position from, Position to, bool closestIfBlocked = true)
        {
            return Search(from, to, 0, 0, 0, closestIfBlocked);
        }

        /// <summary>
        /// Finds a path to any tile adjacent to a footprint, which is how an interaction walks
        /// you next to what you clicked instead of onto it.
        /// </summary>
        public List<Position> FindAdjacent(Position from, Position target, int sizeX, int sizeY,
            int surroundings = 0)
        {
            return Search(from, target, sizeX, sizeY, surroundings, false);
        }

        private List<Position> Search(Position from, Position to, int sizeX, int sizeY,
            int surroundings, bool closestIfBlocked)
        {
            var path = new List<Position>(MaxPathLength);
            LastTargetUnreachable = false;

            if (from.Plane != to.Plane) return path;
            if (from == to && sizeX == 0) return path;

            int startX = from.X - _map.BaseX;
            int startY = from.Y - _map.BaseY;
            int targetX = to.X - _map.BaseX;
            int targetY = to.Y - _map.BaseY;

            if (startX < 1 || startY < 1 || startX > Size - 2 || startY > Size - 2) return path;

            if (targetX < 1) targetX = 1;
            if (targetY < 1) targetY = 1;
            if (targetX > Size - 2) targetX = Size - 2;
            if (targetY > Size - 2) targetY = Size - 2;

            Array.Clear(_visited, 0, _visited.Length);
            for (int i = 0; i < _distance.Length; i++) _distance[i] = 0x5F5E0FF;

            int head = 0;
            int tail = 0;

            _queueX[tail] = startX;
            _queueY[tail] = startY;
            tail++;

            // 99 marks the origin so the backtrack knows when to stop.
            _visited[startY * Size + startX] = 99;
            _distance[startY * Size + startX] = 0;

            bool reached = false;
            int foundX = startX;
            int foundY = startY;

            while (head != tail)
            {
                int cx = _queueX[head];
                int cy = _queueY[head];
                head = (head + 1) % QueueCapacity;

                bool atGoal = sizeX > 0
                    ? _map.ReachedFacingObject(cx + _map.BaseX, cy + _map.BaseY,
                        to.X, to.Y, sizeX, sizeY, surroundings)
                    : (cx == targetX && cy == targetY);

                if (atGoal && !(cx == startX && cy == startY))
                {
                    reached = true;
                    foundX = cx;
                    foundY = cy;
                    break;
                }

                int nextDistance = _distance[cy * Size + cx] + 1;

                // The marker stored on a tile is the direction that leads BACK to its parent:
                // 1 = y+1, 2 = x+1, 4 = y-1, 8 = x-1. A step east therefore stores 8, because
                // the way back is west. Swapping those two produces a path that zig-zags into
                // a wall, which is exactly what the test caught.
                Expand(cx, cy, cx, cy + 1, 0x04, nextDistance, ref tail);   // north
                Expand(cx, cy, cx + 1, cy, 0x08, nextDistance, ref tail);   // east
                Expand(cx, cy, cx, cy - 1, 0x01, nextDistance, ref tail);   // south
                Expand(cx, cy, cx - 1, cy, 0x02, nextDistance, ref tail);   // west

                Expand(cx, cy, cx - 1, cy + 1, 0x06, nextDistance, ref tail);   // north-west
                Expand(cx, cy, cx + 1, cy + 1, 0x0C, nextDistance, ref tail);   // north-east
                Expand(cx, cy, cx - 1, cy - 1, 0x03, nextDistance, ref tail);   // south-west
                Expand(cx, cy, cx + 1, cy - 1, 0x09, nextDistance, ref tail);   // south-east
            }

            if (!reached)
            {
                if (!closestIfBlocked) return path;

                // Walk as close as possible: the 3x3 block around the target, nearest first.
                int best = 100;

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = targetX + dx;
                        int ny = targetY + dy;

                        if (nx < 1 || ny < 1 || nx > Size - 2 || ny > Size - 2) continue;

                        int marker = _visited[ny * Size + nx];
                        if (marker == 0 || marker == 99) continue;

                        int distance = _distance[ny * Size + nx];
                        if (distance >= best) continue;

                        best = distance;
                        foundX = nx;
                        foundY = ny;
                    }
                }

                if (best == 100) return path;

                LastTargetUnreachable = true;
            }

            // --- backtrack ----------------------------------------------------
            // The guard is the whole grid, not the path cap. Capping here truncates the path at
            // the destination end, leaving a gap next to the player — which is a teleport, and a
            // silent one. The cap is applied below, to the path in the order it is walked.
            int walkX = foundX;
            int walkY = foundY;
            int guard = 0;

            while (!(walkX == startX && walkY == startY))
            {
                if (++guard > Size * Size) break;

                int marker = _visited[walkY * Size + walkX];
                if (marker == 0) break;

                path.Add(new Position(walkX + _map.BaseX, walkY + _map.BaseY, from.Plane));

                // Bit 1 = y+1, 2 = x+1, 4 = y-1, 8 = x-1. These step back towards the parent.
                if ((marker & 0x02) != 0) walkX++;
                else if ((marker & 0x08) != 0) walkX--;

                if ((marker & 0x01) != 0) walkY++;
                else if ((marker & 0x04) != 0) walkY--;
            }

            path.Reverse();

            if (path.Count > MaxPathLength) path.RemoveRange(MaxPathLength, path.Count - MaxPathLength);

            return path;
        }

        private void Expand(int fromX, int fromY, int toX, int toY, int marker, int distance, ref int tail)
        {
            if (toX < 1 || toY < 1 || toX > Size - 2 || toY > Size - 2) return;
            if (_visited[toY * Size + toX] != 0) return;

            if (!_map.CanStep(fromX + _map.BaseX, fromY + _map.BaseY, toX + _map.BaseX, toY + _map.BaseY)) return;

            _visited[toY * Size + toX] = (byte)marker;
            _distance[toY * Size + toX] = distance;

            _queueX[tail] = toX;
            _queueY[tail] = toY;
            tail = (tail + 1) % QueueCapacity;
        }
    }
}
