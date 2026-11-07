using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerraCommand.Common.Orbital;

namespace TerraCommand.Content.Tiles
{
	/// <summary>
	/// Sends items to the orbital stockpile. Right-click with an item (cursor or held) to launch it, or
	/// simply throw items onto the pad and it launches them automatically.
	/// </summary>
	public class LaunchPad : ModTile
	{
		public override void SetStaticDefaults()
		{
			Main.tileFrameImportant[Type] = true;
			Main.tileSolidTop[Type] = true;
			Main.tileTable[Type] = true;
			Main.tileNoAttach[Type] = true;
			Main.tileLavaDeath[Type] = false;
			TileID.Sets.HasOutlines[Type] = false;
			TileID.Sets.DisableSmartCursor[Type] = true;

			TileObjectData.newTile.CopyFrom(TileObjectData.Style3x2);
			TileObjectData.newTile.CoordinateHeights = new[] { 16, 16 };
			TileObjectData.newTile.HookPostPlaceMyPlayer = ModContent.GetInstance<LaunchPadEntity>().Generic_HookPostPlaceMyPlayer;
			TileObjectData.newTile.AnchorBottom = new AnchorData(AnchorType.SolidTile | AnchorType.SolidWithTop, 3, 0);
			TileObjectData.addTile(Type);

			DustType = DustID.Iron;
			AddMapEntry(new Color(240, 200, 40), CreateMapEntryName());
		}

		public override void KillMultiTile(int i, int j, int frameX, int frameY)
			=> ModContent.GetInstance<LaunchPadEntity>().Kill(i, j);

		public override void MouseOver(int i, int j)
		{
			Player p = Main.LocalPlayer;
			p.noThrow = 2;
			p.cursorItemIconEnabled = true;
			p.cursorItemIconID = ModContent.ItemType<LaunchPadItem>();
		}

		public override bool RightClick(int i, int j)
		{
			Player p = Main.LocalPlayer;
			Point16 origin = TopLeft(i, j);
			var padTop = new Vector2((origin.X + 1.5f) * 16, origin.Y * 16);

			ref Item source = ref Main.mouseItem;
			if (source.IsAir) {
				if (p.selectedItem >= 58 || p.HeldItem.IsAir || p.HeldItem.favorited || p.HeldItem.ModItem is LaunchPadItem)
					return false;
				source = ref p.inventory[p.selectedItem];
			}
			OrbitalNet.RequestLaunch(source.Clone(), padTop);
			source.TurnToAir();
			Terraria.Audio.SoundEngine.PlaySound(SoundID.Item13, padTop);
			return true;
		}

		public static Point16 TopLeft(int i, int j)
		{
			Tile t = Main.tile[i, j];
			return new Point16(i - t.TileFrameX % 54 / 18, j - t.TileFrameY / 18);
		}
	}

	public class LaunchPadEntity : ModTileEntity
	{
		int timer;

		public override bool IsTileValidForEntity(int x, int y)
		{
			Tile t = Main.tile[x, y];
			return t.HasTile && t.TileType == ModContent.TileType<LaunchPad>() && t.TileFrameX % 54 == 0 && t.TileFrameY == 0;
		}

		public override int Hook_AfterPlacement(int i, int j, int type, int style, int direction, int alternate)
		{
			TileObjectData data = TileObjectData.GetTileData(type, style, alternate);
			int x = i - data.Origin.X, y = j - data.Origin.Y;
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				NetMessage.SendTileSquare(Main.myPlayer, x, y, data.Width, data.Height);
				NetMessage.SendData(MessageID.TileEntityPlacement, number: x, number2: y, number3: Type);
				return -1;
			}
			return Place(x, y);
		}

		public override void OnNetPlace()
		{
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendData(MessageID.TileEntitySharing, number: ID, number2: Position.X, number3: Position.Y);
		}

		/// <summary>Runs on the server / singleplayer: launches items resting on the pad.</summary>
		public override void Update()
		{
			if (++timer < 45)
				return;
			timer = 0;
			// Generous catch area: a tile past each edge and four tiles up, so tossed stacks don't bounce off.
			var area = new Rectangle((Position.X - 1) * 16, (Position.Y - 4) * 16, 5 * 16, 6 * 16);
			bool launched = false;
			int iconType = 0;
			for (int n = 0; n < Main.maxItems; n++) {
				Item it = Main.item[n];
				if (!it.active || it.IsAir || !area.Intersects(it.Hitbox))
					continue;
				OrbitalStockpile.Add(it);
				iconType = it.type;
				it.TurnToAir();
				it.active = false;
				if (Main.netMode == NetmodeID.Server)
					NetMessage.SendData(MessageID.SyncItem, number: n);
				launched = true;
			}
			if (launched)
				Content.Projectiles.LaunchRocket.Spawn(new Vector2((Position.X + 1.5f) * 16, Position.Y * 16), iconType);
		}
	}

	public class LaunchPadItem : ModItem
	{
		public override void SetDefaults()
		{
			Item.DefaultToPlaceableTile(ModContent.TileType<LaunchPad>());
			Item.width = 32;
			Item.height = 20;
			Item.value = Item.buyPrice(silver: 50);
			Item.rare = ItemRarityID.Blue;
		}

		public override void AddRecipes()
		{
			CreateRecipe()
				.AddRecipeGroup(RecipeGroupID.Wood, 20)
				.AddIngredient(ItemID.StoneBlock, 20)
				.AddIngredient(ItemID.Torch, 5)
				.AddTile(TileID.WorkBenches)
				.Register();
		}
	}
}
