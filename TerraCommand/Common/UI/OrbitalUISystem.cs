using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using TerraCommand.Common.Orbital;

namespace TerraCommand.Common.UI
{
	/// <summary>
	/// Orbital command menu: the shared stockpile (left), recipes craftable from it (right) and a drop-pod
	/// manifest (bottom). Drawn immediate-mode in a legacy interface layer.
	///   Stockpile: left-click = add to manifest (shift: whole stack), right-click = select for the Orbital Constructor
	///   Recipes:   left-click = craft 1 (shift: 10)
	///   Manifest:  left-click an entry to remove it
	/// </summary>
	public class OrbitalUISystem : ModSystem
	{
		public static bool Visible;
		static int stockScroll, recipeScroll;
		static readonly List<(StockKey key, int count)> manifest = new();
		static List<StockKey> sortedStock = new();
		static List<int> recipes = new();
		static int cachedVersion = -1;
		static uint lastRecipeRefresh;

		const int Slot = 40, Cols = 8, Rows = 7, RecipeRows = 7, Pad = 10;

		public static void Toggle()
		{
			Visible = !Visible;
			Terraria.Audio.SoundEngine.PlaySound(Visible ? SoundID.MenuOpen : SoundID.MenuClose);
		}

		public override void ClearWorld()
		{
			Visible = false;
			manifest.Clear();
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Mouse Text");
			if (idx < 0)
				idx = layers.Count;
			layers.Insert(idx, new LegacyGameInterfaceLayer("TerraCommand: Orbital Menu", () => {
				if (Visible && !Main.gameMenu)
					Draw(Main.spriteBatch);
				return true;
			}, InterfaceScaleType.UI));
		}

		static void Refresh()
		{
			if (cachedVersion != OrbitalStockpile.Version) {
				sortedStock = OrbitalStockpile.Stock.Keys
					.OrderBy(k => ContentSamples.ItemsByType[k.Type].createTile < 0 && ContentSamples.ItemsByType[k.Type].createWall <= 0)
					.ThenBy(k => k.Type).ThenBy(k => k.Prefix).ToList();
				manifest.RemoveAll(m => OrbitalStockpile.Count(m.key) <= 0);
				cachedVersion = OrbitalStockpile.Version;
				lastRecipeRefresh = 0;
			}
			if (Main.GameUpdateCount - lastRecipeRefresh > 60 || lastRecipeRefresh == 0) {
				recipes = OrbitalCrafting.CraftableRecipeIndices().ToList();
				lastRecipeRefresh = Math.Max(1u, Main.GameUpdateCount);
			}
		}

		static bool Clicked(Rectangle r, bool right = false)
		{
			if (!r.Contains(Main.MouseScreen.ToPoint()))
				return false;
			if (!right && Main.mouseLeft && Main.mouseLeftRelease) { Main.mouseLeftRelease = false; return true; }
			if (right && Main.mouseRight && Main.mouseRightRelease) { Main.mouseRightRelease = false; return true; }
			return false;
		}

		static void DrawSlot(SpriteBatch sb, Rectangle r, Item item, long count, bool highlight)
		{
			Utils.DrawInvBG(sb, r, highlight ? new Color(90, 140, 60, 220) : new Color(40, 50, 90, 200));
			if (item != null && !item.IsAir) {
				Main.DrawItemIcon(sb, item, r.Center.ToVector2(), Color.White, Slot - 12);
				if (count > 1)
					Utils.DrawBorderString(sb, FormatCount(count), new Vector2(r.X + 4, r.Bottom - 16), Color.White, 0.7f);
				if (r.Contains(Main.MouseScreen.ToPoint())) {
					Main.HoverItem = item.Clone();
					Main.hoverItemName = item.HoverName;
				}
			}
		}

		static string FormatCount(long n) => n >= 1_000_000 ? $"{n / 1_000_000}M" : n >= 10_000 ? $"{n / 1000}k" : n.ToString();

		static void Draw(SpriteBatch sb)
		{
			Refresh();
			int stockW = Cols * Slot, recipeW = 5 * Slot;
			int w = Pad * 3 + stockW + recipeW, h = Pad * 4 + 24 + Rows * Slot + Slot + 46;
			var panel = new Rectangle(Main.screenWidth / 2 - w / 2, Main.screenHeight / 2 - h / 2, w, h);
			Utils.DrawInvBG(sb, panel, new Color(20, 25, 50, 230));
			bool hover = panel.Contains(Main.MouseScreen.ToPoint());
			if (hover) {
				Main.LocalPlayer.mouseInterface = true;
				PlayerInput.LockVanillaMouseScroll("TerraCommand/OrbitalMenu");
			}

			var font = FontAssets.MouseText.Value;
			Utils.DrawBorderString(sb, "Orbital Stockpile", new Vector2(panel.X + Pad, panel.Y + Pad), Color.Gold);
			Utils.DrawBorderString(sb, "Fabricate", new Vector2(panel.X + Pad * 2 + stockW, panel.Y + Pad), Color.Gold);

			// ---- stockpile grid
			var grid = new Rectangle(panel.X + Pad, panel.Y + Pad + 28, stockW, Rows * Slot);
			int stockRowsTotal = (sortedStock.Count + Cols - 1) / Cols;
			if (grid.Contains(Main.MouseScreen.ToPoint()) && PlayerInput.ScrollWheelDeltaForUI != 0)
				stockScroll = Math.Clamp(stockScroll - Math.Sign(PlayerInput.ScrollWheelDeltaForUI), 0, Math.Max(0, stockRowsTotal - Rows));
			var orbital = Main.LocalPlayer.GetModPlayer<OrbitalPlayer>();
			for (int n = 0; n < Rows * Cols; n++) {
				int idx = stockScroll * Cols + n;
				var r = new Rectangle(grid.X + n % Cols * Slot, grid.Y + n / Cols * Slot, Slot - 2, Slot - 2);
				if (idx >= sortedStock.Count) {
					DrawSlot(sb, r, null, 0, false);
					continue;
				}
				StockKey k = sortedStock[idx];
				long count = OrbitalStockpile.Count(k);
				var item = new Item(k.Type);
				if (k.Prefix > 0)
					item.Prefix(k.Prefix);
				DrawSlot(sb, r, item, count, orbital.BuildKey == k);
				if (Clicked(r))
					AddToManifest(k, Main.keyState.PressingShift() ? (int)Math.Min(count, int.MaxValue) : 1);
				if (Clicked(r, right: true) && (item.createTile >= TileID.Dirt || item.createWall > 0)) {
					orbital.BuildKey = orbital.BuildKey == k ? null : k;
					Terraria.Audio.SoundEngine.PlaySound(SoundID.MenuTick);
				}
			}

			// ---- recipes
			var rlist = new Rectangle(panel.X + Pad * 2 + stockW, grid.Y, recipeW, Rows * Slot);
			if (rlist.Contains(Main.MouseScreen.ToPoint()) && PlayerInput.ScrollWheelDeltaForUI != 0)
				recipeScroll = Math.Clamp(recipeScroll - Math.Sign(PlayerInput.ScrollWheelDeltaForUI), 0, Math.Max(0, recipes.Count - RecipeRows));
			recipeScroll = Math.Clamp(recipeScroll, 0, Math.Max(0, recipes.Count - RecipeRows));
			for (int n = 0; n < RecipeRows; n++) {
				int idx = recipeScroll + n;
				var row = new Rectangle(rlist.X, rlist.Y + n * Slot, recipeW, Slot - 2);
				if (idx >= recipes.Count) {
					Utils.DrawInvBG(sb, row, new Color(30, 35, 70, 160));
					continue;
				}
				Recipe rec = Main.recipe[recipes[idx]];
				var iconR = new Rectangle(row.X, row.Y, Slot - 2, Slot - 2);
				DrawSlot(sb, iconR, rec.createItem, rec.createItem.stack, false);
				// ingredient icons
				int ix = row.X + Slot + 2;
				foreach (Item req in rec.requiredItem.Take(4)) {
					Main.DrawItemIcon(sb, req, new Vector2(ix + 12, row.Center.Y), Color.White * 0.9f, 22);
					if (req.stack > 1)
						Utils.DrawBorderString(sb, req.stack.ToString(), new Vector2(ix + 2, row.Center.Y + 2), Color.White, 0.6f);
					ix += 30;
				}
				if (Clicked(row)) {
					OrbitalNet.RequestCraft(recipes[idx], Main.keyState.PressingShift() ? 10 : 1);
					Terraria.Audio.SoundEngine.PlaySound(SoundID.Grab);
				}
			}

			// ---- manifest + order button
			int my = grid.Bottom + Pad;
			Utils.DrawBorderString(sb, "Drop pod:", new Vector2(panel.X + Pad, my + 10), Color.LightBlue, 0.85f);
			int mx = panel.X + Pad + 90;
			for (int n = 0; n < manifest.Count && n < 8; n++) {
				var r = new Rectangle(mx + n * Slot, my, Slot - 2, Slot - 2);
				var (k, c) = manifest[n];
				DrawSlot(sb, r, new Item(k.Type), c, false);
				if (Clicked(r)) {
					manifest.RemoveAt(n);
					break;
				}
			}
			var orderBtn = new Rectangle(panel.Right - Pad - 150, my + 4, 150, 30);
			bool canOrder = manifest.Count > 0;
			Utils.DrawInvBG(sb, orderBtn, canOrder ? new Color(140, 70, 30, 230) : new Color(60, 60, 60, 200));
			Utils.DrawBorderString(sb, "Order drop pod", orderBtn.Center.ToVector2() - font.MeasureString("Order drop pod") * 0.4f, Color.White, 0.8f);
			if (canOrder && Clicked(orderBtn)) {
				OrbitalNet.RequestDropPod(manifest.ToList(), Main.LocalPlayer.Center);
				manifest.Clear();
				Main.NewText("Drop pod inbound.", Color.Orange);
			}

			Utils.DrawBorderString(sb, "Stockpile: L-click add to drop pod (Shift: whole stack), R-click a block to build with it",
				new Vector2(panel.X + Pad, panel.Bottom - 40), Color.Gray, 0.65f);
			Utils.DrawBorderString(sb, "Fabricate: click to craft one (Shift: x10)",
				new Vector2(panel.X + Pad, panel.Bottom - 22), Color.Gray, 0.65f);
		}

		static void AddToManifest(StockKey k, int count)
		{
			long have = OrbitalStockpile.Count(k);
			int i = manifest.FindIndex(m => m.key == k);
			if (i >= 0)
				manifest[i] = (k, (int)Math.Min(have, (long)manifest[i].count + count));
			else if (manifest.Count < 8)
				manifest.Add((k, (int)Math.Min(have, count)));
			Terraria.Audio.SoundEngine.PlaySound(SoundID.MenuTick);
		}
	}
}
