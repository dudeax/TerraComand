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
			if (IsExplosive(projectile) && projectile.ModProjectile is not Content.Projectiles.LaunchRocket)
				Physics.Explosions.MarkBlasted(projectile.Center, (Physics.Explosions.BlastRadiusTiles(projectile) + 2) * 16f);
			return true;
		}

		public override void OnKill(Projectile projectile, int timeLeft)
		{
			if (IsExplosive(projectile))
				Testing.AutoTest.Trace($"explosive {projectile.Name} died at {projectile.Center.ToTileCoordinates()}");
			if (IsExplosive(projectile) && projectile.ModProjectile is not Content.Projectiles.LaunchRocket)
				Physics.Explosions.Blast(projectile.Center, (Physics.Explosions.BlastRadiusTiles(projectile) + 2) * 16f, Physics.Tuning.BlastImpulse);
			if (Main.netMode == NetmodeID.MultiplayerClient || !TerraCommandConfig.Instance.PixelExplosions)
				return;
			if (cratered || !IsExplosive(projectile) || projectile.ModProjectile is Content.Projectiles.DropPod)
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

			Vector2 dir = Vector2.Normalize(oldVelocity);
			Vector2 impact = projectile.Center + dir * (Math.Max(projectile.width, projectile.height) * 0.5f + 2f);
			float radius = Math.Clamp(2f + projectile.damage / 25f, 2f, 7f);
			int power = Math.Clamp(projectile.damage, 10, 100);
			PixelTerrain.RequestCarve(impact, radius, power, CarveFlags.None);
			return true;
		}
	}
}
