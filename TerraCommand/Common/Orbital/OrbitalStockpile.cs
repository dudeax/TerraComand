using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Net;

namespace TerraCommand.Common.Orbital
{
	public readonly record struct StockKey(int Type, int Prefix);

	/// <summary>
	/// The shared orbital inventory. One per world, shared by every player. The server (or singleplayer)
	/// is authoritative; clients hold a mirror updated through StockFull/StockDelta packets.
	/// Items are stored by (type, prefix). Per-instance mod data on items is not preserved.
	/// </summary>
	public class OrbitalStockpile : ModSystem
	{
		static readonly Dictionary<StockKey, long> stock = new();

		/// <summary>Bumped on every change so UIs can cheaply detect when to refresh.</summary>
		public static int Version { get; private set; }
		public static IReadOnlyDictionary<StockKey, long> Stock => stock;

		public override void ClearWorld()
		{
			stock.Clear();
			Version++;
		}

		public static long Count(StockKey key) => stock.TryGetValue(key, out long n) ? n : 0;

		public static long CountType(int type)
		{
			long n = 0;
			foreach (var (k, v) in stock)
				if (k.Type == type)
					n += v;
			return n;
		}

		static void ApplyDelta(StockKey key, long delta)
		{
			long n = Count(key) + delta;
			if (n <= 0)
				stock.Remove(key);
			else
				stock[key] = n;
			Version++;
		}

		// ------------------------------------------------------------ authority API (server / singleplayer)

		public static void Change(StockKey key, long delta)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient || delta == 0 || key.Type <= ItemID.None)
				return;
			ApplyDelta(key, delta);
			if (Main.netMode == NetmodeID.Server) {
				ModPacket p = PacketHandler.Create(PacketType.StockDelta);
				p.Write(key.Type); p.Write(key.Prefix); p.Write(delta);
				p.Send();
			}
		}

		public static void Add(Item item)
		{
			if (item == null || item.IsAir)
				return;
			Change(new StockKey(item.type, item.prefix), item.stack);
		}

		/// <summary>Takes <paramref name="amount"/> of any of the given types (any prefix, unprefixed first).</summary>
		public static bool TryTake(IEnumerable<int> acceptedTypes, long amount)
		{
			var types = acceptedTypes as ISet<int> ?? acceptedTypes.ToHashSet();
			var candidates = stock.Where(kv => types.Contains(kv.Key.Type)).OrderBy(kv => kv.Key.Prefix).ToList();
			if (candidates.Sum(kv => kv.Value) < amount)
				return false;
			foreach (var (k, v) in candidates) {
				if (amount <= 0)
					break;
				long take = Math.Min(v, amount);
				Change(k, -take);
				amount -= take;
			}
			return true;
		}

		public static bool TryTake(StockKey key, long amount)
		{
			if (Count(key) < amount)
				return false;
			Change(key, -amount);
			return true;
		}

		// ------------------------------------------------------------ persistence

		public override void SaveWorldData(TagCompound tag)
		{
			var list = new List<TagCompound>();
			foreach (var (k, v) in stock) {
				var item = new Item(k.Type);
				item.prefix = k.Prefix;
				list.Add(new TagCompound { ["item"] = ItemIO.Save(item), ["count"] = v });
			}
			tag["stock"] = list;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			stock.Clear();
			foreach (var t in tag.GetList<TagCompound>("stock")) {
				Item item = ItemIO.Load(t.GetCompound("item"));
				long n = t.GetLong("count");
				if (!item.IsAir && n > 0)
					ApplyDelta(new StockKey(item.type, item.prefix), n);
			}
			Version++;
		}

		// ------------------------------------------------------------ net

		internal static void SendFull(int toClient)
		{
			ModPacket p = PacketHandler.Create(PacketType.StockFull);
			p.Write(stock.Count);
			foreach (var (k, v) in stock) {
				p.Write(k.Type); p.Write(k.Prefix); p.Write(v);
			}
			p.Send(toClient);
		}

		internal static void ReceiveFull(BinaryReader r)
		{
			stock.Clear();
			int n = r.ReadInt32();
			for (int i = 0; i < n; i++)
				stock[new StockKey(r.ReadInt32(), r.ReadInt32())] = r.ReadInt64();
			Version++;
		}

		internal static void ReceiveDelta(BinaryReader r)
		{
			var key = new StockKey(r.ReadInt32(), r.ReadInt32());
			long delta = r.ReadInt64();
			if (Main.netMode == NetmodeID.MultiplayerClient)
				ApplyDelta(key, delta);
		}
	}
}
