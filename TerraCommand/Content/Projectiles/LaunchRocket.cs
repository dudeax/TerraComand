using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Content.Projectiles
{
	/// <summary>Cosmetic rocket carrying cargo to orbit. ai[0] = item type shown as payload (unused visually for now).</summary>
	public class LaunchRocket : ModProjectile
	{
		public static void Spawn(Vector2 padTop, int itemType)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
				return;
			Projectile.NewProjectile(Terraria.Entity.GetSource_NaturalSpawn(), padTop - new Vector2(0, 20), new Vector2(0, -1f),
				ModContent.ProjectileType<LaunchRocket>(), 0, 0f, Main.myPlayer, itemType);
		}

		public override void SetDefaults()
		{
			Projectile.width = 14;
			Projectile.height = 30;
			Projectile.tileCollide = false;
			Projectile.ignoreWater = true;
			Projectile.friendly = false;
			Projectile.hostile = false;
			Projectile.timeLeft = 150;
			Projectile.penetrate = -1;
		}

		public override void AI()
		{
			if (Projectile.localAI[0]++ == 0)
				Terraria.Audio.SoundEngine.PlaySound(SoundID.Item61, Projectile.Center);
			Projectile.velocity.Y -= 0.35f;
			Projectile.velocity.X = (float)System.Math.Sin(Projectile.localAI[0] * 0.15f) * 0.3f;
			if (!Main.dedServ) {
				var d = Dust.NewDustDirect(Projectile.Bottom - new Vector2(4, 0), 8, 6, DustID.Torch, 0, 4f, 100, default, 1.6f);
				d.noGravity = true;
				Dust.NewDust(Projectile.Bottom - new Vector2(6, 0), 12, 6, DustID.Smoke, 0, 2f, 150);
			}
		}
	}
}
