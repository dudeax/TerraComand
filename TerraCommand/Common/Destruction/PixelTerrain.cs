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
			/// <summary>Built up from settled debris (authority only): digging it yields items in proportion to how full it is.</summary>
			public bool Loose;
		}

		static readonly Dictionary<Point16, Damage> damage = new();

		public static int DamagedTileCount => damage.Count;

		public static bool TryGet(int i, int j, out Damage d) => damage.TryGetValue(new Point16(i, j), out d);

		/// <summary>Is the terrain pixel at world position (x, y) solid? Per art pixel (2x2): tiles plus their carved masks.</summary>
		public static bool IsPixelSolid(float x, float y)
		{
			int i = (int)Math.Floor(x / 16f), j = (int)Math.Floor(y / 16f);
			if (!WorldGen.InWorld(i, j))
				return true;
			Tile t = Main.tile[i, j];
			if (!t.HasTile || t.IsActuated || !Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType])
				return false;
			int col = Math.Clamp((int)((x - i * 16) / SubSize), 0, Sub - 1), row = Math.Clamp((int)((y - j * 16) / SubSize), 0, Sub - 1);
			if (t.IsHalfBlock && row < Sub / 2)
				return false;
			return !damage.TryGetValue(new Point16(i, j), out Damage d) || d.Type != t.TileType || (d.Mask & (1UL << (row * Sub + col))) == 0;
		}
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

					SquareOff(i, j, authority);
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
					// the removed material flies off as loose grains (CC)
					bool blast = (flags & CarveFlags.Explosion) != 0, collected = (flags & CarveFlags.DropItems) != 0;
					Debris.FromPixels(i, j, d.Mask & ~before, tile, c, blast ? 4.5f : 2f, settles: !collected);

					if (!Main.dedServ && (flags & CarveFlags.NoDust) == 0) {
						int dusts = Math.Min(1 + removedHere / 6, 4);
						for (int k = 0; k < dusts; k++)
							WorldGen.KillTile_MakeTileDust(i, j, tile);
					}

					if (BitOperations.PopCount(d.Mask) >= threshold) {
						Debris.FromPixels(i, j, ~d.Mask, tile, c, 1f, settles: (flags & CarveFlags.DropItems) == 0); // what's left crumbles and falls
						damage.Remove(key);
						if (authority)
							Crumble(i, j, (flags & CarveFlags.DropItems) != 0 && LooseDrop(d, before));
					}
					else {
						damage[key] = d;
					}
				}
			}
			return removedTotal;
		}

		/// <summary>
		/// Carved tiles are always plain full blocks with a pixel mask, so collision and drawing only deal with
		/// one shape. A sloped / half block being carved is squared off first (authority, synced to clients).
		/// </summary>
		static void SquareOff(int i, int j, bool authority)
		{
			Tile t = Main.tile[i, j];
			if (!authority || (t.Slope == SlopeType.Solid && !t.IsHalfBlock))
				return;
			t.Slope = SlopeType.Solid;
			t.IsHalfBlock = false;
			WorldGen.SquareTileFrame(i, j);
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendTileSquare(-1, i, j, 1);
		}

		static void Crumble(int i, int j, bool drop)
		{
			WorldGen.KillTile(i, j, fail: false, effectOnly: false, noItem: !drop);
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendData(MessageID.TileManipulation, -1, -1, null, drop ? 0 : 4, i, j);
		}

		/// <summary>
		/// Removes every art pixel whose centre lies inside a world-pixel rectangle (authority only, not synced:
		/// precise shapes for tests and scripted terrain).
		/// </summary>
		public static void CarveRect(Rectangle r)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
				return;
			int threshold = (int)Math.Ceiling(Sub * Sub * TerraCommandConfig.Instance.CrumbleThreshold);
			for (int i = r.Left / 16; i <= (r.Right - 1) / 16; i++) {
				for (int j = r.Top / 16; j <= (r.Bottom - 1) / 16; j++) {
					if (!TerrainRules.IsCarvable(i, j))
						continue;
					SquareOff(i, j, authority: true);
					var key = new Point16(i, j);
					GetDamage(key, Main.tile[i, j].TileType, out Damage d);
					for (int b = 0; b < Sub * Sub; b++) {
						int cx = i * 16 + (b % Sub) * SubSize + 1, cy = j * 16 + (b / Sub) * SubSize + 1;
						if (cx >= r.Left && cx < r.Right && cy >= r.Top && cy < r.Bottom)
							d.Mask |= 1UL << b;
					}
					if (BitOperations.PopCount(d.Mask) >= threshold) {
						damage.Remove(key);
						Crumble(i, j, drop: false);
					}
					else if (d.Mask != 0)
						damage[key] = d;
				}
			}
		}

		// ---------------------------------------------------------------- mining

		/// <summary>
		/// One pickaxe hit. Mirrors vanilla pacing: a tile has 100 HP and a hit deals pick power (doubled on
		/// soft ground), so each hit removes that percentage of the tile's pixels - the ones nearest the aim
		/// point, which carves a round bite where the player is pointing.
		/// </summary>
		public static void RequestMine(int i, int j, Vector2 aim, int pickPower)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket p = PacketHandler.Create(PacketType.MineRequest);
				p.Write(i); p.Write(j); p.Write(aim.X); p.Write(aim.Y); p.Write((short)pickPower);
				p.Send();
				return;
			}
			ApplyMine(i, j, aim, pickPower);
		}

		internal static void ReceiveMine(BinaryReader r, int from)
		{
			int i = r.ReadInt32(), j = r.ReadInt32();
			var aim = new Vector2(r.ReadSingle(), r.ReadSingle());
			int power = r.ReadInt16();
			if (Main.netMode == NetmodeID.Server && Vector2.Distance(Main.player[from].Center, new Vector2(i * 16, j * 16)) < 1200f)
				ApplyMine(i, j, aim, power);
		}

		static void ApplyMine(int i, int j, Vector2 aim, int pickPower)
		{
			if (!TerrainRules.IsCarvable(i, j) || !WorldGen.CanKillTile(i, j))
				return;
			Tile tile = Main.tile[i, j];
			int type = tile.TileType;
			if (pickPower < TerrainRules.MinPick(type))
				return;

			SquareOff(i, j, authority: true);
			int hitDamage = TerrainRules.Softness(type) >= 1f ? pickPower * 2 : pickPower;
			int pixels = Math.Clamp((int)Math.Ceiling(Sub * Sub * hitDamage / 100f), 1, Sub * Sub);

			var key = new Point16(i, j);
			GetDamage(key, type, out Damage d);
			Span<(float dist, int bit)> free = stackalloc (float, int)[Sub * Sub];
			int n = 0;
			for (int b = 0; b < Sub * Sub; b++) {
				if ((d.Mask & (1UL << b)) != 0)
					continue;
				var p = new Vector2(i * 16 + (b % Sub) * SubSize + 1, j * 16 + (b / Sub) * SubSize + 1);
				free[n++] = (Vector2.DistanceSquared(p, aim), b);
			}
			var slice = free[..n];
			slice.Sort((x, y) => x.dist != y.dist ? x.dist.CompareTo(y.dist) : x.bit.CompareTo(y.bit));
			ulong beforeMine = d.Mask;
			for (int k = 0; k < Math.Min(pixels, n); k++)
				d.Mask |= 1UL << slice[k].bit;
			// dug material sprays out of the bite, back toward the digger
			Vector2 tileCenter = new(i * 16 + 8, j * 16 + 8);
			Debris.FromPixels(i, j, d.Mask & ~beforeMine, tile, tileCenter + (tileCenter - aim) * 2f, 1.8f, settles: false); // collected as items

			if (!Main.dedServ)
				for (int k = 0; k < 3; k++)
					WorldGen.KillTile_MakeTileDust(i, j, tile);

			int threshold = (int)Math.Ceiling(Sub * Sub * TerraCommandConfig.Instance.CrumbleThreshold);
			if (BitOperations.PopCount(d.Mask) >= threshold) {
				damage.Remove(key);
				Crumble(i, j, drop: LooseDrop(d, beforeMine));
				return;
			}
			damage[key] = d;
			SendMask(i, j, d, deposit: false);
		}

		/// <summary>A loose tile (built from debris) yields its item only in proportion to how full it was.</summary>
		static bool LooseDrop(Damage d, ulong maskBefore) =>
			!d.Loose || Main.rand.NextFloat() < (Sub * Sub - BitOperations.PopCount(maskBefore)) / (float)(Sub * Sub);

		static void SendMask(int i, int j, Damage d, bool deposit)
		{
			if (Main.netMode != NetmodeID.Server)
				return;
			ModPacket p = PacketHandler.Create(PacketType.MaskSet);
			p.Write(i); p.Write(j); p.Write(d.Mask); p.Write(d.Type); p.Write(deposit);
			p.Send();
		}

		// ---------------------------------------------------------------- settling: loose material becomes terrain

		static readonly HashSet<Point16> dirty = new();
		static readonly HashSet<Point16> newTiles = new();
		public static int Deposits;

		/// <summary>
		/// Authority: one art pixel of <paramref name="material"/> settles into the terrain at world position (x, y) (the
		/// 2x2 cell containing it). It refills a carved tile's pixel, or starts a new loose tile holding just that pixel.
		/// Returns false if it can't (solid already, liquid, a body in the way...).
		/// </summary>
		public static bool Deposit(float x, float y, ushort material)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
				return false;
			int i = (int)Math.Floor(x / 16f), j = (int)Math.Floor(y / 16f);
			if (!WorldGen.InWorld(i, j, 10))
				return false;
			int col = Math.Clamp((int)((x - i * 16) / SubSize), 0, Sub - 1), row = Math.Clamp((int)((y - j * 16) / SubSize), 0, Sub - 1);
			ulong bit = 1UL << (row * Sub + col);
			var key = new Point16(i, j);
			Tile t = Main.tile[i, j];
			if (t.HasTile) {
				if (!damage.TryGetValue(key, out Damage d) || d.Type != t.TileType || (d.Mask & bit) == 0)
					return false;   // whole tile, or that pixel's already there
				d.Mask &= ~bit;
				if (d.Mask == 0)
					damage.Remove(key);
				else
					damage[key] = d;
				dirty.Add(key);
				Deposits++;
				return true;
			}
			if (t.LiquidAmount > 0 || OccupiedByBody(new Vector2(col * SubSize + i * 16, row * SubSize + j * 16)))
				return false;
			// a new loose tile holding just this pixel
			t.ResetToType(material);
			t.Slope = SlopeType.Solid;
			t.IsHalfBlock = false;
			damage[key] = new Damage { Type = material, Mask = ~bit, Loose = true };
			WorldGen.SquareTileFrame(i, j, resetFrame: true);
			newTiles.Add(key);
			dirty.Add(key);
			Deposits++;
			return true;
		}

		static bool OccupiedByBody(Vector2 cell)
		{
			var r = new Rectangle((int)cell.X, (int)cell.Y, SubSize, SubSize);
			foreach (Player p in Main.ActivePlayers)
				if (!p.dead && p.Hitbox.Intersects(r))
					return true;
			foreach (NPC n in Main.ActiveNPCs)
				if (!n.noTileCollide && n.Hitbox.Intersects(r))
					return true;
			return false;
		}

		/// <summary>Sends this tick's settled pixels (new loose tiles first) to the clients.</summary>
		public override void PostUpdateWorld()
		{
			if (dirty.Count == 0)
				return;
			if (Main.netMode == NetmodeID.Server) {
				foreach (Point16 k in newTiles)
					NetMessage.SendTileSquare(-1, k.X, k.Y, 1);
				foreach (Point16 k in dirty) {
					damage.TryGetValue(k, out Damage d);
					if (!damage.ContainsKey(k))
						d = new Damage { Type = Main.tile[k.X, k.Y].TileType, Mask = 0 };
					SendMask(k.X, k.Y, d, deposit: true);
				}
			}
			newTiles.Clear();
			dirty.Clear();
		}

		static void GetDamage(Point16 key, int type, out Damage d)
		{
			damage.TryGetValue(key, out d);
			if (d.Type != type)
				d = new Damage { Type = (ushort)type };
		}

		internal static void ReceiveMaskSet(BinaryReader r)
		{
			int i = r.ReadInt32(), j = r.ReadInt32();
			var d = new Damage { Mask = r.ReadUInt64(), Type = r.ReadUInt16() };
			bool deposit = r.ReadBoolean();
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;
			if (d.Mask == 0) {
				damage.Remove(new Point16(i, j));   // filled back up: a whole tile again
				return;
			}
			if (deposit) {
				damage[new Point16(i, j)] = d;      // settled material: no debris
				return;
			}
			if (WorldGen.InWorld(i, j) && Main.tile[i, j].HasTile && !Main.dedServ) {
				WorldGen.KillTile_MakeTileDust(i, j, Main.tile[i, j]);
				damage.TryGetValue(new Point16(i, j), out Damage old);
				ulong newly = d.Mask & ~(old.Type == d.Type ? old.Mask : 0UL);
				Debris.FromPixels(i, j, newly, Main.tile[i, j], new Vector2(i * 16 + 8, j * 16 + 8), 1.8f, settles: false);
			}
			damage[new Point16(i, j)] = d;
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
				xs.Add(k.X); ys.Add(k.Y); masks.Add((long)d.Mask); types.Add(d.Type | (d.Loose ? 1 << 16 : 0));
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
				damage[new Point16(xs[n], ys[n])] = new Damage { Mask = (ulong)masks[n], Type = (ushort)(types[n] & 0xFFFF), Loose = (types[n] & (1 << 16)) != 0 };
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
