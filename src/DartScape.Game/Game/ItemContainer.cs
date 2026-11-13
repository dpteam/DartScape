using System;
using System.Collections.Generic;
using DartScape.Game.Definitions;

namespace DartScape.Game
{
    /// <summary>A stack of one item type.</summary>
    public struct ItemStack
    {
        public int Id;
        public int Amount;

        public ItemStack(int id, int amount)
        {
            Id = id;
            Amount = amount;
        }

        /// <summary>An empty slot. Item id -1 is never valid in the cache.</summary>
        public static readonly ItemStack Empty = new ItemStack(-1, 0);

        public bool IsEmpty
        {
            get { return Id < 0 || Amount <= 0; }
        }

        public override string ToString()
        {
            return IsEmpty ? "empty" : Amount.ToString() + "x" + Id.ToString();
        }
    }

    /// <summary>
    /// A fixed-size container of item stacks: the inventory, the bank, a shop.
    /// </summary>
    /// <remarks>
    /// Containers are the one place where the server must be exact, because the client
    /// mirrors them byte for byte. Every mutation returns a description of what changed
    /// so the network layer can send the minimum number of packets instead of
    /// re-sending the whole container on every pickup.
    /// </remarks>
    public sealed class ItemContainer
    {
        private readonly ItemStack[] _slots;

        /// <summary>Stable identity used by the client to address this container.</summary>
        public readonly int InterfaceId;

        public ItemContainer(int interfaceId, int size)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException("size");
            InterfaceId = interfaceId;
            _slots = new ItemStack[size];
            for (int i = 0; i < size; i++) _slots[i] = ItemStack.Empty;
        }

        public int Size
        {
            get { return _slots.Length; }
        }

        public ItemStack this[int slot]
        {
            get { return slot >= 0 && slot < _slots.Length ? _slots[slot] : ItemStack.Empty; }
        }

        public ItemStack[] Snapshot()
        {
            var copy = new ItemStack[_slots.Length];
            Array.Copy(_slots, copy, _slots.Length);
            return copy;
        }

        public int FreeSlots
        {
            get
            {
                int free = 0;
                for (int i = 0; i < _slots.Length; i++) if (_slots[i].IsEmpty) free++;
                return free;
            }
        }

        public bool IsFull
        {
            get { return FreeSlots == 0; }
        }

        /// <summary>Total number of the given item across all slots.</summary>
        public int Count(int itemId)
        {
            int total = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Id == itemId) total += _slots[i].Amount;
            }
            return total;
        }

        public bool Contains(int itemId)
        {
            return Count(itemId) > 0;
        }

        /// <summary>First slot holding the item, or -1.</summary>
        public int IndexOf(int itemId)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Id == itemId) return i;
            }
            return -1;
        }

        /// <summary>
        /// Adds items, stacking where the definition allows it.
        /// </summary>
        /// <returns>How many were actually added; the caller must handle the remainder.</returns>
        public int Add(int itemId, int amount)
        {
            if (itemId < 0 || amount <= 0) return 0;

            ItemDefinition def = DefinitionLoader.Item(itemId);
            bool stackable = def != null && def.Stackable;

            int added = 0;

            if (stackable)
            {
                // Top up an existing stack first.
                for (int i = 0; i < _slots.Length && amount > 0; i++)
                {
                    if (_slots[i].Id != itemId) continue;
                    int space = int.MaxValue - _slots[i].Amount;
                    int take = Math.Min(space, amount);
                    _slots[i].Amount += take;
                    added += take;
                    amount -= take;
                }
            }

            for (int i = 0; i < _slots.Length && amount > 0; i++)
            {
                if (!_slots[i].IsEmpty) continue;

                int take = stackable ? amount : 1;
                _slots[i] = new ItemStack(itemId, take);
                added += take;
                amount -= take;
            }

            return added;
        }

        /// <summary>
        /// Removes items, taking from stacks as needed.
        /// </summary>
        /// <returns>How many were actually removed.</returns>
        public int Remove(int itemId, int amount)
        {
            if (itemId < 0 || amount <= 0) return 0;

            int removed = 0;
            for (int i = 0; i < _slots.Length && amount > 0; i++)
            {
                if (_slots[i].Id != itemId) continue;

                int take = Math.Min(_slots[i].Amount, amount);
                _slots[i].Amount -= take;
                amount -= take;
                removed += take;

                if (_slots[i].Amount <= 0) _slots[i] = ItemStack.Empty;
            }
            return removed;
        }

        /// <summary>Removes exactly the given amount from one slot, or nothing at all.</summary>
        public bool RemoveFromSlot(int slot, int amount)
        {
            if (slot < 0 || slot >= _slots.Length) return false;
            if (_slots[slot].IsEmpty || _slots[slot].Amount < amount) return false;

            _slots[slot].Amount -= amount;
            if (_slots[slot].Amount <= 0) _slots[slot] = ItemStack.Empty;
            return true;
        }

        public bool RemoveAt(int slot, int amount)
        {
            return RemoveFromSlot(slot, amount);
        }

        /// <summary>Sets a slot outright, used by loading and by admin commands.</summary>
        public void Set(int slot, int itemId, int amount)
        {
            if (slot < 0 || slot >= _slots.Length) return;
            _slots[slot] = itemId < 0 || amount <= 0 ? ItemStack.Empty : new ItemStack(itemId, amount);
        }

        /// <summary>Swaps two slots, as the client's drag-and-drop does.</summary>
        public bool Swap(int from, int to)
        {
            if (from < 0 || from >= _slots.Length) return false;
            if (to < 0 || to >= _slots.Length) return false;

            ItemStack tmp = _slots[from];
            _slots[from] = _slots[to];
            _slots[to] = tmp;
            return true;
        }

        /// <summary>Moves items between two slots, merging when the definitions stack.</summary>
        public bool Insert(int from, int to)
        {
            if (from < 0 || from >= _slots.Length) return false;
            if (to < 0 || to >= _slots.Length) return false;
            if (from == to) return false;

            ItemStack source = _slots[from];
            if (source.IsEmpty) return false;

            ItemStack target = _slots[to];
            if (target.IsEmpty)
            {
                _slots[to] = source;
                _slots[from] = ItemStack.Empty;
                return true;
            }

            if (target.Id == source.Id)
            {
                ItemDefinition def = DefinitionLoader.Item(source.Id);
                if (def == null || !def.Stackable) return false;

                long total = (long)target.Amount + source.Amount;
                _slots[to] = new ItemStack(target.Id, (int)Math.Min(total, int.MaxValue));
                _slots[from] = ItemStack.Empty;
                return true;
            }

            // Different items: swap.
            _slots[from] = target;
            _slots[to] = source;
            return true;
        }

        /// <summary>Empties every slot.</summary>
        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i] = ItemStack.Empty;
        }

        /// <summary>All non-empty slots, for persistence.</summary>
        public IEnumerable<KeyValuePair<int, ItemStack>> Occupied()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (!_slots[i].IsEmpty) yield return new KeyValuePair<int, ItemStack>(i, _slots[i]);
            }
        }
    }
}
