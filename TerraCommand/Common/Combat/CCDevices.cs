using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Combat
{
	/// <summary>
	/// The Cortex Command firearm model as data (HDFirearm + Magazine + Round), for items that declare their CC stats
	/// explicitly instead of having them derived from a Terraria weapon. Every CC firearm feature is here, so new guns are
	/// mostly a spec: fire rate (RPM), full/semi auto, bursts, spin-up (ActivationDelay), several particles per round
	/// (shotguns), magazine and reload, shake / sharp shake / sharp length, recoil, and what the round is.
	/// </summary>
	public sealed class FirearmSpec
	{
		public float Mass = 0f;              // explicit CC device mass; zero uses the generic estimate
		public int RateOfFire = 600;          // rounds per minute
		public bool FullAuto = true;
		public int Burst = 1;                 // rounds per trigger pull (semi-auto bursts)
		public int ActivationDelay;           // ticks the trigger must be held before the first round (spin-up)
		public int Magazine = 30;
		public int ReloadTicks = 108;
		public float ShakeDeg = 3f, SharpShakeDeg = 1.5f;
		public float SharpLength = 250f;
		public float NoSupportFactor = 2f;
		public int ParticleCount = 1;         // particles per round (a shotgun's pellets)
		public float ParticleSpreadDeg;       // pellets' own spread
		public int Projectile = ProjectileID.Bullet;
		public float FireVelocity = 14f;      // px/tick
		public float VelocityVariation;       // 0..1, per particle
		public float Recoil = 2f;             // knockback-like push on the shooter
		public bool OneHanded;
		public SoundStyle? Sound = SoundID.Item11;
	}

	/// <summary>Base class for original CC-style guns, launchers and diggers: give it a <see cref="FirearmSpec"/>.</summary>
	public abstract class CCFirearm : ModItem
	{
		public abstract FirearmSpec Spec { get; }
		protected abstract int Damage { get; }
		protected virtual int Rarity => ItemRarityID.Blue;
		protected virtual int ValueCopper => Item.buyPrice(silver: 50);

		public override void SetDefaults()
		{
			FirearmSpec s = Spec;
			Item.width = 40;
			Item.height = 20;
			Item.useStyle = ItemUseStyleID.Shoot;
			Item.useTime = Item.useAnimation = Math.Max(2, (int)Math.Round(3600f / s.RateOfFire));
			Item.autoReuse = s.FullAuto || s.Burst > 1;
			Item.noMelee = true;
			Item.DamageType = DamageClass.Ranged;
			Item.damage = Damage;
			Item.knockBack = s.Recoil;
			Item.shoot = s.Projectile;
			Item.shootSpeed = s.FireVelocity;
			Item.UseSound = s.Sound;
			Item.rare = Rarity;
			Item.value = ValueCopper;
		}

		/// <summary>Rounds leave the muzzle (position/velocity already aimed and shaken by the device code); several particles fan out.</summary>
		public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position, Vector2 velocity, int type, int damage, float knockback)
		{
			Fire(source, position, velocity, type, damage, knockback, player.whoAmI, Spec);
			return false;
		}

		/// <summary>One round: its particles, each with the round's own spread and velocity variation. Used by players and idle bodies.</summary>
		public static void Fire(IEntitySource source, Vector2 muzzle, Vector2 velocity, int type, int damage, float knockback, int owner, FirearmSpec s,
			bool hostile = false)
		{
			for (int k = 0; k < s.ParticleCount; k++) {
				Vector2 v = velocity;
				if (s.ParticleSpreadDeg > 0f)
					v = v.RotatedBy(MathHelper.ToRadians(Main.rand.NextFloat(-s.ParticleSpreadDeg, s.ParticleSpreadDeg) / 2f));
				if (s.VelocityVariation > 0f)
					v *= 1f + Main.rand.NextFloat(-s.VelocityVariation, s.VelocityVariation);
				int pi = Projectile.NewProjectile(source, muzzle, v, type, damage, knockback, owner);
				if (hostile)
					EnemyFire.Mark(Main.projectile[pi]);
			}
		}
	}

	/// <summary>Base class for thrown CC devices (grenades, charges): consumable, thrown with a wind-up by the device code.</summary>
	public abstract class CCThrowable : ModItem
	{
		protected abstract int ProjectileType { get; }
		protected abstract int Damage { get; }

		public override void SetDefaults()
		{
			Item.width = Item.height = 14;
			Item.useStyle = ItemUseStyleID.Swing;
			Item.useTime = Item.useAnimation = 30;
			Item.consumable = true;
			Item.maxStack = Item.CommonMaxStack;
			Item.noUseGraphic = true;
			Item.noMelee = true;
			Item.shoot = ProjectileType;
			Item.shootSpeed = 6f;
			Item.damage = Damage;
			Item.DamageType = DamageClass.Ranged;
			Item.value = Item.buyPrice(silver: 5);
			Item.rare = ItemRarityID.Blue;
		}
	}

	/// <summary>
	/// Rounds fired by the AI faction: hostile (they hurt players and players' bodies, not the faction's own). The flag
	/// isn't part of vanilla's projectile sync, so it travels with the projectile's extra AI.
	/// </summary>
	public class EnemyFire : GlobalProjectile
	{
		public override bool InstancePerEntity => true;
		public bool Enemy;

		public static void Mark(Projectile p)
		{
			p.GetGlobalProjectile<EnemyFire>().Enemy = true;
			Apply(p);
			p.netUpdate = true;
		}

		static void Apply(Projectile p)
		{
			p.hostile = true;
			p.friendly = false;
		}

		public override void SendExtraAI(Projectile p, Terraria.ModLoader.IO.BitWriter bits, System.IO.BinaryWriter w) => bits.WriteBit(Enemy);

		public override void ReceiveExtraAI(Projectile p, Terraria.ModLoader.IO.BitReader bits, System.IO.BinaryReader r)
		{
			Enemy = bits.ReadBit();
			if (Enemy)
				Apply(p);
		}

		/// <summary>The faction's rounds pass through its own bodies.</summary>
		public override bool? CanHitNPC(Projectile p, NPC target) =>
			Enemy && target.ModNPC is Content.NPCs.CloneUnit u && u.Team == Content.NPCs.CloneUnit.EnemyTeam ? false : null;
	}
}
