using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>Explosions leave craters; bullets, arrows and bolts chip terrain where they hit.</summary>
	public class DestructionGlobalProjectile : GlobalProjectile
	{
		public override bool InstancePerEntity => true;

		/// <summary>Set when vanilla ran its tile-destroying explosion (we cratered there instead).</summary>
		bool cratered;

		static bool IsExplosive(Projectile p) => ProjectileID.Sets.Explosive[p.type] || p.aiStyle == ProjAIStyleID.Explosive;

		public override void Load() => On_Projectile.ExplodeTiles += OnExplodeTiles;

		/// <summary>
		/// Vanilla calls this (on the owner) with the real blast radius in tiles. Carve a matching crater;
		/// vanilla still runs afterwards for walls and non-carvable tiles (CanExplode blocks carvable ones).
		/// </summary>
		static void OnExplodeTiles(On_Projectile.orig_ExplodeTiles orig, Projectile self, Vector2 compareSpot, int radius,
			int minI, int maxI, int minJ, int maxJ, bool wallSplode)
		{
			if (TerraCommandConfig.Instance.PixelExplosions) {
				var center = new Vector2((minI + maxI + 1) * 8f, (minJ + maxJ + 1) * 8f);
				float r = Math.Clamp(radius * 16f + 6f, 12f, 400f);
				Testing.AutoTest.Trace($"ExplodeTiles {self.Name} radius={radius} tiles at {center.ToTileCoordinates()}");
				PixelTerrain.RequestCarve(center, r, TerrainRules.ExplosivePower, CarveFlags.Explosion);
				self.GetGlobalProjectile<DestructionGlobalProjectile>().cratered = true;
			}
			orig(self, compareSpot, radius, minI, maxI, minJ, maxJ, wallSplode);
		}

		public override bool PreKill(Projectile projectile, int timeLeft)
		{
			// before vanilla deals the explosion's damage, so blast deaths gib
			if (IsExplosive(projectile))
				Physics.Explosions.MarkBlasted(projectile.Center, (Physics.Explosions.BlastRadiusTiles(projectile) + 2) * 16f);
			return true;
		}

		public override void OnKill(Projectile projectile, int timeLeft)
		{
			if (IsExplosive(projectile))
				Testing.AutoTest.Trace($"explosive {projectile.Name} died at {projectile.Center.ToTileCoordinates()}");
			if (IsExplosive(projectile))
				Physics.Explosions.Blast(projectile.Center, (Physics.Explosions.BlastRadiusTiles(projectile) + 2) * 16f, Physics.Tuning.BlastImpulse, projectile.damage);
			if (Main.netMode == NetmodeID.MultiplayerClient || !TerraCommandConfig.Instance.PixelExplosions)
				return;
			if (cratered || !IsExplosive(projectile))
				return;
			// Explosives that don't destroy tiles in vanilla (grenades, Rocket I/III...) still scorch the ground.
			Testing.AutoTest.Trace($"scorch {projectile.Name} at {projectile.Center.ToTileCoordinates()}");
			PixelTerrain.RequestCarve(projectile.Center, 22f, TerrainRules.ExplosivePower, CarveFlags.Explosion);
		}

		public override bool OnTileCollide(Projectile projectile, Vector2 oldVelocity)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient || !TerraCommandConfig.Instance.ProjectilesChipTerrain)
				return true;
			if (projectile.damage <= 0 || projectile.minion || projectile.sentry || IsExplosive(projectile))
				return true;
			if (!projectile.friendly && !projectile.hostile)
				return true;
			if (oldVelocity.LengthSquared() < 4f)
				return true;

			if (!FindTerrainImpact(projectile, oldVelocity, out Vector2 impact))
				return true;
			float radius = Math.Clamp(2f + projectile.damage / 25f, 2f, 7f);
			int power = Math.Clamp(projectile.damage, 10, 100);
			PixelTerrain.RequestCarve(impact, radius, power, CarveFlags.None);
			return true;
		}
		/// <summary>Find the material the projectile's footprint hit, including the rim of an existing hole.</summary>
		static bool FindTerrainImpact(Projectile projectile, Vector2 velocity, out Vector2 impact) {
			Vector2 direction = Vector2.Normalize(velocity);
			Vector2 side = new(-direction.Y, direction.X);
			float halfWidth = projectile.width * 0.5f, halfHeight = projectile.height * 0.5f;
			float cross = Math.Abs(direction.Y) * halfWidth + Math.Abs(direction.X) * halfHeight;
			float forward = Math.Abs(direction.X) * halfWidth + Math.Abs(direction.Y) * halfHeight;
			float distance = velocity.Length() + forward + 2;
			for (float travel = 0; travel <= distance; travel += 0.5f) {
				Vector2 center = projectile.Center + direction * travel;
				// Prefer the centre line, then either side of the projectile footprint.
				if (PixelTerrain.IsPixelSolid(center.X, center.Y)) { impact = center; return true; }
				for (float offset = 0.5f; offset <= cross; offset += 0.5f) {
					Vector2 point = center + side * offset;
					if (PixelTerrain.IsPixelSolid(point.X, point.Y)) { impact = point; return true; }
					point = center - side * offset;
					if (PixelTerrain.IsPixelSolid(point.X, point.Y)) { impact = point; return true; }
				}
			}
			impact = default;
			return false;
		}

	}
}
