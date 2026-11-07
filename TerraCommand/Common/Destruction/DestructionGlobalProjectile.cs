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
		static bool IsExplosive(Projectile p) => ProjectileID.Sets.Explosive[p.type] || p.aiStyle == ProjAIStyleID.Explosive;

		public override void OnKill(Projectile projectile, int timeLeft)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient || !TerraCommandConfig.Instance.PixelExplosions)
				return;
			if (!IsExplosive(projectile) || projectile.ModProjectile is Content.Projectiles.DropPod)
				return;

			// Explosives are usually resized to their blast area right before dying.
			float radius = Math.Clamp(Math.Max(projectile.width, projectile.height) * 0.5f, 12f, 200f);
			// Vanilla tile-destroying explosives (bombs, dynamite) dig full craters; others (grenades, rockets) scorch.
			if (!ProjectileID.Sets.Explosive[projectile.type])
				radius *= 0.5f;
			PixelTerrain.RequestCarve(projectile.Center, radius, TerrainRules.ExplosivePower, CarveFlags.Explosion);
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
