using System;

namespace DartScape.Server.Game
{
    /// <summary>
    /// A point in the world: tile coordinates plus a plane (floor).
    /// </summary>
    /// <remarks>
    /// A struct, because positions are copied constantly (pathfinding, updates,
    /// collision checks) and allocating a class per step would put real pressure on
    /// the gen-0 heap at 1.66 ticks per second with dozens of entities.
    /// </remarks>
    public struct Position : IEquatable<Position>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Plane;

        public Position(int x, int y, int plane)
        {
            X = x;
            Y = y;
            Plane = plane;
        }

        /// <summary>Manhattan distance on the same plane, or a large value across planes.</summary>
        public int DistanceTo(Position other)
        {
            if (Plane != other.Plane) return 10000;
            return Math.Abs(X - other.X) + Math.Abs(Y - other.Y);
        }

        /// <summary>Chebyshev distance — the "tiles within N" metric used for vision.</summary>
        public int ChebyshevDistance(Position other)
        {
            if (Plane != other.Plane) return 10000;
            return Math.Max(Math.Abs(X - other.X), Math.Abs(Y - other.Y));
        }

        public bool IsWithin(Position other, int radius)
        {
            return ChebyshevDistance(other) <= radius;
        }

        /// <summary>True when this tile is inside the same 8x8 map region as <paramref name="other"/>.</summary>
        public bool InSameRegion(Position other)
        {
            return Plane == other.Plane && (X >> 3) == (other.X >> 3) && (Y >> 3) == (other.Y >> 3);
        }

        /// <summary>Packs the position into a single int for hash sets and dictionaries.</summary>
        public int Pack()
        {
            return ((Plane & 0x3) << 28) | ((X & 0x3FFF) << 14) | (Y & 0x3FFF);
        }

        public static Position Unpack(int packed)
        {
            return new Position((packed >> 14) & 0x3FFF, packed & 0x3FFF, (packed >> 28) & 0x3);
        }

        public Position Translate(int dx, int dy)
        {
            return new Position(X + dx, Y + dy, Plane);
        }

        public bool Equals(Position other)
        {
            return X == other.X && Y == other.Y && Plane == other.Plane;
        }

        public override bool Equals(object obj)
        {
            return obj is Position && Equals((Position)obj);
        }

        public override int GetHashCode()
        {
            return Pack();
        }

        public static bool operator ==(Position a, Position b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Position a, Position b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return X.ToString() + "," + Y.ToString() + "," + Plane.ToString();
        }
    }

    /// <summary>The eight compass directions plus "none", matching the 317 walk encoding.</summary>
    public static class Direction
    {
        public const int None = -1;

        /// <summary>Delta X for each direction index 0..7 (0 = north, clockwise).</summary>
        public static readonly int[] DeltaX = { 0, 1, 1, 1, 0, -1, -1, -1 };

        /// <summary>Delta Y for each direction index 0..7.</summary>
        public static readonly int[] DeltaY = { 1, 1, 0, -1, -1, -1, 0, 1 };

        /// <summary>Player update direction values (1..12), where running is direction + 4.</summary>
        public static readonly int[] WalkDirection = { 1, 3, 4, 6, 7, 9, 10, 12 };

        /// <summary>Returns the direction index from <paramref name="from"/> to <paramref name="to"/>, or -1.</summary>
        public static int Between(Position from, Position to)
        {
            int dx = to.X - from.X;
            int dy = to.Y - from.Y;

            if (dx < -1) dx = -1;
            if (dx > 1) dx = 1;
            if (dy < -1) dy = -1;
            if (dy > 1) dy = 1;

            for (int i = 0; i < 8; i++)
            {
                if (DeltaX[i] == dx && DeltaY[i] == dy) return i;
            }
            return None;
        }
    }
}
