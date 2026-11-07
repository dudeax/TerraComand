using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Net;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace TerraCommand.Common.Destruction
{
	[Flags]
	public enum CarveFlags : byte
	{
		None = 0,
		DropItems = 1,     // fully carved tiles drop their item (mining)
		Explosion = 2,     // apply explosion gating rules
		NoDust = 4,
	}

	/// <summary>
	/// Per-pixel terrain damage. Each tile is split into 8x8 "art pixels" (2x2 world pixels, the native
	/// resolution of Terraria's sprites), so a tile's damage fits in one 64-bit mask (bit set = removed).
	/// The authority (server or singleplayer) decides when a tile crumbles and kills it through vanilla
	/// so drops, framing and tile sync keep working unchanged.
	/// </summary>
	public class PixelTerrain : ModSystem
	{
		public const int Sub = 8;
		public const int SubSize = 16 / Sub;

		public struct Damage
		{
			public ulong Mask;
			public ushort Type;
		}

		static readonly Dictionary<Point16, Damage> damage = new();

		public static int DamagedTileCount => damage.Count;

		public static bool TryGet(int i, int j, out Damage d) => damage.TryGetValue(new Point16(i, j), out d);
		public static void Clear(int i, int j) => damage.Remove(new Point16(i, j));

		public override void ClearWorld() => damage.Clear();

		// ---------------------------------------------------------------- carving

		/// <summary>Entry point for gameplay code on any side. Clients forward to the server.</summary>
		public static void RequestCarve(Vector2 center, float radius, int power, CarveFlags flags)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket p = PacketHandler.Create(PacketType.CarveRequest);
				WriteCarve(p, center, radius, power, flags);
				p.Send();
				return;
			}
			Apply(center, radius, power, flags, authority: true);
			if (Main.netMode == NetmodeID.Server) {
				ModPacket p = PacketHandler.Create(PacketType.CarveApply);
				WriteCarve(p, center, radius, power, flags);
				p.Send();
			}
		}

		static void WriteCarve(BinaryWriter w, Vector2 c, float r, int power, CarveFlags flags)
		{
			w.Write(c.X); w.Write(c.Y); w.Write(r); w.Write((short)power); w.Write((byte)flags);
		}

		internal static void ReceiveCarve(BinaryReader r, PacketType type, int from)
		{
			var c = new Vector2(r.ReadSingle(), r.ReadSingle());
			float radius = r.ReadSingle();
			int power = r.ReadInt16();
			var flags = (CarveFlags)r.ReadByte();
			if (type == PacketType.CarveRequest && Main.netMode == NetmodeID.Server) {
				// Basic sanity: clients may only carve near themselves.
				Player p = Main.player[from];
				if (radius > 200f || Vector2.Distance(p.Center, c) > 2400f)
					return;
				RequestCarve(c, radius, power, flags);
			}
			else if (type == PacketType.CarveApply && Main.netMode == NetmodeID.MultiplayerClient) {
				Apply(c, radius, power, flags, authority: false);
			}
		}

		/// <summary>Removes all art pixels within radius. Returns the number of art pixels removed.</summary>
		static int Apply(Vector2 c, float radius, int power, CarveFlags flags, bool authority)
		{
			int removedTotal = 0;
			int threshold = (int)Math.Ceiling(Sub * Sub * TerraCommandConfig.Instance.CrumbleThreshold);
			int x0 = (int)((c.X - radius) / 16f), x1 = (int)((c.X + radius) / 16f);
			int y0 = (int)((c.Y - radius) / 16f), y1 = (int)((c.Y + radius) / 16f);

			for (int i = x0; i <= x1; i++) {
				for (int j = y0; j <= y1; j++) {
					if (!TerrainRules.IsCarvable(i, j))
						continue;
					Tile tile = Main.tile[i, j];
					int type = tile.TileType;
					if (power < TerrainRules.MinPick(type))
						continue;
					if ((flags & CarveFlags.Explosion) != 0 && !TerrainRules.ExplosionCanAffect(type))
						continue;
					if (authority && !WorldGen.CanKillTile(i, j))
						continue;

					float r = radius * TerrainRules.Softness(type);
					float r2 = r * r;
					var key = new Point16(i, j);
					damage.TryGetValue(key, out Damage d);
					if (d.Type != type)
						d = new Damage { Type = (ushort)type };

					ulong before = d.Mask;
					for (int row = 0; row < Sub; row++) {
						float py = j * 16 + row * SubSize + SubSize * 0.5f - c.Y;
						for (int col = 0; col < Sub; col++) {
							float px = i * 16 + col * SubSize + SubSize * 0.5f - c.X;
							if (px * px + py * py <= r2)
								d.Mask |= 1UL << (row * Sub + col);
						}
					}
					if (d.Mask == before)
						continue;

					int removedHere = BitOperations.PopCount(d.Mask) - BitOperations.PopCount(before);
					removedTotal += removedHere;

					if (!Main.dedServ && (flags & CarveFlags.NoDust) == 0) {
						int dusts = Math.Min(1 + removedHere / 6, 4);
						for (int k = 0; k < dusts; k++)
							WorldGen.KillTile_MakeTileDust(i, j, tile);
					}

					if (BitOperations.PopCount(d.Mask) >= threshold) {
						damage.Remove(key);
						if (authority)
							Crumble(i, j, (flags & CarveFlags.DropItems) != 0);
					}
					else {
						damage[key] = d;
					}
				}
			}
			return removedTotal;
		}

		static void Crumble(int i, int j, bool drop)
		{
			WorldGen.KillTile(i, j, fail: false, effectOnly: false, noItem: !drop);
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendData(MessageID.TileManipulation, -1, -1, null, drop ? 0 : 4, i, j);
		}

		/// <summary>Finds the remaining (uncarved) art pixel of a tile nearest to a world position.</summary>
		public static Vector2 NearestSolidPixel(int i, int j, Vector2 target)
		{
			TryGet(i, j, out Damage d);
			if (d.Type != Main.tile[i, j].TileType)
				d.Mask = 0;
			Vector2 best = new(i * 16 + 8, j * 16 + 8);
			float bestDist = float.MaxValue;
			for (int b = 0; b < Sub * Sub; b++) {
				if ((d.Mask & (1UL << b)) != 0)
					continue;
				var p = new Vector2(i * 16 + (b % Sub) * SubSize + 1, j * 16 + (b / Sub) * SubSize + 1);
				float dist = Vector2.DistanceSquared(p, target);
				if (dist < bestDist) {
					bestDist = dist;
					best = p;
				}
			}
			return best;
		}

		// ---------------------------------------------------------------- persistence

		public override void SaveWorldData(TagCompound tag)
		{
			var xs = new List<int>(damage.Count);
			var ys = new List<int>(damage.Count);
			var masks = new List<long>(damage.Count);
			var types = new List<int>(damage.Count);
			foreach (var (k, d) in damage) {
				xs.Add(k.X); ys.Add(k.Y); masks.Add((long)d.Mask); types.Add(d.Type);
			}
			tag["x"] = xs; tag["y"] = ys; tag["mask"] = masks; tag["type"] = types;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			damage.Clear();
			var xs = tag.GetList<int>("x");
			var ys = tag.GetList<int>("y");
			var masks = tag.GetList<long>("mask");
			var types = tag.GetList<int>("type");
			for (int n = 0; n < xs.Count && n < ys.Count && n < masks.Count && n < types.Count; n++)
				damage[new Point16(xs[n], ys[n])] = new Damage { Mask = (ulong)masks[n], Type = (ushort)types[n] };
		}

		internal static void SendFull(int toClient)
		{
			// Chunk to keep packets well under the 65k limit (each entry = 4+8+2 bytes).
			var all = new List<KeyValuePair<Point16, Damage>>(damage);
			const int chunk = 3000;
			for (int start = 0; start < all.Count || start == 0; start += chunk) {
				int count = Math.Min(chunk, all.Count - start);
				ModPacket p = PacketHandler.Create(PacketType.TerrainFull);
				p.Write(start == 0);
				p.Write(count);
				for (int n = start; n < start + count; n++) {
					p.Write(all[n].Key.X); p.Write(all[n].Key.Y);
					p.Write(all[n].Value.Mask); p.Write(all[n].Value.Type);
				}
				p.Send(toClient);
				if (all.Count == 0)
					break;
			}
		}

		internal static void ReceiveFull(BinaryReader r)
		{
			if (r.ReadBoolean())
				damage.Clear();
			int count = r.ReadInt32();
			for (int n = 0; n < count; n++) {
				var k = new Point16(r.ReadInt16(), r.ReadInt16());
				damage[k] = new Damage { Mask = r.ReadUInt64(), Type = r.ReadUInt16() };
			}
		}
	}
}
