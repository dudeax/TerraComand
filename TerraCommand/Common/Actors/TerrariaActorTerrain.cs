using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerraCommand.Common.Destruction;

namespace TerraCommand.Common.Actors
{
	/// <summary>Read the same remaining 2px terrain cells as destruction, with slopes and one-way platforms.</summary>
	public sealed class TerrariaActorTerrain : IActorTerrain
	{
		public static readonly TerrariaActorTerrain Instance = new();
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
			float friction = type == TileID.IceBlock || type == TileID.BreakableIce ? 0.08f : TerrainRules.Softness(type) >= 1 ? 0.85f : 0.65f;
			surface = new ContactSurface(friction, 0.05f);
			return true;
		}
	}
}
