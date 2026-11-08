using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Map;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>
	/// Loose material, as in Cortex Command: the pixels a blast or a digger removes fly off as grains of that material, bounce,
	/// and settle - sliding down until they rest, like sand - into the terrain as real pixels (refilling carved tiles or
	/// piling up as loose tiles, see <see cref="PixelTerrain.Deposit"/>). The authority (server / singleplayer) simulates the
	/// grains that become terrain; multiplayer clients show their own copies, which vanish as they settle (the server's
	/// settled pixels arrive over the network).
	/// </summary>
	public class Debris : ModSystem
	{
		const int Max = 4000, PerTileCap = 10;
		const float Gravity = 0.25f;

		struct Grain
		{
			public Vector2 Pos, Vel;
			public Color Color;
			public ushort Material;
			public bool Settles;      // becomes terrain when it comes to rest (blasted material); dug material is collected as items
			public int Life;          // ticks left once settled (grains that couldn't settle into the terrain)
			public bool Settled;
		}

		static readonly List<Grain> grains = new();
		public static int Count => grains.Count;

		public override void ClearWorld() => grains.Clear();

		/// <summary>
		/// The pixels in <paramref name="removed"/> (bit set = just removed) of tile (i, j) fly off, away from
		/// <paramref name="from"/> at about <paramref name="speed"/> px/tick.
		/// </summary>
		public static void FromPixels(int i, int j, ulong removed, Tile tile, Vector2 from, float speed, bool settles = true)
		{
			if (removed == 0)
				return;
			Color c = new(120, 100, 80);
			if (!Main.dedServ) {
				MapTile mt = MapHelper.CreateMapTile(i, j, 255);
				c = MapHelper.GetMapTileXnaColor(ref mt);
			}
			ushort material = LooseMaterial(tile.TileType);
			if (c.A == 0)
				c = new Color(120, 100, 80);
			int n = 0;
			for (int b = 0; b < 64 && n < PerTileCap; b++) {
				if ((removed & (1UL << b)) == 0)
					continue;
				// not every removed pixel becomes a grain (CC throws a fraction of the material)
				if (Main.rand.Next(3) != 0)
					continue;
				var p = new Vector2(i * 16 + (b % 8) * 2 + 1, j * 16 + (b / 8) * 2 + 1);
				Vector2 dir = (p - from).SafeNormalize(-Vector2.UnitY);
				Vector2 v = dir * speed * Main.rand.NextFloat(0.4f, 1.2f) + Main.rand.NextVector2Circular(0.8f, 0.8f) + new Vector2(0f, -speed * 0.3f);
				Add(p, v, Vary(c), material, settles);
				n++;
			}
		}

		static Color Vary(Color c)
		{
			float k = Main.rand.NextFloat(0.8f, 1.1f);
			return new Color((int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
		}

		static void Add(Vector2 p, Vector2 v, Color c, ushort material, bool settles)
		{
			if (!settles && Main.dedServ)
				return;   // purely cosmetic
			if (grains.Count >= Max)
				grains.RemoveAt(0);
			grains.Add(new Grain { Pos = p, Vel = v, Color = c, Material = material, Settles = settles, Life = 60 * Main.rand.Next(20, 35) });
		}

		/// <summary>What a material settles as: grass gives its soil, everything else itself.</summary>
		public static ushort LooseMaterial(int type) => type switch {
			TileID.JungleGrass or TileID.MushroomGrass or TileID.AshGrass => type == TileID.AshGrass ? TileID.Ash : TileID.Mud,
			_ when TileID.Sets.Grass[type] => TileID.Dirt,
			_ => (ushort)type,
		};

		static bool Solid(Vector2 p) => PixelTerrain.IsPixelSolid(p.X, p.Y);

		/// <summary>Where a grain landing at <paramref name="p"/> comes to rest: it slides down and off edges like sand.</summary>
		static Vector2 SlideToRest(Vector2 p)
		{
			Vector2 cell = new(MathF.Floor(p.X / 2f) * 2f + 1f, MathF.Floor(p.Y / 2f) * 2f + 1f);
			for (int k = 0; k < 12; k++) {
				if (!Solid(cell + new Vector2(0f, 2f))) {
					cell.Y += 2f;
					continue;
				}
				int dir = Main.rand.NextBool() ? 1 : -1;
				bool moved = false;
				foreach (int d in new[] { dir, -dir }) {
					if (!Solid(cell + new Vector2(2f * d, 0f)) && !Solid(cell + new Vector2(2f * d, 2f))) {
						cell += new Vector2(2f * d, 2f);
						moved = true;
						break;
					}
				}
				if (!moved)
					break;
			}
			return cell;
		}

		public override void PostUpdateDusts()
		{
			bool authority = Main.netMode != NetmodeID.MultiplayerClient;
			for (int k = grains.Count - 1; k >= 0; k--) {
				Grain g = grains[k];
				if (g.Settled) {
					// lying on the ground; if that ground is blown away, it falls again
					if (--g.Life <= 0) {
						grains.RemoveAt(k);
						continue;
					}
					if (Main.GameUpdateCount % 20 == (ulong)(k % 20) && !Solid(g.Pos + new Vector2(0f, 2f)))
						g.Settled = false;
					grains[k] = g;
					continue;
				}
				g.Vel.Y = Math.Min(g.Vel.Y + Gravity, 10f);
				// move in sub-steps so grains don't tunnel through thin pixel walls
				int steps = (int)Math.Ceiling(g.Vel.Length() / 1.5f);
				Vector2 step = g.Vel / Math.Max(1, steps);
				for (int s = 0; s < steps; s++) {
					Vector2 next = g.Pos + step;
					if (!Solid(next)) {
						g.Pos = next;
						continue;
					}
					// hit: bounce off whichever axis is blocked, losing most of the speed
					bool blockX = Solid(new Vector2(next.X, g.Pos.Y)), blockY = Solid(new Vector2(g.Pos.X, next.Y));
					if (blockY || !blockX) {
						g.Vel.Y = -g.Vel.Y * 0.25f;
						g.Vel.X *= 0.5f;
					}
					if (blockX)
						g.Vel.X = -g.Vel.X * 0.3f;
					break;
				}
				// at rest on something: slide to where it can stay, and become part of the terrain there
				if (Math.Abs(g.Vel.Y) < 0.6f && Math.Abs(g.Vel.X) < 0.4f && Solid(g.Pos + new Vector2(0f, 1.5f))) {
					if (!authority && g.Settles) {
						grains.RemoveAt(k);   // the server's settled pixel takes its place
						continue;
					}
					Vector2 rest = SlideToRest(g.Pos);
					if (g.Settles && !Solid(rest) && PixelTerrain.Deposit(rest.X, rest.Y, g.Material)) {
						grains.RemoveAt(k);
						continue;
					}
					g.Settled = true;   // couldn't join the terrain (liquid, a body in the way): just lie there a while
					g.Vel = Vector2.Zero;
					g.Pos = g.Pos.Floor();
				}
				if (Collision.WetCollision(g.Pos, 1, 1))
					g.Vel *= 0.8f;
				if (g.Pos.Y > Main.maxTilesY * 16)
					g.Life = 0;
				grains[k] = g;
			}
		}

		public override void PostDrawTiles()
		{
			if (grains.Count == 0 || Main.dedServ)
				return;
			SpriteBatch sb = Main.spriteBatch;
			sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None,
				Main.Rasterizer, null, Main.GameViewMatrix.TransformationMatrix);
			Texture2D px = TextureAssets.MagicPixel.Value;
			var one = new Rectangle(0, 0, 1, 1);
			var view = new Rectangle((int)Main.screenPosition.X - 16, (int)Main.screenPosition.Y - 16, Main.screenWidth + 32, Main.screenHeight + 32);
			foreach (Grain g in grains) {
				if (!view.Contains(g.Pos.ToPoint()))
					continue;
				float fade = g.Settled ? Math.Min(1f, g.Life / 120f) : 1f;
				Color c = g.Color.MultiplyRGB(Lighting.GetColor(g.Pos.ToTileCoordinates())) * fade;
				sb.Draw(px, (g.Pos - Main.screenPosition).Floor() - Vector2.One, one, c, 0f, Vector2.Zero, 2f, SpriteEffects.None, 0f);
			}
			sb.End();
		}
	}
}
