using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>Pickaxes carve the art pixels nearest the cursor instead of chipping a hit-point bar.</summary>
	public class PixelMining : ModSystem
	{
		public override void Load() => On_Player.PickTile += OnPickTile;

		static void OnPickTile(On_Player.orig_PickTile orig, Player self, int x, int y, int pickPower)
		{
			if (!TerraCommandConfig.Instance.PixelMining || !TerrainRules.IsCarvable(x, y)) {
				orig(self, x, y, pickPower);
				return;
			}
			Tile tile = Main.tile[x, y];
			if (pickPower < TerrainRules.MinPick(tile.TileType) || !WorldGen.CanKillTile(x, y)) {
				orig(self, x, y, pickPower); // let vanilla show its "too hard" feedback
				return;
			}

			Vector2 aim = self.whoAmI == Main.myPlayer ? Main.MouseWorld : self.Center;
			PixelTerrain.RequestMine(x, y, aim, pickPower);
		}
	}
}
