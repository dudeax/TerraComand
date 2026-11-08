using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Combat;
using TerraCommand.Content.Projectiles;

namespace TerraCommand.Content.Items.Devices
{
	// Original Cortex Command-style devices: one example of each CC device system, slotted into Terraria's progression.
	// (Art: tools/gen_art.py. Stats: CC archetypes - see docs/cc-actor-design.md.)

	// ======================================================================== firearms

	/// <summary>Semi-automatic sidearm (CC pistol archetype): accurate, small magazine, quick reload, one-handed.</summary>
	public class ServicePistol : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 400, FullAuto = false, Magazine = 12, ReloadTicks = 60,
			ShakeDeg = 2.5f, SharpShakeDeg = 1f, SharpLength = 160f, FireVelocity = 13f, Recoil = 1.5f, OneHanded = true,
		};
		protected override int Damage => 14;
		protected override int ValueCopper => Item.buyPrice(silver: 40);
		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 8).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Full-auto rifle (CC assault rifle archetype): 30 rounds, 1.8 s reload.</summary>
	public class FieldRifle : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 700, Magazine = 30, ReloadTicks = 108, ShakeDeg = 5f, SharpShakeDeg = 3f, SharpLength = 250f,
			FireVelocity = 16f, Recoil = 2f,
		};
		protected override int Damage => 11;
		protected override int ValueCopper => Item.buyPrice(gold: 1);
		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 14).AddIngredient(ItemID.Lens, 2).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Three-round bursts per trigger pull (CC burst-fire): steadier than full auto.</summary>
	public class TriBurstRifle : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 900, FullAuto = false, Burst = 3, Magazine = 24, ReloadTicks = 96, ShakeDeg = 3.5f, SharpShakeDeg = 1.5f,
			SharpLength = 280f, FireVelocity = 17f, Recoil = 1.8f,
		};
		protected override int Damage => 16;
		protected override int Rarity => ItemRarityID.Green;
		protected override int ValueCopper => Item.buyPrice(gold: 2);
		public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.SilverBar, 14).AddIngredient(ItemID.Lens, 2).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Shotgun (CC multi-particle rounds): eight pellets fan out from every shot.</summary>
	public class Scattergun : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 90, FullAuto = false, Magazine = 6, ReloadTicks = 120, ShakeDeg = 2f, SharpShakeDeg = 1f, SharpLength = 150f,
			ParticleCount = 8, ParticleSpreadDeg = 14f, FireVelocity = 12f, VelocityVariation = 0.15f, Recoil = 5f,
			Sound = SoundID.Item36,
		};
		protected override int Damage => 9;
		protected override int Rarity => ItemRarityID.Green;
		protected override int ValueCopper => Item.buyPrice(gold: 2);
		public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.GoldBar, 12).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Sniper rifle (CC long SharpLength): reaches far with sharp aim, tiny shake, big hits, slow.</summary>
	public class Longshot : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 50, FullAuto = false, Magazine = 5, ReloadTicks = 150, ShakeDeg = 1.2f, SharpShakeDeg = 0.1f, SharpLength = 480f,
			Projectile = ProjectileID.BulletHighVelocity, FireVelocity = 24f, Recoil = 6f, Sound = SoundID.Item40,
		};
		protected override int Damage => 70;
		protected override int Rarity => ItemRarityID.Orange;
		protected override int ValueCopper => Item.buyPrice(gold: 4);
		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 6).AddIngredient(ItemID.DemoniteBar, 12).AddIngredient(ItemID.Lens, 4).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Rotary machine gun (CC ActivationDelay): it has to spin up before it fires, then pours out rounds.</summary>
	public class Spinner : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 1200, Magazine = 150, ReloadTicks = 210, ActivationDelay = 36, ShakeDeg = 7f, SharpShakeDeg = 5f, SharpLength = 220f,
			FireVelocity = 15f, Recoil = 1.2f,
		};
		protected override int Damage => 10;
		protected override int Rarity => ItemRarityID.Orange;
		protected override int ValueCopper => Item.buyPrice(gold: 5);
		public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.HellstoneBar, 18).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Rocket launcher (CC explosive rounds): one rocket per magazine.</summary>
	public class RocketTube : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 60, FullAuto = false, Magazine = 1, ReloadTicks = 120, ShakeDeg = 2f, SharpShakeDeg = 0.6f, SharpLength = 300f,
			Projectile = ProjectileID.RocketI, FireVelocity = 8f, Recoil = 7f, Sound = SoundID.Item11,
		};
		protected override int Damage => 60;
		protected override int Rarity => ItemRarityID.LightRed;
		protected override int ValueCopper => Item.buyPrice(gold: 8);
		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 20).AddIngredient(ItemID.ExplosivePowder, 10).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Shoots spinning saw blades that ricochet off terrain, cutting into it, and slice through several targets.</summary>
	public class Sawthrower : CCFirearm
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 180, Magazine = 10, ReloadTicks = 120, ShakeDeg = 3f, SharpShakeDeg = 1.5f, SharpLength = 200f,
			Projectile = ModContent.ProjectileType<SawBlade>(), FireVelocity = 9f, Recoil = 3f, Sound = SoundID.Item22,
		};
		protected override int Damage => 24;
		protected override int Rarity => ItemRarityID.Orange;
		protected override int ValueCopper => Item.buyPrice(gold: 4);
		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 15).AddIngredient(ItemID.Sawmill).AddTile(TileID.Anvils).Register();
	}

	// ======================================================================== diggers

	/// <summary>
	/// CC diggers fire particles that eat terrain up to their strength (here: pick power). Hold to dig along the aim; the dug
	/// material drops as items, like mining.
	/// </summary>
	public abstract class CCDigger : CCFirearm
	{
		protected abstract int Strength { get; }

		public override void SetDefaults()
		{
			base.SetDefaults();
			Item.pick = 0;   // digging is the particles' job, not vanilla's pickaxe
		}

		public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position, Vector2 velocity, int type, int damage, float knockback)
		{
			for (int k = 0; k < Spec.ParticleCount; k++) {
				Vector2 v = velocity.RotatedBy(MathHelper.ToRadians(Main.rand.NextFloat(-Spec.ParticleSpreadDeg, Spec.ParticleSpreadDeg) / 2f));
				Projectile.NewProjectile(source, position, v, type, damage, 0f, player.whoAmI, Strength);
			}
			return false;
		}
	}

	public class LightDigger : CCDigger
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 900, Magazine = 999, ReloadTicks = 30, ShakeDeg = 4f, SharpShakeDeg = 4f, SharpLength = 60f,
			ParticleCount = 2, ParticleSpreadDeg = 20f, Projectile = ModContent.ProjectileType<DigParticle>(), FireVelocity = 7f,
			Recoil = 0.2f, Sound = SoundID.Item22 with { Volume = 0.3f, Pitch = 0.5f },
		};
		protected override int Damage => 2;
		protected override int Strength => 50;
		protected override int ValueCopper => Item.buyPrice(silver: 30);
		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 6).AddIngredient(ItemID.CopperBar, 4).AddTile(TileID.Anvils).Register();
	}

	public class HeavyDigger : CCDigger
	{
		public override FirearmSpec Spec { get; } = new() {
			RateOfFire = 1200, Magazine = 999, ReloadTicks = 30, ShakeDeg = 5f, SharpShakeDeg = 5f, SharpLength = 70f,
			ParticleCount = 4, ParticleSpreadDeg = 26f, Projectile = ModContent.ProjectileType<DigParticle>(), FireVelocity = 8f,
			Recoil = 0.3f, Sound = SoundID.Item22 with { Volume = 0.35f, Pitch = 0.2f },
		};
		protected override int Damage => 3;
		protected override int Strength => 110;
		protected override int Rarity => ItemRarityID.Orange;
		protected override int ValueCopper => Item.buyPrice(gold: 3);
		public override void AddRecipes() => CreateRecipe().AddIngredient<LightDigger>().AddIngredient(ItemID.HellstoneBar, 10).AddTile(TileID.Anvils).Register();
	}

	// ======================================================================== thrown devices

	public class FragGrenade : CCThrowable
	{
		protected override int ProjectileType => ModContent.ProjectileType<FragGrenadeProjectile>();
		protected override int Damage => 45;
		public override void AddRecipes() => CreateRecipe(5).AddRecipeGroup(RecipeGroupID.IronBar).AddIngredient(ItemID.Gel, 5).AddTile(TileID.Anvils).Register();
	}

	public class ImpactGrenade : CCThrowable
	{
		protected override int ProjectileType => ModContent.ProjectileType<ImpactGrenadeProjectile>();
		protected override int Damage => 50;
		public override void AddRecipes() => CreateRecipe(5).AddRecipeGroup(RecipeGroupID.IronBar).AddIngredient(ItemID.Gel, 5).AddIngredient(ItemID.FallenStar).AddTile(TileID.Anvils).Register();
	}

	public class StickyCharge : CCThrowable
	{
		protected override int ProjectileType => ModContent.ProjectileType<StickyChargeProjectile>();
		protected override int Damage => 70;
		public override void AddRecipes() => CreateRecipe(3).AddRecipeGroup(RecipeGroupID.IronBar).AddIngredient(ItemID.Gel, 10).AddIngredient(ItemID.Bomb).AddTile(TileID.Anvils).Register();
	}

	public class ClusterGrenade : CCThrowable
	{
		protected override int ProjectileType => ModContent.ProjectileType<ClusterGrenadeProjectile>();
		protected override int Damage => 40;
		public override void AddRecipes() => CreateRecipe(3).AddIngredient<FragGrenade>(3).AddIngredient(ItemID.Bomb).AddTile(TileID.Anvils).Register();
	}

	public class RemoteCharge : CCThrowable
	{
		protected override int ProjectileType => ModContent.ProjectileType<RemoteChargeProjectile>();
		protected override int Damage => 90;
		public override void AddRecipes() => CreateRecipe(3).AddRecipeGroup(RecipeGroupID.IronBar, 2).AddIngredient(ItemID.Bomb, 2).AddIngredient(ItemID.Wire, 5).AddTile(TileID.Anvils).Register();
	}

	// ======================================================================== held tools

	/// <summary>Sets off every remote charge you've planted.</summary>
	public class RemoteDetonator : ModItem
	{
		public override void SetDefaults()
		{
			Item.width = 14;
			Item.height = 20;
			Item.useStyle = ItemUseStyleID.HoldUp;
			Item.useTime = Item.useAnimation = 20;
			Item.value = Item.buyPrice(silver: 20);
			Item.rare = ItemRarityID.Blue;
		}

		public override bool? UseItem(Player player)
		{
			if (player.whoAmI == Main.myPlayer) {
				int n = CCGrenade.DetonateAll(player.whoAmI);
				SoundEngine.PlaySound(SoundID.Item108 with { Volume = 0.6f }, player.Center);
				Common.Testing.AutoTest.Trace($"detonator: {n} charges");
			}
			return true;
		}

		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 3).AddIngredient(ItemID.Wire, 10).AddTile(TileID.Anvils).Register();
	}

	/// <summary>
	/// A shield held in the hands (CC shields are held devices that soak fire): hostile projectiles hitting it from the front are
	/// stopped. Can't fire while holding it.
	/// </summary>
	public class RiotShield : ModItem
	{
		public override void SetDefaults()
		{
			Item.width = 16;
			Item.height = 32;
			Item.useStyle = ItemUseStyleID.HoldUp;   // held, not swung
			Item.useTime = Item.useAnimation = 20;
			Item.value = Item.buyPrice(gold: 1);
			Item.rare = ItemRarityID.Blue;
			Item.defense = 0;
		}

		public override bool CanUseItem(Player player) => false;

		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 16).AddIngredient(ItemID.Glass, 10).AddTile(TileID.Anvils).Register();
	}

	/// <summary>Patches a body up: heals and clears its wounds.</summary>
	public class Medikit : ModItem
	{
		public override void SetDefaults()
		{
			Item.width = Item.height = 18;
			Item.useStyle = ItemUseStyleID.HoldUp;
			Item.useTime = Item.useAnimation = 45;
			Item.consumable = true;
			Item.maxStack = 30;
			Item.UseSound = SoundID.Item4;
			Item.value = Item.buyPrice(silver: 25);
			Item.rare = ItemRarityID.Blue;
		}

		public override bool? UseItem(Player player)
		{
			player.Heal(60);
			player.GetModPlayer<DamagePlayer>().Wounds.Clear();
			return true;
		}

		public override void AddRecipes() => CreateRecipe(2).AddIngredient(ItemID.LesserHealingPotion, 2).AddIngredient(ItemID.Silk, 2).AddTile(TileID.WorkBenches).Register();
	}
}
