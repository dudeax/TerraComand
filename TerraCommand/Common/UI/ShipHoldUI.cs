using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using TerraCommand.Common.Orbital;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.UI
{
	/// <summary>
	/// A landed ship's cargo hold, opened like a chest by a body standing by it (F, or the pie menu): click an item to take it,
	/// drop the item on the cursor into it, or shift-click items in your pack to load them. What's in the hold goes up to the
	/// stockpile when the ship is launched.
	/// </summary>
	public class ShipHoldUI : ModSystem
	{
		/// <summary>The NPC index of the ship whose hold is open (-1: none).</summary>
		public static int OpenShip { get; private set; } = -1;
		const int Cols = 10, Rows = 4, Slot = 44;

		public static CargoShip NearestLandedShip(Player p) => ShipSystem.Ships()
			.Where(s => s.DoorOpen && s.Team == p.team && Vector2.Distance(s.NPC.Center, p.Center) < ShipSystem.HoldReach)
			.OrderBy(s => Vector2.DistanceSquared(s.NPC.Center, p.Center)).FirstOrDefault();

		public static void Open(CargoShip ship)
		{
			OpenShip = ship.NPC.whoAmI;
			Main.playerInventory = true;
			SoundEngine.PlaySound(SoundID.MenuOpen);
			Testing.AutoTest.Trace($"opened the hold of ship #{OpenShip}");
		}

		public static void Close()
		{
			if (OpenShip < 0)
				return;
			OpenShip = -1;
			SoundEngine.PlaySound(SoundID.MenuClose);
		}

		public override void ClearWorld() => OpenShip = -1;

		public override void PostUpdateInput()
		{
			if (OpenShip < 0 || Main.gameMenu)
				return;
			NPC n = Main.npc[OpenShip];
			Player p = Main.LocalPlayer;
			// the hold closes when the pack does, when the ship leaves, or when the body walks away
			if (!Main.playerInventory || !n.active || n.ModNPC is not CargoShip s || !s.DoorOpen || p.dead
				|| Vector2.Distance(p.Center, n.Center) > ShipSystem.HoldReach + 32f)
				Close();
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Inventory");
			if (idx < 0)
				return;
			layers.Insert(idx + 1, new LegacyGameInterfaceLayer("TerraCommand: Ship hold", () => {
				if (OpenShip >= 0 && Main.playerInventory)
					Draw(Main.spriteBatch);
				return true;
			}, InterfaceScaleType.UI));
		}

		static readonly Item[] view = new Item[Cols * Rows];

		static void Draw(SpriteBatch sb)
		{
			IReadOnlyList<Item> cargo = ShipSystem.CargoView(OpenShip);
			int x0 = 73, y0 = Math.Max(Main.instance.invBottom, 258) + 34;   // where chests open, below the pack
			var panel = new Rectangle(x0 - 8, y0 - 30, Cols * Slot + 16, Rows * Slot + 40);
			Utils.DrawInvBG(sb, panel, new Color(40, 50, 90, 220));
			Utils.DrawBorderString(sb, $"Ship hold ({cargo.Sum(i => i.stack)} items) - goes to orbit on launch", new Vector2(x0, y0 - 26), Color.LightBlue, 0.8f);
			if (Main.npc[OpenShip].ModNPC is CargoShip ship && ship.CanPilot) {
				var pilotBtn = new Rectangle(x0 + Cols * Slot - 110, y0 - 30, 110, 24);
				bool hover = pilotBtn.Contains(Main.MouseScreen.ToPoint());
				Utils.DrawInvBG(sb, pilotBtn, hover ? new Color(90, 140, 60, 230) : new Color(60, 90, 50, 220));
				Utils.DrawBorderString(sb, "Pilot ship", new Vector2(pilotBtn.X + 14, pilotBtn.Y + 4), Color.White, 0.8f);
				if (hover && Main.mouseLeft && Main.mouseLeftRelease) {
					Main.mouseLeftRelease = false;
					Main.LocalPlayer.GetModPlayer<PilotPlayer>().Board(ship);
					Main.playerInventory = false;
					Close();
					Main.NewText("Flying: move to steer, jump to thrust, Q to get out.", Color.LightBlue);
					return;
				}
			}
			if (panel.Contains(Main.MouseScreen.ToPoint()))
				Main.LocalPlayer.mouseInterface = true;

			for (int k = 0; k < Cols * Rows; k++) {
				view[k] = k < cargo.Count ? cargo[k] : new Item();
				var at = new Vector2(x0 + k % Cols * Slot, y0 + k / Cols * Slot);
				float scale = Main.inventoryScale;
				Main.inventoryScale = 0.85f;
				ItemSlot.Draw(sb, view, ItemSlot.Context.ChestItem, k, at);
				Main.inventoryScale = scale;
				var r = new Rectangle((int)at.X, (int)at.Y, (int)(52 * 0.85f), (int)(52 * 0.85f));
				if (!r.Contains(Main.MouseScreen.ToPoint()))
					continue;
				Main.LocalPlayer.mouseInterface = true;
				if (!view[k].IsAir) {
					Main.HoverItem = view[k].Clone();
					Main.hoverItemName = view[k].Name;
				}
				if (Main.mouseLeft && Main.mouseLeftRelease) {
					Main.mouseLeftRelease = false;
					if (!Main.mouseItem.IsAir) {
						// put what's on the cursor in
						ShipSystem.RequestPut(Main.LocalPlayer, OpenShip, Main.mouseItem.Clone());
						Main.mouseItem.TurnToAir();
					}
					else if (k < cargo.Count)
						ShipSystem.RequestTake(Main.LocalPlayer, OpenShip, k);
					SoundEngine.PlaySound(SoundID.Grab);
				}
			}
		}
	}

	/// <summary>Shift-clicking an item in the pack while a hold is open loads it into the ship.</summary>
	public class ShipHoldPlayer : ModPlayer
	{
		public override bool ShiftClickSlot(Item[] inventory, int context, int slot)
		{
			if (ShipHoldUI.OpenShip < 0 || context != ItemSlot.Context.InventoryItem || inventory[slot].IsAir || inventory[slot].favorited)
				return false;
			ShipSystem.RequestPut(Player, ShipHoldUI.OpenShip, inventory[slot].Clone());
			inventory[slot].TurnToAir();
			SoundEngine.PlaySound(SoundID.Grab);
			return true;
		}
	}
}
