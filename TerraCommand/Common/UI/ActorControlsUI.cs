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
using TerraCommand.Common.Actors;
using TerraCommand.Common.Orbital;
using TerraCommand.Common.Physics;
using TerraCommand.Common.Units;

namespace TerraCommand.Common.UI
{
	/// <summary>
	/// Cortex Command controls UI: the pie menu (hold the right mouse button, point at a command, release) and the HUD drawn
	/// next to the controlled body (held item, health, jetpack fuel, the carried-items list after switching, pick-up hint).
	/// Terraria's hotbar is hidden: the body carries a CC inventory (<see cref="ActorInventory"/>).
	/// </summary>
	public class ActorControlsUI : ModSystem
	{
		// ------------------------------------------------------------ pie menu

		public record Slice(string Label, Func<bool> Enabled, Action Run);

		public static bool PieOpen { get; private set; }
		static Vector2 pieCenter;          // screen (UI) coordinates
		static int hovered = -1;
		static bool rightWasDown;

		static Player Me => Main.LocalPlayer;

		public static readonly Slice[] Slices = {
			new("Orbital menu", () => true, () => OrbitalUISystem.Toggle()),
			new("Launch ship", () => ShipSystem.HasLandedShip(Me.team), () => ShipSystem.RequestLaunch(Me)),
			new("Next item", () => true, () => Me.GetModPlayer<ActorInventory>().Next()),
			new("Drop", () => !Me.inventory[ActorInventory.Held].IsAir, () => Me.GetModPlayer<ActorInventory>().DropHeld()),
			new("Pick up", () => Me.GetModPlayer<ActorInventory>().ItemInReach >= 0, () => Me.GetModPlayer<ActorInventory>().PickUp()),
			new("Previous item", () => true, () => Me.GetModPlayer<ActorInventory>().Prev()),
			new("Units: hold / follow", () => UnitSystem.TeamUnits(Me.team).Any(), () => Me.GetModPlayer<UnitPlayer>().CycleOrders()),
			new("Switch body", () => UnitSystem.TeamUnits(Me.team).Any(), () => Me.GetModPlayer<UnitPlayer>().SwitchBody()),
		};

		public override void ClearWorld()
		{
			PieOpen = false;
			hovered = -1;
		}

		public override void PostUpdateInput()
		{
			if (Main.gameMenu || Main.dedServ)
				return;
			bool down = Main.mouseRight;
			bool blocked = Main.playerInventory || Main.mapFullscreen || OrbitalUISystem.Visible || ShipSystem.PickingLz
				|| Me.talkNPC >= 0 || Me.dead || Main.drawingPlayerChat;
			if (down && !rightWasDown && !blocked && !Me.mouseInterface) {
				PieOpen = true;
				pieCenter = new Vector2(Main.mouseX, Main.mouseY);
				hovered = -1;
				SoundEngine.PlaySound(SoundID.MenuOpen with { Volume = 0.5f });
			}
			if (PieOpen && !testHold) {
				Vector2 d = new Vector2(Main.mouseX, Main.mouseY) - pieCenter;
				hovered = d.Length() < 18f ? -1 : SliceAt(d);
				if (!down) {
					PieOpen = false;
					if (hovered >= 0 && Slices[hovered].Enabled())
						Activate(hovered);
				}
				Main.mouseRightRelease = false;
			}
			rightWasDown = down;
		}

		/// <summary>Slice 0 is straight up; they go clockwise in 45 degree steps.</summary>
		static int SliceAt(Vector2 d)
		{
			float a = MathF.Atan2(d.X, -d.Y);                    // 0 = up, clockwise positive
			int i = (int)MathF.Round(a / MathHelper.PiOver4);
			return ((i % Slices.Length) + Slices.Length) % Slices.Length;
		}

		public static void Activate(int index)
		{
			SoundEngine.PlaySound(SoundID.MenuTick);
			Testing.AutoTest.Trace($"pie: {Slices[index].Label}");
			Slices[index].Run();
		}

		/// <summary>For tests: show the pie menu open (around the screen centre) with a slice hovered; -1 closes it.</summary>
		public static void ShowForTest(int hover)
		{
			PieOpen = hover >= 0;
			testHold = PieOpen;
			pieCenter = new Vector2(Main.screenWidth, Main.screenHeight) / 2f / Main.UIScale;
			hovered = hover;
		}

		static bool testHold;

		/// <summary>For tests: run a pie command by label.</summary>
		public static bool Activate(string label)
		{
			int i = Array.FindIndex(Slices, s => s.Label.StartsWith(label, StringComparison.OrdinalIgnoreCase));
			if (i < 0 || !Slices[i].Enabled())
				return false;
			Activate(i);
			return true;
		}

		// ------------------------------------------------------------ interface

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			// the hotbar doesn't exist for a CC body: it carries one item in its hands and the rest in a queue
			GameInterfaceLayer hotbar = layers.FirstOrDefault(l => l.Name == "Vanilla: Hotbar");
			if (hotbar != null)
				hotbar.Active = false;
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Mouse Text");
			if (idx < 0)
				idx = layers.Count;
			layers.Insert(idx, new LegacyGameInterfaceLayer("TerraCommand: Actor HUD", () => {
				if (!Main.gameMenu && !Me.dead && !Main.mapFullscreen)
					DrawHud(Main.spriteBatch);
				if (PieOpen)
					DrawPie(Main.spriteBatch);
				return true;
			}, InterfaceScaleType.UI));
		}

		static Vector2 WorldToUi(Vector2 world) =>
			Vector2.Transform(world - Main.screenPosition, Main.GameViewMatrix.ZoomMatrix) / Main.UIScale;

		static void DrawPie(SpriteBatch sb)
		{
			var font = FontAssets.MouseText.Value;
			Texture2D px = TextureAssets.MagicPixel.Value;
			sb.Draw(px, new Rectangle((int)pieCenter.X - 3, (int)pieCenter.Y - 3, 6, 6), Color.White * 0.8f);
			for (int i = 0; i < Slices.Length; i++) {
				float a = i * MathHelper.PiOver4;
				Vector2 at = pieCenter + new Vector2(MathF.Sin(a) * 1.35f, -MathF.Cos(a)) * 92f;
				bool on = Slices[i].Enabled();
				bool hot = i == hovered;
				string label = Slices[i].Label;
				float scale = hot ? 0.95f : 0.8f;
				Vector2 size = font.MeasureString(label) * scale;
				var box = new Rectangle((int)(at.X - size.X / 2 - 6), (int)(at.Y - size.Y / 2 - 3), (int)size.X + 12, (int)size.Y + 4);
				Utils.DrawInvBG(sb, box, hot && on ? new Color(200, 140, 40, 230) : on ? new Color(40, 50, 80, 210) : new Color(40, 40, 40, 160));
				Utils.DrawBorderString(sb, label, at - size / 2f, on ? (hot ? Color.White : Color.LightGray) : Color.Gray, scale);
			}
		}

		static void DrawHud(SpriteBatch sb)
		{
			Player p = Me;
			var inv = p.GetModPlayer<ActorInventory>();
			var body = p.GetModPlayer<BodyPhysicsPlayer>();
			Vector2 head = WorldToUi(p.Top + new Vector2(0f, -6f));
			Vector2 side = WorldToUi(p.Center + new Vector2(-p.direction * 22f, 0f));
			var font = FontAssets.MouseText.Value;
			Texture2D px = TextureAssets.MagicPixel.Value;

			// jetpack fuel: a slim bar beside the body while it isn't full
			if (body.FuelMax > 0f && body.Fuel < body.FuelMax - 0.5f) {
				float f = body.Fuel / body.FuelMax;
				var back = new Rectangle((int)side.X - 2, (int)side.Y - 16, 4, 32);
				sb.Draw(px, back, Color.Black * 0.6f);
				int h = (int)(30 * f);
				sb.Draw(px, new Rectangle(back.X + 1, back.Bottom - 1 - h, 2, h), Color.Lerp(Color.OrangeRed, Color.LightSkyBlue, f));
			}

			// held item and health over the head
			Item held = p.inventory[ActorInventory.Held];
			var aim = p.GetModPlayer<Combat.AimPlayer>();
			string top = held.IsAir ? "(empty hands)" : held.stack > 1 ? $"{held.Name} ({held.stack})" : held.Name;
			Color topColor = Color.White;
			if (Combat.Devices.IsFirearm(held)) {
				int rounds = held.GetGlobalItem<Combat.MagazineGlobalItem>().Loaded(held);
				top = aim.Reloading ? $"{held.Name}  reloading" : $"{held.Name}  {rounds}/{Combat.Devices.Stats(held).Magazine}";
				topColor = aim.Reloading || rounds == 0 ? Color.Orange : Color.White;
				if (aim.Reloading) {
					// reload progress under the name
					float f = 1f - aim.ReloadTicks / (float)Math.Max(1, aim.ReloadTotal);
					var bar = new Rectangle((int)head.X - 20, (int)head.Y - 12, 40, 3);
					sb.Draw(px, bar, Color.Black * 0.6f);
					sb.Draw(px, new Rectangle(bar.X, bar.Y, (int)(40 * f), 3), Color.Orange);
				}
			}
			else if (aim.ThrowCharge > 0f) {
				var bar = new Rectangle((int)head.X - 20, (int)head.Y - 12, 40, 3);
				sb.Draw(px, bar, Color.Black * 0.6f);
				sb.Draw(px, new Rectangle(bar.X, bar.Y, (int)(40 * aim.ThrowCharge), 3), Color.Gold);
			}
			Center(sb, font, top, head - new Vector2(0f, 14f), topColor, 0.7f);
			Center(sb, font, $"{p.statLife}", head, p.statLife < p.statLifeMax2 * 0.3f ? Color.OrangeRed : new Color(140, 230, 140), 0.7f);

			// the carried queue, briefly, after switching or picking something up
			if (inv.EquipHudTicks > 0) {
				float alpha = Math.Min(1f, inv.EquipHudTicks / 30f);
				var names = new List<string>();
				for (int i = 1; i < 50; i++)
					if (ActorInventory.IsDevice(p.inventory[i]))
						names.Add(p.inventory[i].Name);
				for (int k = 0; k < names.Count && k < 6; k++)
					Center(sb, font, names[k], head - new Vector2(0f, 30f + k * 14f), Color.LightGray * alpha, 0.6f);
				if (names.Count > 6)
					Center(sb, font, $"+{names.Count - 6} more", head - new Vector2(0f, 30f + 6 * 14f), Color.Gray * alpha, 0.6f);
			}

			// what F would pick up
			if (inv.ItemInReach >= 0 && Main.item[inv.ItemInReach].active) {
				Item it = Main.item[inv.ItemInReach];
				Vector2 at = WorldToUi(it.Top - new Vector2(0f, 10f));
				string key = TerraCommandKeybinds.Pickup?.GetAssignedKeys().FirstOrDefault() ?? "F";
				Center(sb, font, $"{key}: pick up {it.Name}", at, Color.Gold, 0.65f);
			}
		}

		static void Center(SpriteBatch sb, ReLogic.Graphics.DynamicSpriteFont font, string s, Vector2 at, Color c, float scale)
		{
			Vector2 size = font.MeasureString(s) * scale;
			Utils.DrawBorderString(sb, s, at - new Vector2(size.X / 2f, size.Y), c, scale);
		}
	}

	/// <summary>While the pie menu is open the body doesn't fire or turn to follow the cursor.</summary>
	public class PieMenuPlayer : ModPlayer
	{
		public override void SetControls()
		{
			if (ActorControlsUI.PieOpen && Player.whoAmI == Main.myPlayer)
				Player.controlUseItem = false;
		}
	}
}
