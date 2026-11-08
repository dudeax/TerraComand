using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Content.Items.Devices;

namespace TerraCommand.Common.Combat
{
	/// <summary>A body holding a shield stops hostile projectiles that hit the shield (held in front, along the aim).</summary>
	public class ShieldGlobalProjectile : GlobalProjectile
	{
		/// <summary>About to hit a body holding a shield, from the side the shield faces: the shield takes it.</summary>
		public override bool CanHitPlayer(Projectile projectile, Player target)
		{
			if (target.inventory[ActorInventory.Held].ModItem is not RiotShield || target.dead)
				return true;
			int facing = target.GetModPlayer<Physics.BodyPhysicsPlayer>().Actor.FlipFactor;
			if (System.Math.Sign(projectile.Center.X - target.Center.X) != facing)
				return true;   // from behind: the shield doesn't help
			ShieldPlayer.Block(projectile);
			return false;
		}
	}

	public class ShieldPlayer : ModPlayer
	{
		public static void Block(Projectile p)
		{
			p.Kill();
			Blocked++;
			if (!Main.dedServ) {
				SoundEngine.PlaySound(SoundID.NPCHit4 with { Volume = 0.6f, Pitch = 0.3f }, p.Center);
				for (int i = 0; i < 4; i++)
					Dust.NewDust(p.Center, 2, 2, DustID.Iron, -p.velocity.X * 0.2f, -p.velocity.Y * 0.2f);
			}
		}

		public static int Blocked;

		public Rectangle? ShieldBox()
		{
			if (Player.dead || Player.inventory[ActorInventory.Held].ModItem is not RiotShield)
				return null;
			RigPose pose = Player.GetModPlayer<RigPlayer>().Pose();
			Vector2 c = RigRenderer.Grip(pose) + RigRenderer.AimWorld(pose).ToRotationVector2() * 4f;
			return new Rectangle((int)c.X - 6, (int)c.Y - 16, 12, 32);
		}

		public override void PreUpdate()
		{
			if (ShieldBox() is not Rectangle box)
				return;
			// anything hostile passing through the shield this tick (swept: fast rounds move further than the shield is wide)
			foreach (Projectile p in Main.ActiveProjectiles) {
				if (!p.hostile || p.damage <= 0)
					continue;
				Vector2 end = p.Center + p.velocity * (p.extraUpdates + 1);
				float _ = 0f;
				if (p.Hitbox.Intersects(box) || Collision.CheckAABBvLineCollision(box.TopLeft(), box.Size(), p.Center, end, 4f, ref _))
					Block(p);
			}
		}
	}
}
