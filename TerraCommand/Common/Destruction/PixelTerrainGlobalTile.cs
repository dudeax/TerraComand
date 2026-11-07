using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>Draws partially carved tiles and keeps damage data consistent with the world.</summary>
	public class PixelTerrainGlobalTile : GlobalTile
	{
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

			Vector2 zero = Main.drawToScreen ? Vector2.Zero : new Vector2(Main.offScreenRange);
			Vector2 basePos = new Vector2(i * 16, j * 16) - Main.screenPosition + zero;
			Color light = Lighting.GetColor(i, j);
			if (tile.IsTileFullbright)
				light = Color.White;
			int fx = tile.TileFrameX, fy = tile.TileFrameY;
			const int S = PixelTerrain.SubSize;

			for (int row = 0; row < PixelTerrain.Sub; row++) {
				int col = 0;
				while (col < PixelTerrain.Sub) {
					// skip removed pixels
					while (col < PixelTerrain.Sub && (d.Mask & (1UL << (row * PixelTerrain.Sub + col))) != 0)
						col++;
					int start = col;
					while (col < PixelTerrain.Sub && (d.Mask & (1UL << (row * PixelTerrain.Sub + col))) == 0)
						col++;
					if (col > start) {
						var src = new Rectangle(fx + start * S, fy + row * S, (col - start) * S, S);
						spriteBatch.Draw(tex, basePos + new Vector2(start * S, row * S), src, light);
					}
				}
			}
			return false;
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
