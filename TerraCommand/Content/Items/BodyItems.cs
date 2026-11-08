using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Units;

namespace TerraCommand.Content.Items
{
	/// <summary>
	/// A body, packed: used at the cursor it deploys an idle body of its kind (which you, or teammates, can take control of);
	/// shipped down in a cargo ship it steps out as one. Bodies sent back up to orbit come back as these.
	/// </summary>
	public abstract class BodyItem : ModItem
	{
		public abstract ActorKind Kind { get; }
		/// <summary>A weapon built into the body (drones and turrets have mounted guns).</summary>
		public virtual int BuiltInWeapon => ItemID.None;
		protected virtual int ValueCopper => Item.buyPrice(gold: 1);

		public override void SetDefaults()
		{
			Item.width = 22;
			Item.height = 30;
			Item.maxStack = Item.CommonMaxStack;
			Item.consumable = true;
			Item.useStyle = ItemUseStyleID.HoldUp;
			Item.useTime = 30;
			Item.useAnimation = 30;
			Item.UseSound = SoundID.Item44;
			Item.rare = ItemRarityID.Blue;
			Item.value = ValueCopper;
		}

		public override bool CanUseItem(Player player)
		{
			Vector2 m = Main.MouseWorld;
			return Vector2.Distance(player.Center, m) < 16 * 20 && !Collision.SolidCollision(m - new Vector2(9, 40), 18, 40);
		}

		public override bool? UseItem(Player player)
		{
			if (player.whoAmI == Main.myPlayer)
				UnitSystem.RequestDeploy(player, Main.MouseWorld, Kind, BuiltInWeapon);
			return true;
		}

		/// <summary>The packed-body item for an actor kind (the brain has none: it can't be shipped).</summary>
		public static int ItemFor(ActorKind kind) => kind switch {
			ActorKind.Light => ModContent.ItemType<LightBody>(),
			ActorKind.Heavy => ModContent.ItemType<HeavyBody>(),
			ActorKind.Crab => ModContent.ItemType<CrabDrone>(),
			ActorKind.Turret => ModContent.ItemType<TurretKit>(),
			_ => ModContent.ItemType<CloneBody>(),
		};
	}

	/// <summary>The standard soldier body.</summary>
	public class CloneBody : BodyItem
	{
		public override ActorKind Kind => ActorKind.Soldier;

		public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.Gel, 20).AddRecipeGroup(RecipeGroupID.IronBar, 4)
			.AddIngredient(ItemID.Bottle).AddTile(TileID.Anvils).Register();
	}

	/// <summary>A light soldier: quick on its feet, a bigger jetpack tank, less armour.</summary>
	public class LightBody : BodyItem
	{
		public override ActorKind Kind => ActorKind.Light;
		protected override int ValueCopper => Item.buyPrice(silver: 70);

		public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.Gel, 20).AddRecipeGroup(RecipeGroupID.IronBar, 3)
			.AddIngredient(ItemID.Feather, 3).AddTile(TileID.Anvils).Register();
	}

	/// <summary>A heavy soldier: slow and tough, a powerful jetpack that burns out fast.</summary>
	public class HeavyBody : BodyItem
	{
		public override ActorKind Kind => ActorKind.Heavy;
		protected override int ValueCopper => Item.buyPrice(gold: 2);

		public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.Gel, 20).AddRecipeGroup(RecipeGroupID.IronBar, 12)
			.AddIngredient(ItemID.Bottle).AddTile(TileID.Anvils).Register();
	}

	/// <summary>A low, four-legged drone with a mounted gun. No jetpack.</summary>
	public class CrabDrone : BodyItem
	{
		public override ActorKind Kind => ActorKind.Crab;
		public override int BuiltInWeapon => ModContent.ItemType<Devices.ServicePistol>();
		protected override int ValueCopper => Item.buyPrice(gold: 1, silver: 20);

		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 10).AddIngredient(ItemID.Wire, 10)
			.AddIngredient<Devices.ServicePistol>().AddTile(TileID.Anvils).Register();
	}

	/// <summary>A gun turret: it can't move, but it watches its arc and fires at anything hostile.</summary>
	public class TurretKit : BodyItem
	{
		public override ActorKind Kind => ActorKind.Turret;
		public override int BuiltInWeapon => ModContent.ItemType<Devices.FieldRifle>();
		protected override int ValueCopper => Item.buyPrice(gold: 1, silver: 50);

		public override void AddRecipes() => CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 14).AddIngredient(ItemID.Wire, 10)
			.AddIngredient<Devices.FieldRifle>().AddTile(TileID.Anvils).Register();
	}
}
