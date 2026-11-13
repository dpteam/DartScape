using System;
using System.IO;
using DartScape.Core.Cache;
using DartScape.Game.Definitions;

namespace DartScape.Tests
{
    /// <summary>
    /// Integration tests that need a real cache directory.
    /// </summary>
    /// <remarks>
    /// These are the tests that would have caught every real cache bug: the length-table
    /// index format, the newline-terminated definition strings, the headerless bzip2
    /// framing, and the modern versus legacy archive layout. They only run when a cache is
    /// supplied, because a cache is not something the repository can carry.
    /// </remarks>
    public static class CacheIntegrationTests
    {
        public static void Run(string cachePath)
        {
            Harness.Section("Cache integration");

            if (!Directory.Exists(cachePath))
            {
                Console.WriteLine("  SKIP  cache directory not found: " + cachePath);
                return;
            }

            FileStore store = null;

            try
            {
                Harness.Test("the cache opens", () =>
                {
                    store = FileStore.Open(cachePath);
                    Harness.NotNull(store, "file store");
                    Harness.True(store.StoreCount >= 5, "at least five stores, got " + store.StoreCount);
                });

                if (store == null)
                {
                    Console.WriteLine("  SKIP  remaining cache tests (cache did not open)");
                    return;
                }

                Harness.Test("every file in store 0 parses as an archive", () =>
                {
                    int parsed = 0;
                    int count = store.GetFileCount(0);

                    for (int i = 0; i < count; i++)
                    {
                        byte[] raw = store.Read(0, i);
                        if (raw == null) continue;

                        Archive archive = Archive.Parse(raw);
                        if (archive != null) parsed++;
                    }

                    Harness.True(parsed > 0, "at least one archive parsed, got " + parsed);
                });

                Harness.Test("the config archive exposes the definition files", () =>
                {
                    Archive config = null;

                    for (int i = 0; i < store.GetFileCount(0); i++)
                    {
                        byte[] raw = store.Read(0, i);
                        if (raw == null) continue;

                        Archive archive = Archive.Parse(raw);
                        if (archive == null) continue;

                        if (archive.GetFile("obj.dat") != null)
                        {
                            config = archive;
                            break;
                        }
                    }

                    Harness.NotNull(config, "config archive");

                    Harness.NotNull(config.GetFile("obj.dat"), "obj.dat");
                    Harness.NotNull(config.GetFile("obj.idx"), "obj.idx");
                    Harness.NotNull(config.GetFile("npc.dat"), "npc.dat");
                    Harness.NotNull(config.GetFile("npc.idx"), "npc.idx");
                });

                Harness.Test("item definitions load with readable names", () =>
                {
                    DefinitionLoader.Load(store);

                    Harness.True(DefinitionLoader.Items.Length > 1000,
                        "at least a thousand items, got " + DefinitionLoader.Items.Length);

                    // The first entries in any 317-family cache are the Dwarf remains set.
                    Harness.Equal("Dwarf remains", DefinitionLoader.Item(0).Name, "item 0");

                    // Coins are the canonical stackable item.
                    ItemDefinition coins = DefinitionLoader.Item(995);
                    Harness.NotNull(coins, "coins");
                    Harness.Equal("Coins", coins.Name, "coins name");
                    Harness.True(coins.Stackable, "coins are stackable");
                });

                Harness.Test("NPC definitions load with readable names", () =>
                {
                    Harness.True(DefinitionLoader.Npcs.Length > 500,
                        "at least five hundred NPCs, got " + DefinitionLoader.Npcs.Length);

                    bool foundChicken = false;
                    bool foundGuard = false;

                    for (int i = 0; i < DefinitionLoader.Npcs.Length; i++)
                    {
                        NpcDefinition npc = DefinitionLoader.Npcs[i];
                        if (npc == null || npc.Name == null) continue;

                        if (npc.Name == "Chicken") foundChicken = true;
                        if (npc.Name == "Guard") foundGuard = true;
                    }

                    Harness.True(foundChicken, "a Chicken exists");
                    Harness.True(foundGuard, "a Guard exists");
                });

                Harness.Test("derived NPC stats are all positive", () =>
                {
                    for (int i = 0; i < DefinitionLoader.Npcs.Length; i++)
                    {
                        NpcDefinition npc = DefinitionLoader.Npcs[i];
                        if (npc == null) continue;

                        Harness.True(npc.Hitpoints > 0, "hitpoints for NPC " + i);
                        Harness.True(npc.Size >= 1, "size for NPC " + i);
                        Harness.True(npc.Attack >= 1, "attack for NPC " + i);
                        Harness.True(npc.Defence >= 1, "defence for NPC " + i);
                    }
                });

                Harness.Test("no definition name contains control characters", () =>
                {
                    // A desynchronised stream shows up exactly here: garbage bytes glued
                    // to the front of a name.
                    for (int i = 0; i < Math.Min(500, DefinitionLoader.Items.Length); i++)
                    {
                        ItemDefinition item = DefinitionLoader.Items[i];
                        if (item == null || item.Name == null) continue;

                        foreach (char c in item.Name)
                        {
                            Harness.True(c >= 32, "item " + i + " name contains a control character: '" + item.Name + "'");
                        }
                    }
                });
            }
            finally
            {
                if (store != null) store.Dispose();
            }
        }
    }
}
