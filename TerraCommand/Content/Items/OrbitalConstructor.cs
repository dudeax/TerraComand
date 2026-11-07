using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Orbital;

namespace TerraCommand.Content.Items
{
	/// <summary>Places the block or wall selected in the orbital menu, paid for from the shared stockpile.</summary>
	public class OrbitalConstructor : ModItem
	{
		public override void SetDefaults()
		{
			Item.width = 28;
			Item.height = 28;
			Item.useStyle = ItemUseStyleID.Swing;
			Item.useTime = 8;
			Item.useAnimation = 8;
			Item.autoReuse = true;
			Item.noMelee = true;
			Item.rare = ItemRarityID.Green;
			Item.value = Item.buyPrice(gold: 1);
			Item.tileBoost = 12;
		}

		public override bool? UseItem(Player player)
		{
			if (player.whoAmI != Main.myPlayer)
				return true;
			var mp = player.GetModPlayer<OrbitalPlayer>();
			if (mp.BuildKey is not StockKey key || OrbitalStockpile.Count(key) < 1)
				return true;
			Point tile = Main.MouseWorld.ToTileCoordinates();
			if (Vector2.Distance(player.Center, Main.MouseWorld) > 16 * 40)
				return true;
			OrbitalNet.RequestBuild(tile.X, tile.Y, key);
			return true;
		}

		public override void ModifyTooltips(System.Collections.Generic.List<TooltipLine> tooltips)
		{
			var mp = Main.LocalPlayer.GetModPlayer<OrbitalPlayer>();
			string sel = mp.BuildKey is StockKey k
				? $"{Lang.GetItemNameValue(k.Type)} ({OrbitalStockpile.Count(k)} in orbit)"
				: "none - right-click a block in the orbital menu";
			tooltips.Add(new TooltipLine(Mod, "BuildSel", "Building with: " + sel));
		}

		public override void AddRecipes()
		{
			CreateRecipe().AddRecipeGroup(RecipeGroupID.IronBar, 5).AddIngredient(ItemID.FallenStar, 1).AddTile(TileID.Anvils).Register();
		}
	}
}
