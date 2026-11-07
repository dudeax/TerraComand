using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Units;

namespace TerraCommand.Content.Items
{
	/// <summary>Deploys a new, empty body at the cursor that you (or teammates) can take control of.</summary>
	public class CloneBody : ModItem
	{
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
			Item.value = Item.buyPrice(silver: 40);
		}

		public override bool CanUseItem(Player player)
		{
			Vector2 m = Main.MouseWorld;
			return Vector2.Distance(player.Center, m) < 16 * 20 && !Collision.SolidCollision(m - new Vector2(9, 40), 18, 40);
		}

		public override bool? UseItem(Player player)
		{
			if (player.whoAmI == Main.myPlayer)
				UnitSystem.RequestDeploy(player, Main.MouseWorld);
			return true;
		}

		public override void AddRecipes()
		{
			CreateRecipe()
				.AddIngredient(ItemID.Gel, 20)
				.AddRecipeGroup(RecipeGroupID.IronBar, 4)
				.AddIngredient(ItemID.Bottle, 1)
				.AddTile(TileID.Anvils)
				.Register();
		}
	}
}
