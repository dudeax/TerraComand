using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerraCommand.Common.Destruction;

namespace TerraCommand.Common.Actors
{
	/// <summary>Read the same remaining 2px terrain cells as destruction, with slopes and one-way platforms.</summary>
	public sealed class TerrariaActorTerrain : IPenetrableTerrain
	{
		public static readonly TerrariaActorTerrain Instance = new();
		public bool Penetrate(Vector2 point,float momentum,out float loss) {
			loss=0;
			if (Main.netMode == NetmodeID.MultiplayerClient || !TerraCommandConfig.Instance.PhysicalTerrainPenetration) return false;
			int i=(int)MathF.Floor(point.X/16),j=(int)MathF.Floor(point.Y/16);
			if (!TerrainRules.IsCarvable(i,j) || !WorldGen.CanKillTile(i,j)) return false;
			int type=Main.tile[i,j].TileType;
			if (!TerrainRules.ExplosionCanAffect(type) || TerrainRules.MinPick(type)>TerrainRules.ExplosivePower) return false;
			if (!Physics.MaterialSolver.Penetration(momentum,BallisticFractures.Resistance(point),1,out loss)) return false;
			return PixelTerrain.PenetrateCell(point);
		}

		public bool Sample(Vector2 point, out ContactSurface surface)
		{
			surface = new ContactSurface(0.7f, 0.05f);
			int i = (int)MathF.Floor(point.X / 16), j = (int)MathF.Floor(point.Y / 16);
			if (i < 1 || j < 1 || i >= Main.maxTilesX - 1 || j >= Main.maxTilesY - 1) return true;
			Tile tile = Main.tile[i, j];
			if (!tile.HasTile || tile.IsActuated) return false;
			int type = tile.TileType;
			float x = point.X - i * 16, y = point.Y - j * 16;
			if (Main.tileSolidTop[type] || TileID.Sets.Platforms[type]) {
				surface = new ContactSurface(0.8f, 0, true, j * 16);
				return y < 2;
			}
			if (!Main.tileSolid[type]) return false;
			if (tile.IsHalfBlock && y < 8) return false;
			bool slope = (int)tile.Slope switch { 1 => y >= x, 2 => y >= 16 - x, 3 => y <= 16 - x, 4 => y <= x, _ => true };
			if (!slope) return false;
			if (PixelTerrain.TryGet(i, j, out var damage) && damage.Type == tile.TileType) {
				int bit = Math.Clamp((int)y / 2, 0, 7) * 8 + Math.Clamp((int)x / 2, 0, 7);
				if ((damage.Mask & (1UL << bit)) != 0) return false;
			}
			var material = TerrainRules.Material(type);
			surface = new ContactSurface(material.Friction, material.Restitution);
			return true;
		}
	}
}
