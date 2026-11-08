using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>Pickaxes carve the art pixels nearest the cursor instead of chipping a hit-point bar.</summary>
	public class PixelMining : ModSystem
	{
		/// <summary>Where a digger's dig lands this tick (set by the device code: CC diggers dig along the aim).</summary>
		public static Vector2? DigPoint;

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

			Vector2 aim = self.whoAmI == Main.myPlayer ? DigPoint ?? Main.MouseWorld : self.Center;
			if (self.whoAmI == Main.myPlayer && DigPoint is Vector2 dp && !Main.dedServ)
				for (int i = 0; i < 3; i++)
					Dust.NewDust(dp - new Vector2(4), 8, 8, DustID.Dirt, -self.direction * Main.rand.NextFloat(1f, 3f), -1f);
			PixelTerrain.RequestMine(x, y, aim, pickPower);
		}
	}
}
