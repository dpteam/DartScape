using System;
using System.Collections.Generic;
using DartScape.Core.Cache;
using DartScape.Game;

namespace DartScape.Tests
{
    /// <summary>
    /// Tests for reading the cache and for building collision from the real landscape.
    /// </summary>
    /// <remarks>
    /// These run against a synthetic cache buffer rather than the shipped one, so they work in
    /// CI where no cache is present. The format rules they encode are the ones that cost real
    /// debugging time: string terminators, one-based floor ids, and the object block's smart
    /// values.
    /// </remarks>
    public static class CacheTests
    {
        public static void Run()
        {
            Harness.Section("Cache format");

            BufferTests();
            FloorTests();
            ObjectTests();
            MapIndexTests();
        }

        private static void BufferTests()
        {
            Harness.Test("a cache string ends at a newline, not at zero", () =>
            {
                // This is the mistake that makes a whole floor table parse as two entries: the
                // terminator is 10, and stopping at 0 instead swallows the record's own
                // terminator and walks into the next definition.
                byte[] data = { (byte)'c', (byte)'l', (byte)'i', (byte)'f', (byte)'f', 10, 0, 0x42 };

                var buffer = new CacheBuffer(data);
                string name = buffer.GetString();

                Harness.Equal("cliff", name, "the name");
                Harness.Equal(6, buffer.Position, "the terminator was consumed, the next byte was not");

                // The zero that follows belongs to the enclosing record, not to the string.
                Harness.Equal(0, buffer.GetUnsignedByte(), "the record terminator is still there");
                Harness.Equal(0x42, buffer.GetUnsignedByte(), "reading continues after it");
            });

            Harness.Test("a smart value is one byte below 128", () =>
            {
                var buffer = new CacheBuffer(new byte[] { 0x7F });

                Harness.Equal(0x7F, buffer.GetSmart(), "one-byte form");
                Harness.Equal(1, buffer.Position, "one byte consumed");
            });

            Harness.Test("a smart value is two bytes from 128, offset by 0x8000", () =>
            {
                // 0x80 0x00 = 0x8000, minus 0x8000 = 0. This is the encoding object blocks
                // use, and it is NOT the signed variant used for model deltas.
                var buffer = new CacheBuffer(new byte[] { 0x80, 0x00 });

                Harness.Equal(0, buffer.GetSmart(), "0x8000 - 0x8000");
                Harness.Equal(2, buffer.Position, "two bytes consumed");
            });

            Harness.Test("the signed smart value uses different offsets", () =>
            {
                // The model-delta variant: one byte minus 64, or two bytes minus 0xC000.
                // Using it on an object block walks the ids backwards into negative numbers.
                var oneByte = new CacheBuffer(new byte[] { 0x40 });
                Harness.Equal(0, oneByte.GetSmartB(), "0x40 - 64");

                var twoByte = new CacheBuffer(new byte[] { 0xC0, 0x00 });
                Harness.Equal(0, twoByte.GetSmartB(), "0xC000 - 0xC000");
            });

            Harness.Test("reading past the end throws instead of returning zero", () =>
            {
                var buffer = new CacheBuffer(new byte[] { 1 });

                buffer.GetUnsignedByte();

                Harness.Throws(() => buffer.GetUnsignedByte(),
                    "a short read is an error, not a silent zero");
            });
        }

        private static void FloorTests()
        {
            Harness.Test("floor definitions parse from a stream of opcode records", () =>
            {
                // Two definitions: a grey floor with a name, then one with a colour and no
                // name. Written by hand to the format, including the newline terminators.
                var data = new List<byte>();

                data.Add(0); data.Add(2);                     // count = 2

                data.Add(1); data.Add(0xAA); data.Add(0xAA); data.Add(0xAA);   // colour
                data.Add(6); foreach (char c in "cliff") data.Add((byte)c);
                data.Add(10);                                  // string terminator
                data.Add(0);                                   // end of definition

                data.Add(1); data.Add(0x11); data.Add(0x22); data.Add(0x33);
                data.Add(2); data.Add(1);                      // texture 1 = water
                data.Add(0);

                FloorDefinitionSet floors = FloorDefinitionSet.Parse(data.ToArray());

                Harness.Equal(2, floors.Count, "two definitions");

                FloorDefinition cliff = floors.Get(0);
                Harness.NotNull(cliff, "first definition");
                Harness.Equal(0xAAAAAA, cliff.Colour, "colour");
                Harness.Equal("cliff", cliff.Name, "name");
                Harness.False(cliff.IsWater, "cliff is not water");

                FloorDefinition water = floors.Get(1);
                Harness.Equal(0x112233, water.Colour, "second colour");
                Harness.True(water.IsWater, "texture 1 is water");
            });

            Harness.Test("floor ids on tiles are one-based", () =>
            {
                // The client looks a tile's floor up as Flo.cache[id - 1], and 0 means "no
                // floor". Reading them zero-based shifts every surface by one entry, which is
                // invisible until it turns the grey road into the black dungeon stone.
                var data = new List<byte>();
                data.Add(0); data.Add(2);
                data.Add(1); data.Add(0x50); data.Add(0x50); data.Add(0x50); data.Add(0);  // road
                data.Add(1); data.Add(0x22); data.Add(0x22); data.Add(0x22); data.Add(0);  // darkstone

                FloorDefinitionSet floors = FloorDefinitionSet.Parse(data.ToArray());

                Harness.Equal(0x505050, floors.Get(0).Colour, "tile id 1 is entry 0");
                Harness.Equal(0x222222, floors.Get(1).Colour, "tile id 2 is entry 1");
            });

            Harness.Test("an unknown opcode stops the parse rather than guessing", () =>
            {
                var data = new List<byte>();
                data.Add(0); data.Add(3);
                data.Add(1); data.Add(0x01); data.Add(0x02); data.Add(0x03); data.Add(0);
                data.Add(99);                                  // not a real opcode
                data.Add(0);

                FloorDefinitionSet floors = FloorDefinitionSet.Parse(data.ToArray());

                Harness.Equal(3, floors.Count, "the array is still allocated");
                Harness.NotNull(floors.Get(0), "the valid definition survived");
                Harness.Null(floors.Get(1).Name, "the broken one has no name");
            });
        }

        private static void ObjectTests()
        {
            Harness.Test("object definitions parse with their sizes and options", () =>
            {
                // loc.idx: count, then one length per definition.
                var index = new List<byte> { 0, 1, 0, 11 };

                // loc.dat: a two-byte header, then one definition — 2x2, named, with a
                // "Chop down" option. The offsets in loc.idx are absolute into this file, so
                // the first definition starts at 2, not 0.
                var data = new List<byte>();
                data.Add(0); data.Add(0);                      // header
                data.Add(14); data.Add(2);                     // sizeX
                data.Add(15); data.Add(2);                     // sizeY
                data.Add(2); foreach (char c in "Tree") data.Add((byte)c); data.Add(10);
                data.Add(30); foreach (char c in "Chop down") data.Add((byte)c); data.Add(10);
                data.Add(19); data.Add(1);                     // has actions
                data.Add(0);

                ObjectDefinitionSet objects =
                    ObjectDefinitionSet.Parse(index.ToArray(), data.ToArray());

                ObjectDefinition tree = objects.Get(0);

                Harness.NotNull(tree, "the definition");
                Harness.Equal(2, tree.SizeX, "width");
                Harness.Equal(2, tree.SizeY, "depth");
                Harness.Equal("Tree", tree.Name, "name");
                Harness.True(tree.HasActions, "has actions");
                Harness.Equal("Chop down", tree.Actions[0], "first option");
            });

            Harness.Test("an object is solid and walkable unless it says otherwise", () =>
            {
                // The defaults are the trap: forgetting opcode 17 turns every flower and patch
                // of grass into a wall, which makes the world impassable and looks like a
                // pathfinding failure.
                var index = new List<byte> { 0, 1, 0, 3 };
                var data = new List<byte> { 0, 0, 0 };         // header, then an empty definition

                ObjectDefinitionSet objects =
                    ObjectDefinitionSet.Parse(index.ToArray(), data.ToArray());

                ObjectDefinition definition = objects.Get(0);

                Harness.True(definition.Solid, "solid by default");
                Harness.True(definition.Walkable, "walkable by default");
                Harness.Equal(1, definition.SizeX, "one tile wide");
            });

            Harness.Test("opcode 17 makes an object non-solid", () =>
            {
                var index = new List<byte> { 0, 1, 0, 3 };
                var data = new List<byte> { 0, 0, 17, 0 };     // header, then opcode 17, end

                ObjectDefinitionSet objects =
                    ObjectDefinitionSet.Parse(index.ToArray(), data.ToArray());

                Harness.False(objects.Get(0).Solid, "grass is not solid");
            });
        }

        private static void MapIndexTests()
        {
            Harness.Test("the map index maps region coordinates to file slots", () =>
            {
                // Seven bytes per row: packed coordinate, terrain slot, object slot, members.
                var data = new List<byte>();
                data.Add(0x32); data.Add(0x32);                 // region 50,50
                data.Add(0x01); data.Add(0x7E);                 // terrain 382
                data.Add(0x01); data.Add(0x7F);                 // objects 383
                data.Add(0);                                    // free

                MapIndex index = MapIndex.Parse(data.ToArray());

                Harness.Equal(1, index.Count, "one region");

                MapIndexEntry entry;
                Harness.True(index.TryGet(50, 50, out entry), "found 50,50");
                Harness.Equal(382, entry.TerrainFileId, "terrain slot");
                Harness.Equal(383, entry.ObjectFileId, "object slot");
                Harness.False(entry.Members, "free region");
            });

            Harness.Test("an unknown region is not found", () =>
            {
                MapIndex index = MapIndex.Parse(new byte[] { 0x32, 0x32, 0, 1, 0, 2, 0 });

                MapIndexEntry entry;
                Harness.False(index.TryGet(51, 51, out entry), "region 51,51 is not in the table");
            });
        }
    }
}
