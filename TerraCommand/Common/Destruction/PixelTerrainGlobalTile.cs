using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>
	/// Draws partially carved tiles and keeps damage data consistent with the world. A carved tile is drawn from the
	/// material's plain interior texture (its original frame has outlines baked onto edges that may be gone now), and every
	/// pixel exposed to open space gets a dark border, Terraria-style; intact tiles next to carved ones get the same border
	/// where their neighbour's pixels are gone, so a cut surface reads as one continuous edge.
	/// </summary>
	public class PixelTerrainGlobalTile : GlobalTile
	{
		const int Sub = PixelTerrain.Sub, S = PixelTerrain.SubSize;
		static readonly Color Edge = new(0, 0, 0, 120);

		public override bool PreDraw(int i, int j, int type, SpriteBatch spriteBatch)
		{
			if (!PixelTerrain.TryGet(i, j, out var d))
				return true;
			Tile tile = Main.tile[i, j];
			if (!tile.HasTile || d.Type != tile.TileType) {
				PixelTerrain.Clear(i, j);
				return true;
			}
			// Slopes and half blocks keep vanilla rendering (damage still accumulates).
			if (tile.Slope != SlopeType.Solid || tile.IsHalfBlock)
				return true;

			Texture2D tex = Main.instance.TilesRenderer.GetTileDrawTexture(tile, i, j);
			if (tex == null)
				return true;

			Vector2 basePos = TileScreenPos(i, j);
			Color light = tile.IsTileFullbright ? Color.White : Lighting.GetColor(i, j);
			// the interior frame (a block surrounded on all sides: no outlines), varied like vanilla's
			int fx = Main.tileFrameImportant[type] ? tile.TileFrameX : 18 + 18 * ((i * 7 + j * 13) % 3);
			int fy = Main.tileFrameImportant[type] ? tile.TileFrameY : 18;

			for (int row = 0; row < Sub; row++) {
				int col = 0;
				while (col < Sub) {
					while (col < Sub && Removed(d.Mask, row, col))
						col++;
					int start = col;
					while (col < Sub && !Removed(d.Mask, row, col))
						col++;
					if (col > start) {
						var src = new Rectangle(fx + start * S, fy + row * S, (col - start) * S, S);
						spriteBatch.Draw(tex, basePos + new Vector2(start * S, row * S), src, light);
					}
				}
			}
			// dark borders on every remaining pixel that faces open space
			Texture2D px = TextureAssets.MagicPixel.Value;
			for (int row = 0; row < Sub; row++)
				for (int col = 0; col < Sub; col++)
					if (!Removed(d.Mask, row, col) && Exposed(i, j, d.Mask, row, col))
						spriteBatch.Draw(px, basePos + new Vector2(col * S, row * S), new Rectangle(0, 0, S, S), Edge.MultiplyRGBA(light));
			return false;
		}

		/// <summary>An intact tile next to a carved one: border its edge pixels where the neighbour's pixels are gone.</summary>
		public override void PostDraw(int i, int j, int type, SpriteBatch spriteBatch)
		{
			if (PixelTerrain.DamagedTileCount == 0 || PixelTerrain.TryGet(i, j, out _))
				return;
			bool l = PixelTerrain.TryGet(i - 1, j, out _), r = PixelTerrain.TryGet(i + 1, j, out _);
			bool u = PixelTerrain.TryGet(i, j - 1, out _), dn = PixelTerrain.TryGet(i, j + 1, out _);
			if (!(l || r || u || dn))
				return;
			Tile tile = Main.tile[i, j];
			if (!tile.HasTile || !Main.tileSolid[type] || Main.tileSolidTop[type] || tile.Slope != SlopeType.Solid || tile.IsHalfBlock)
				return;
			Vector2 basePos = TileScreenPos(i, j);
			Color c = Edge.MultiplyRGBA(Lighting.GetColor(i, j));
			Texture2D px = TextureAssets.MagicPixel.Value;
			for (int k = 0; k < Sub; k++) {
				if (l && !SolidPixel(i - 1, j, k, Sub - 1))
					spriteBatch.Draw(px, basePos + new Vector2(0, k * S), new Rectangle(0, 0, S, S), c);
				if (r && !SolidPixel(i + 1, j, k, 0))
					spriteBatch.Draw(px, basePos + new Vector2((Sub - 1) * S, k * S), new Rectangle(0, 0, S, S), c);
				if (u && !SolidPixel(i, j - 1, Sub - 1, k))
					spriteBatch.Draw(px, basePos + new Vector2(k * S, 0), new Rectangle(0, 0, S, S), c);
				if (dn && !SolidPixel(i, j + 1, 0, k))
					spriteBatch.Draw(px, basePos + new Vector2(k * S, (Sub - 1) * S), new Rectangle(0, 0, S, S), c);
			}
		}

		static Vector2 TileScreenPos(int i, int j)
		{
			Vector2 zero = Main.drawToScreen ? Vector2.Zero : new Vector2(Main.offScreenRange);
			return new Vector2(i * 16, j * 16) - Main.screenPosition + zero;
		}

		static bool Removed(ulong mask, int row, int col) => (mask & (1UL << (row * Sub + col))) != 0;

		/// <summary>Is pixel (row, col) of tile (i, j) solid terrain? Looks into neighbouring tiles' masks.</summary>
		static bool SolidPixel(int i, int j, int row, int col)
		{
			if (!WorldGen.InWorld(i, j))
				return true;
			Tile t = Main.tile[i, j];
			if (!t.HasTile || t.IsActuated || !Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType])
				return false;
			return !PixelTerrain.TryGet(i, j, out var d) || d.Type != t.TileType || !Removed(d.Mask, row, col);
		}

		static bool Exposed(int i, int j, ulong mask, int row, int col)
		{
			bool Open(int r, int c)
			{
				if (r >= 0 && r < Sub && c >= 0 && c < Sub)
					return Removed(mask, r, c);
				int ni = i + (c < 0 ? -1 : c >= Sub ? 1 : 0), nj = j + (r < 0 ? -1 : r >= Sub ? 1 : 0);
				return !SolidPixel(ni, nj, (r + Sub) % Sub, (c + Sub) % Sub);
			}
			return Open(row - 1, col) || Open(row + 1, col) || Open(row, col - 1) || Open(row, col + 1);
		}

		public override bool CanExplode(int i, int j, int type)
		{
			// Carvable terrain is handled by pixel explosions instead of vanilla's whole-tile removal.
			if (TerraCommandConfig.Instance.PixelExplosions && TerrainRules.IsCarvable(i, j))
				return false;
			return true;
		}

		public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly, ref bool noItem)
		{
			if (!fail && !effectOnly)
				PixelTerrain.Clear(i, j);
		}
	}
}
