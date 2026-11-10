using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Physics;
using Actors = TerraCommand.Common.Actors;

namespace TerraCommand.Content.Projectiles
{
	// ======================================================================== rounds

	/// <summary>
	/// A spinning saw blade (CC's ricochet rounds): it bounces off terrain, cutting a notch each time, and slices through
	/// several bodies before it breaks. Example round for "a gun that shoots chainsaws".
	/// </summary>
	public class SawBlade : ModProjectile
	{
		public override void SetDefaults()
		{
			Projectile.width = Projectile.height = 14 * Actors.CC.WorldScale;
			Projectile.friendly = true;
			Projectile.DamageType = DamageClass.Ranged;
			Projectile.penetrate = 6;
			Projectile.timeLeft = 240;
			Projectile.usesLocalNPCImmunity = true;
			Projectile.localNPCHitCooldown = 12;
			Projectile.extraUpdates = 1;
		}

		ref float Bounces => ref Projectile.ai[0];

		public override void AI()
		{
			Projectile.rotation += 0.6f * Math.Sign(Projectile.velocity.X == 0 ? 1 : Projectile.velocity.X);
			Projectile.velocity.Y += 0.04f * Actors.CC.WorldScale;
			if (Main.rand.NextBool(4) && !Main.dedServ) {
				var d = Dust.NewDustDirect(Projectile.position, Projectile.width, Projectile.height, DustID.Iron, 0, 0, 120, default, 0.8f);
				d.noGravity = true;
			}
		}

		public override bool OnTileCollide(Vector2 oldVelocity)
		{
			// cut into what it hit, then ricochet
			Vector2 dir = oldVelocity.SafeNormalize(Vector2.UnitX);
			if (Main.netMode != NetmodeID.MultiplayerClient)
				PixelTerrain.RequestCarve(Projectile.Center + dir * 7f, 6f, 60, CarveFlags.None);
			SoundEngine.PlaySound(SoundID.Item22 with { Volume = 0.4f, Pitch = 0.8f }, Projectile.Center);
			if (++Bounces > 6)
				return true;
			if (Projectile.velocity.X != oldVelocity.X)
				Projectile.velocity.X = -oldVelocity.X * 0.85f;
			if (Projectile.velocity.Y != oldVelocity.Y)
				Projectile.velocity.Y = -oldVelocity.Y * 0.85f;
			return false;
		}

		public override bool PreDraw(ref Color lightColor)
		{
			Texture2D tex = TextureAssets.Projectile[Type].Value;
			Main.EntitySpriteDraw(tex, Projectile.Center - Main.screenPosition, null, lightColor, Projectile.rotation, tex.Size() / 2f, Actors.CC.WorldScale, SpriteEffects.None);
			return false;
		}
	}

	/// <summary>
	/// A digger's particle (CC diggers fire particles that eat terrain up to their strength): flies a short way, and where it
	/// meets terrain it takes a bite (dropping the material like mining). Strength = pick power, in ai[0].
	/// </summary>
	public class DigParticle : ModProjectile
	{
		public override void SetDefaults()
		{
			Projectile.width = Projectile.height = 4;
			Projectile.friendly = true;
			Projectile.DamageType = DamageClass.Ranged;
			Projectile.penetrate = 1;
			Projectile.timeLeft = 10;      // short range
			Projectile.tileCollide = false;
			Projectile.hide = true;
		}

		public override void AI()
		{
			if (!Main.dedServ && Main.rand.NextBool(2)) {
				var d = Dust.NewDustDirect(Projectile.position, 4, 4, DustID.Electric, 0, 0, 150, default, 0.5f);
				d.noGravity = true;
				d.velocity *= 0.2f;
			}
			if (!PixelTerrain.IsPixelSolid(Projectile.Center.X, Projectile.Center.Y))
				return;
			if (Main.netMode != NetmodeID.MultiplayerClient)
				PixelTerrain.RequestCarve(Projectile.Center, 4f, (int)Projectile.ai[0], CarveFlags.DropItems);
			Projectile.Kill();
		}
	}

	/// <summary>A sliver of a frag grenade's casing: fast, short-lived, chips terrain where it hits.</summary>
	public class Shrapnel : ModProjectile
	{
		public override string Texture => "Terraria/Images/Projectile_" + ProjectileID.Bullet;

		public override void SetDefaults()
		{
			Projectile.width = Projectile.height = 4;
			Projectile.friendly = true;
			Projectile.DamageType = DamageClass.Ranged;
			Projectile.penetrate = 1;
			Projectile.timeLeft = 60;
			Projectile.extraUpdates = 2;
			Projectile.scale = 0.6f;
		}

		public static int Spawned;   // tests

		public override void OnSpawn(Terraria.DataStructures.IEntitySource source) => Spawned++;

		public override void AI() => Projectile.rotation = Projectile.velocity.ToRotation() + MathHelper.PiOver2;
	}

	// ======================================================================== thrown devices

	/// <summary>
	/// CC thrown devices (ThrownDevice + its grenade): a timed fuse, or triggered by impact, by sticking to what it hits, or
	/// by a remote detonator; on going off it explodes, optionally bursting into shrapnel or bomblets. Subclasses set the mix.
	/// </summary>
	public abstract class CCGrenade : ModProjectile
	{
		protected virtual int Fuse => 150;             // ticks (CC TriggerDelay); 0 = none
		protected virtual bool Impact => false;        // goes off on hitting anything
		protected virtual bool Sticky => false;        // sticks to terrain, then the fuse runs
		protected virtual bool Remote => false;        // waits for the detonator
		protected virtual int ShrapnelCount => 0;
		protected virtual int Bomblets => 0;
		protected virtual float BlastRadius => 64f;
		protected virtual bool Small => false;

		public bool Stuck => Projectile.ai[1] == 1f;
		bool armed;

		public override void SetDefaults()
		{
			Projectile.width = Projectile.height = (Small ? 8 : 12) * Actors.CC.WorldScale;
			Projectile.friendly = true;
			Projectile.DamageType = DamageClass.Ranged;
			Projectile.penetrate = -1;
			Projectile.timeLeft = 60 * 60;
		}

		public override bool? CanDamage() => false;   // the explosion does the damage

		public override void AI()
		{
			if (Projectile.ai[2] == 1f) {   // detonation requested (remote)
				Projectile.Kill();
				return;
			}
			if (!armed) {
				armed = true;
				if (Fuse > 0 && !Remote)
					Projectile.timeLeft = Fuse;
			}
			if (Stuck) {
				Projectile.velocity = Vector2.Zero;
				if (Remote && Main.GameUpdateCount % 40 == 0 && !Main.dedServ)
					Dust.NewDust(Projectile.Center, 1, 1, DustID.RedTorch, 0, 0, 0, default, 0.8f);
				return;
			}
			// impact fuses also go off on hitting a creature
			if (Impact && Projectile.owner == Main.myPlayer)
				foreach (NPC n in Main.ActiveNPCs)
					if (!n.friendly && n.Hitbox.Intersects(Projectile.Hitbox)) {
						Projectile.Kill();
						return;
					}
			Projectile.velocity.Y = Math.Min(Projectile.velocity.Y + 0.25f * Actors.CC.WorldScale, 12f * Actors.CC.WorldScale);
			Projectile.rotation += Projectile.velocity.X * 0.08f;
			if (Fuse > 0 && !Remote && Projectile.timeLeft % 20 == 0 && !Main.dedServ)
				Dust.NewDust(Projectile.Center, 1, 1, DustID.Torch, 0, -1f, 0, default, 0.7f);
		}

		public override bool OnTileCollide(Vector2 oldVelocity)
		{
			if (Impact)
				return true;
			if (Sticky || Remote) {
				Projectile.ai[1] = 1f;
				Projectile.velocity = Vector2.Zero;
				Projectile.netUpdate = true;
				SoundEngine.PlaySound(SoundID.Item52 with { Volume = 0.5f }, Projectile.Center);
				return false;
			}
			// bounce, losing most of the energy
			if (Projectile.velocity.X != oldVelocity.X)
				Projectile.velocity.X = -oldVelocity.X * 0.4f;
			if (Projectile.velocity.Y != oldVelocity.Y)
				Projectile.velocity.Y = -oldVelocity.Y * 0.35f;
			Projectile.velocity.X *= 0.85f;
			return false;
		}


		public override void OnKill(int timeLeft)
		{
			Explosions.Detonate(Projectile.Center, BlastRadius * Actors.CC.WorldScale, Projectile.damage, Projectile.owner);
			if (Projectile.owner != Main.myPlayer)
				return;
			var src = Projectile.GetSource_FromThis();
			for (int k = 0; k < ShrapnelCount; k++) {
				Vector2 v = Main.rand.NextVector2Unit() * Main.rand.NextFloat(8f, 13f);
				Projectile.NewProjectile(src, Projectile.Center, v, ModContent.ProjectileType<Shrapnel>(), Projectile.damage / 3, 1f, Projectile.owner);
			}
			for (int k = 0; k < Bomblets; k++) {
				Vector2 v = new Vector2(Main.rand.NextFloat(-4f, 4f), -Main.rand.NextFloat(3f, 6f));
				Projectile.NewProjectile(src, Projectile.Center, v, ModContent.ProjectileType<Bomblet>(), Projectile.damage / 2, 1f, Projectile.owner);
			}
		}

		/// <summary>The detonator: every remote charge this player has planted goes off.</summary>
		public static int DetonateAll(int owner)
		{
			int n = 0;
			foreach (Projectile p in Main.ActiveProjectiles) {
				if (p.owner == owner && p.ModProjectile is CCGrenade g && g.Remote) {
					p.ai[2] = 1f;
					p.netUpdate = true;
					n++;
				}
			}
			return n;
		}

		public override bool PreDraw(ref Color lightColor)
		{
			Texture2D tex = TextureAssets.Projectile[Type].Value;
			Main.EntitySpriteDraw(tex, Projectile.Center - Main.screenPosition, null, lightColor, Projectile.rotation, tex.Size() / 2f, Actors.CC.WorldScale, SpriteEffects.None);
			return false;
		}
	}

	/// <summary>Timed fuse; bursts into shrapnel.</summary>
	public class FragGrenadeProjectile : CCGrenade
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/FragGrenade";
		protected override int Fuse => 150;
		protected override int ShrapnelCount => 14;
		protected override float BlastRadius => 56f;
	}

	/// <summary>Goes off the moment it hits anything.</summary>
	public class ImpactGrenadeProjectile : CCGrenade
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/ImpactGrenade";
		protected override int Fuse => 0;
		protected override bool Impact => true;
		protected override float BlastRadius => 60f;
	}

	/// <summary>Sticks where it lands; the fuse runs.</summary>
	public class StickyChargeProjectile : CCGrenade
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/StickyCharge";
		protected override int Fuse => 180;
		protected override bool Sticky => true;
		protected override float BlastRadius => 72f;
	}

	/// <summary>Timed fuse; scatters bomblets that go off on impact.</summary>
	public class ClusterGrenadeProjectile : CCGrenade
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/ClusterGrenade";
		protected override int Fuse => 120;
		protected override int Bomblets => 6;
		protected override float BlastRadius => 40f;
	}

	/// <summary>Sticks and waits for the detonator.</summary>
	public class RemoteChargeProjectile : CCGrenade
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/RemoteCharge";
		protected override int Fuse => 0;
		protected override bool Remote => true;
		protected override float BlastRadius => 80f;
	}

	/// <summary>A cluster grenade's bomblet.</summary>
	public class Bomblet : CCGrenade
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/ClusterGrenade";
		protected override int Fuse => 90;
		protected override bool Impact => true;
		protected override float BlastRadius => 30f;
		protected override bool Small => true;

		public override bool PreDraw(ref Color lightColor)
		{
			Texture2D tex = TextureAssets.Projectile[Type].Value;
			Main.EntitySpriteDraw(tex, Projectile.Center - Main.screenPosition, null, lightColor, Projectile.rotation, tex.Size() / 2f, 0.55f * Actors.CC.WorldScale, SpriteEffects.None);
			return false;
		}
	}
}
