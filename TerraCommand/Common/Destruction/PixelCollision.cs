using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>
	/// Per-pixel collision against carved terrain, for everything that uses Terraria's tile collision
	/// (players, bodies, enemies, items, projectiles).
	///
	/// Approach: for each collision query, partially carved tiles near the entity are temporarily hidden from
	/// vanilla (so slopes, platforms and intact blocks keep vanilla behaviour), then the result is refined
	/// against the carved tiles' remaining pixels. When there is no carved terrain nearby, vanilla runs
	/// untouched (these functions run for every entity every tick).
	/// </summary>
	public class PixelCollision : ModSystem
	{
		/// <summary>Highest pixel bump a walker steps over without jumping (CC actors climb rough terrain).</summary>
		public const int MaxStepUp = 10;

		[ThreadStatic] static Point16[] buffer;
		const int MaxTiles = 96;

		public override void Load()
		{
			On_Collision.TileCollision += OnTileCollision;
			On_Collision.SolidCollision_Vector2_int_int += OnSolidCollision;
			On_Collision.SolidCollision_Vector2_int_int_bool += OnSolidCollisionTop;
			On_Collision.StepUp += OnStepUp;
		}

		// ------------------------------------------------------------ queries

		/// <summary>Collects carved tiles (with a valid mask) overlapping the rectangle. Returns the count.</summary>
		static int CollectCarved(float x, float y, float w, float h, Point16[] into)
		{
			if (PixelTerrain.DamagedTileCount == 0)
				return 0;
			int i0 = Math.Max((int)Math.Floor(x / 16f), 0), i1 = Math.Min((int)Math.Floor((x + w) / 16f), Main.maxTilesX - 1);
			int j0 = Math.Max((int)Math.Floor(y / 16f), 0), j1 = Math.Min((int)Math.Floor((y + h) / 16f), Main.maxTilesY - 1);
			int n = 0;
			for (int i = i0; i <= i1; i++) {
				for (int j = j0; j <= j1; j++) {
					if (n >= into.Length)
						return n;
					if (!PixelTerrain.TryGet(i, j, out var d) || d.Mask == 0)
						continue;
					Tile t = Main.tile[i, j];
					if (t.HasTile && t.TileType == d.Type && !t.IsActuated)
						into[n++] = new Point16(i, j);
				}
			}
			return n;
		}

		/// <summary>True if the box overlaps a remaining (solid) pixel of any of the given carved tiles.</summary>
		static bool OverlapsCarved(float x, float y, float w, float h, Point16[] tiles, int count)
		{
			for (int k = 0; k < count; k++) {
				int tx = tiles[k].X * 16, ty = tiles[k].Y * 16;
				if (x >= tx + 16 || x + w <= tx || y >= ty + 16 || y + h <= ty)
					continue;
				PixelTerrain.TryGet(tiles[k].X, tiles[k].Y, out var d);
				int c0 = Math.Max((int)Math.Floor((x - tx) / 2f), 0), c1 = Math.Min((int)Math.Ceiling((x + w - tx) / 2f) - 1, 7);
				int r0 = Math.Max((int)Math.Floor((y - ty) / 2f), 0), r1 = Math.Min((int)Math.Ceiling((y + h - ty) / 2f) - 1, 7);
				if (c1 < c0 || r1 < r0)
					continue;
				int colMask = ((1 << (c1 + 1)) - 1) & ~((1 << c0) - 1);
				for (int r = r0; r <= r1; r++) {
					int removed = (int)((d.Mask >> (r * 8)) & 0xFF);
					if ((~removed & colMask) != 0)
						return true;
				}
			}
			return false;
		}

		readonly struct Hidden : IDisposable
		{
			readonly Point16[] tiles;
			readonly int count;

			public Hidden(Point16[] tiles, int count)
			{
				this.tiles = tiles;
				this.count = count;
				for (int k = 0; k < count; k++) {
					Tile t = Main.tile[tiles[k].X, tiles[k].Y];
					t.HasTile = false;
				}
			}

			public void Dispose()
			{
				for (int k = 0; k < count; k++) {
					Tile t = Main.tile[tiles[k].X, tiles[k].Y];
					t.HasTile = true;
				}
			}
		}

		static Point16[] Buf => buffer ??= new Point16[MaxTiles];

		/// <summary>Pixel-accurate solidity test of a box against all terrain (vanilla + carved).</summary>
		public static bool BoxSolid(Vector2 pos, int w, int h) => Collision.SolidCollision(pos, w, h);

		// ------------------------------------------------------------ hooks

		static Vector2 OnTileCollision(On_Collision.orig_TileCollision orig, Vector2 pos, Vector2 vel, int w, int h,
			bool fallThrough, bool fall2, int gravDir)
		{
			if (PixelTerrain.DamagedTileCount == 0)
				return orig(pos, vel, w, h, fallThrough, fall2, gravDir);
			float minX = Math.Min(pos.X, pos.X + vel.X) - 2, minY = Math.Min(pos.Y, pos.Y + vel.Y) - 2;
			float maxX = Math.Max(pos.X, pos.X + vel.X) + w + 2, maxY = Math.Max(pos.Y, pos.Y + vel.Y) + h + 2;
			Point16[] tiles = Buf;
			int n = CollectCarved(minX, minY, maxX - minX, maxY - minY, tiles);
			if (n == 0)
				return orig(pos, vel, w, h, fallThrough, fall2, gravDir);

			Vector2 v;
			using (new Hidden(tiles, n))
				v = orig(pos, vel, w, h, fallThrough, fall2, gravDir);

			// Already embedded in carved pixels (terrain changed under us): don't trap the entity.
			if (OverlapsCarved(pos.X, pos.Y, w, h, tiles, n))
				return v;

			// Sweep the whole path, including when the endpoint is beyond a thin remnant.
			// Axis-separated, X then Y, in 1px steps.
			if (v.X != 0f)
				v.X = Sweep(pos.X, v.X, x => OverlapsCarved(x, pos.Y, w, h, tiles, n));
			if (v.Y != 0f) {
				float px = pos.X + v.X;
				v.Y = Sweep(pos.Y, v.Y, y => OverlapsCarved(px, y, w, h, tiles, n));
			}
			return v;
		}

		/// <summary>Largest move along one axis (up to <paramref name="delta"/>) that stays free.</summary>
		static float Sweep(float start, float delta, Func<float, bool> blocked)
		{
			float dir = Math.Sign(delta), len = Math.Abs(delta), moved = 0f;
			for (float d = 1f; ; d += 1f) {
				float step = Math.Min(d, len);
				if (blocked(start + dir * step))
					return dir * moved;
				moved = step;
				if (step >= len)
					return delta;
			}
		}

		static bool OnSolidCollision(On_Collision.orig_SolidCollision_Vector2_int_int orig, Vector2 pos, int w, int h)
		{
			Point16[] tiles = Buf;
			int n = CollectCarved(pos.X, pos.Y, w, h, tiles);
			if (n == 0)
				return orig(pos, w, h);
			using (new Hidden(tiles, n))
				if (orig(pos, w, h))
					return true;
			return OverlapsCarved(pos.X, pos.Y, w, h, tiles, n);
		}

		static bool OnSolidCollisionTop(On_Collision.orig_SolidCollision_Vector2_int_int_bool orig, Vector2 pos, int w, int h, bool acceptTop)
		{
			Point16[] tiles = Buf;
			int n = CollectCarved(pos.X, pos.Y, w, h, tiles);
			if (n == 0)
				return orig(pos, w, h, acceptTop);
			using (new Hidden(tiles, n))
				if (orig(pos, w, h, acceptTop))
					return true;
			return OverlapsCarved(pos.X, pos.Y, w, h, tiles, n);
		}

		/// <summary>Walkers step up small pixel bumps in carved terrain instead of stopping dead.</summary>
		static void OnStepUp(On_Collision.orig_StepUp orig, ref Vector2 pos, ref Vector2 vel, int w, int h, ref float stepSpeed,
			ref float gfxOffY, int gravDir, bool holdsMatching, int specialChecksMode)
		{
			if (PixelTerrain.DamagedTileCount == 0) {
				orig(ref pos, ref vel, w, h, ref stepSpeed, ref gfxOffY, gravDir, holdsMatching, specialChecksMode);
				return;
			}
			// vanilla steps up onto whole 16px blocks: it must not see carved tiles as whole (it would lift the body onto a
			// tile's original top, leaving it hovering over the dug-out part)
			Point16[] tiles = Buf;
			int n = CollectCarved(pos.X - 18, pos.Y - 18, w + 36, h + 36, tiles);
			using (new Hidden(tiles, n))
				orig(ref pos, ref vel, w, h, ref stepSpeed, ref gfxOffY, gravDir, holdsMatching, specialChecksMode);
			if (gravDir != 1 || vel.X == 0f)
				return;
			float nx = pos.X + vel.X;
			n = CollectCarved(nx - 2, pos.Y - MaxStepUp, w + 4, h + MaxStepUp + 4, tiles);
			if (n == 0 || !OverlapsCarved(nx, pos.Y, w, h, tiles, n))
				return;
			// must be standing on something
			if (!Collision.SolidCollision(new Vector2(pos.X, pos.Y + 2), w, h))
				return;
			for (int k = 2; k <= MaxStepUp; k += 2) {
				if (!Collision.SolidCollision(new Vector2(nx, pos.Y - k), w, h)) {
					pos.Y -= k;
					gfxOffY += k;
					stepSpeed = 1.5f;
					return;
				}
			}
		}
	}
}
