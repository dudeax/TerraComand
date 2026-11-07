using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Destruction;

namespace TerraCommand.Content.Projectiles
{
	/// <summary>Falls from orbit, cratering the ground on impact and spilling its cargo.</summary>
	public class DropPod : ModProjectile
	{
		// Cargo lives only on the authority; the projectile id is the key.
		static readonly Dictionary<int, List<Item>> cargo = new();
		static int nextId = 1;

		public static void Spawn(Vector2 target, List<Item> items)
		{
			int id = nextId++;
			cargo[id] = items;
			float startY = System.Math.Max(target.Y - 1600f, 16 * 12f);
			Projectile.NewProjectile(Terraria.Entity.GetSource_NaturalSpawn(), new Vector2(target.X, startY), new Vector2(0, 14f),
				ModContent.ProjectileType<DropPod>(), 0, 0f, Main.myPlayer, id);
		}

		public override void SetDefaults()
		{
			Projectile.width = 26;
			Projectile.height = 34;
			Projectile.tileCollide = true;
			Projectile.friendly = false;
			Projectile.hostile = false;
			Projectile.penetrate = -1;
			Projectile.timeLeft = 600;
		}

		public override void AI()
		{
			Projectile.velocity.Y = System.Math.Min(Projectile.velocity.Y + 0.4f, 22f);
			Projectile.rotation = 0f;
			if (!Main.dedServ) {
				var d = Dust.NewDustDirect(Projectile.position, Projectile.width, 8, DustID.Torch, 0, -2f, 100, default, 1.4f);
				d.noGravity = true;
			}
		}

		public override bool OnTileCollide(Vector2 oldVelocity) => true; // die on impact

		public override void OnKill(int timeLeft)
		{
			Terraria.Audio.SoundEngine.PlaySound(SoundID.Item14, Projectile.Center);
			if (!Main.dedServ) {
				for (int i = 0; i < 25; i++)
					Dust.NewDust(Projectile.position, Projectile.width, Projectile.height, DustID.Smoke, Main.rand.NextFloat(-4, 4), Main.rand.NextFloat(-5, 0), 100, default, 1.5f);
			}
			if (Main.netMode == NetmodeID.MultiplayerClient)
				return;
			PixelTerrain.RequestCarve(Projectile.Bottom, 18f, 50, CarveFlags.Explosion);
			if (cargo.Remove((int)Projectile.ai[0], out var items)) {
				foreach (Item it in items)
					Item.NewItem(Projectile.GetSource_DropAsItem(), Projectile.Center - new Vector2(0, 16), it, noBroadcast: false, noGrabDelay: true);
			}
		}
	}
}
