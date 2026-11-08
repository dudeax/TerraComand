using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.UI;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Units
{
	/// <summary>Body switching controls and transferring control when the current body dies.</summary>
	public class UnitPlayer : ModPlayer
	{
		int lastSwitchedTo = -1;

		public override void ProcessTriggers(TriggersSet triggersSet)
		{
			if (TerraCommandKeybinds.SwitchBody.JustPressed)
				SwitchBody();
			if (TerraCommandKeybinds.CycleOrders.JustPressed)
				CycleOrders();
			if (TerraCommandKeybinds.OrbitalMenu.JustPressed)
				OrbitalUISystem.Toggle();
			// Esc closes the orbital menu (instead of opening the inventory)
			else if (OrbitalUISystem.Visible && PlayerInput.Triggers.JustPressed.Inventory) {
				OrbitalUISystem.Toggle();
				triggersSet.KeyStatus["Inventory"] = false;
				PlayerInput.Triggers.JustPressed.KeyStatus["Inventory"] = false;
			}
		}

		internal void SwitchBody()
		{
			var units = UnitSystem.TeamUnits(Player.team).OrderBy(u => u.NPC.whoAmI).ToList();
			if (units.Count == 0) {
				Main.NewText("No idle bodies available. Use a Clone Body to deploy one.", Color.Orange);
				return;
			}
			// Prefer the body under the cursor; otherwise cycle.
			CloneUnit target = units.FirstOrDefault(u => u.NPC.Hitbox.Contains(Main.MouseWorld.ToPoint()))
				?? units.FirstOrDefault(u => u.NPC.whoAmI > lastSwitchedTo)
				?? units[0];
			lastSwitchedTo = target.NPC.whoAmI;
			UnitSystem.RequestSwap(Player, target, leaveBody: true);
		}

		// ------------------------------------------------------------ orders and selection (client)

		/// <summary>The bodies picked by box-select (NPC indices). Orders go to these, or to every team body when empty.</summary>
		public static readonly HashSet<int> Selected = new();

		public enum Picking : byte { None, Waypoint, Box }
		public static Picking Pick;
		static Vector2? boxStart;

		public static string OrderName(UnitOrder o) => o switch {
			UnitOrder.Sentry => "sentry", UnitOrder.Follow => "follow", UnitOrder.Patrol => "patrol",
			UnitOrder.GoTo => "go to", UnitOrder.GoldDig => "gold dig", _ => "brain hunt",
		};

		/// <summary>The bodies orders apply to: the selection (pruned of the dead), or all of them.</summary>
		public IReadOnlyCollection<int> OrderTargets()
		{
			Selected.RemoveWhere(i => !Main.npc[i].active || Main.npc[i].ModNPC is not CloneUnit u || u.Team != Player.team);
			return Selected.ToArray();
		}

		public void Order(UnitOrder order)
		{
			if (order == UnitOrder.GoTo) {
				Pick = Picking.Waypoint;
				Main.NewText("Go to: left-click the point, right-click to cancel.", Color.LightBlue);
				return;
			}
			UnitSystem.RequestOrders(Player, order, default, OrderTargets());
			Main.NewText($"{(Selected.Count > 0 ? $"{Selected.Count} selected" : "All")} units: {OrderName(order)}.", Color.LightGreen);
		}

		public void BeginBoxSelect()
		{
			Pick = Picking.Box;
			Main.NewText("Select units: drag a box with the left button (right-click cancels).", Color.LightBlue);
		}

		/// <summary>Select every body inside a world rectangle (an empty box clears the selection).</summary>
		public void SelectIn(Rectangle box)
		{
			Selected.Clear();
			foreach (CloneUnit u in UnitSystem.TeamUnits(Player.team))
				if (box.Intersects(u.NPC.Hitbox))
					Selected.Add(u.NPC.whoAmI);
			Main.NewText(Selected.Count > 0 ? $"{Selected.Count} units selected." : "Selection cleared: orders go to all units.", Color.LightGreen);
			Testing.AutoTest.Trace($"selected {Selected.Count} units");
		}

		internal void CycleOrders()
		{
			var units = UnitSystem.TeamUnits(Player.team).ToList();
			if (units.Count == 0)
				return;
			UnitOrder[] cycle = { UnitOrder.Sentry, UnitOrder.Follow, UnitOrder.Patrol, UnitOrder.GoldDig, UnitOrder.BrainHunt };
			int at = Array.IndexOf(cycle, units[0].Order);
			Order(cycle[(at + 1) % cycle.Length]);
		}

		public override void SetControls()
		{
			if (Pick != Picking.None && Player.whoAmI == Main.myPlayer) {
				Player.controlUseItem = false;   // the click is for the order, not the gun
				Player.controlUseTile = false;
			}
		}

		/// <summary>The click for a go-to point or a selection box.</summary>
		internal static void UpdatePicking()
		{
			if (Pick == Picking.None || Main.gameMenu)
				return;
			Player me = Main.LocalPlayer;
			var up = me.GetModPlayer<UnitPlayer>();
			if (Main.mouseRight && Main.mouseRightRelease || me.dead) {
				Pick = Picking.None;
				boxStart = null;
				Main.mouseRightRelease = false;
				return;
			}
			if (Pick == Picking.Waypoint && Main.mouseLeft && Main.mouseLeftRelease) {
				Main.mouseLeftRelease = false;
				Pick = Picking.None;
				UnitSystem.RequestOrders(me, UnitOrder.GoTo, Main.MouseWorld, up.OrderTargets());
				Main.NewText("Units moving to the point.", Color.LightGreen);
			}
			else if (Pick == Picking.Box) {
				if (Main.mouseLeft && boxStart == null)
					boxStart = Main.MouseWorld;
				else if (!Main.mouseLeft && boxStart is Vector2 a) {
					Vector2 b = Main.MouseWorld;
					up.SelectIn(new Rectangle((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), (int)Math.Abs(a.X - b.X) + 1, (int)Math.Abs(a.Y - b.Y) + 1));
					boxStart = null;
					Pick = Picking.None;
				}
				Main.mouseLeftRelease = false;
			}
		}

		internal static void DrawPicking(Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
		{
			if (Pick == Picking.Waypoint) {
				Utils.DrawBorderString(sb, "Go to", new Vector2(Main.mouseX + 10, Main.mouseY - 20), Color.LightGreen, 0.8f);
				return;
			}
			if (Pick != Picking.Box)
				return;
			Utils.DrawBorderString(sb, "Select", new Vector2(Main.mouseX + 10, Main.mouseY - 20), Color.Cyan, 0.8f);
			if (boxStart is not Vector2 a)
				return;
			Vector2 s0 = Vector2.Transform(a - Main.screenPosition, Main.GameViewMatrix.ZoomMatrix) / Main.UIScale;
			Vector2 s1 = new(Main.mouseX, Main.mouseY);
			var r = new Rectangle((int)Math.Min(s0.X, s1.X), (int)Math.Min(s0.Y, s1.Y), (int)Math.Abs(s0.X - s1.X), (int)Math.Abs(s0.Y - s1.Y));
			var t = Terraria.GameContent.TextureAssets.MagicPixel.Value;
			sb.Draw(t, r, Color.Cyan * 0.15f);
			sb.Draw(t, new Rectangle(r.X, r.Y, r.Width, 1), Color.Cyan);
			sb.Draw(t, new Rectangle(r.X, r.Bottom, r.Width, 1), Color.Cyan);
			sb.Draw(t, new Rectangle(r.X, r.Y, 1, r.Height), Color.Cyan);
			sb.Draw(t, new Rectangle(r.Right, r.Y, 1, r.Height), Color.Cyan);
		}

		public override bool PreKill(double damage, int hitDirection, bool pvp, ref bool playSound, ref bool genDust, ref PlayerDeathReason damageSource)
		{
			if (Player.whoAmI != Main.myPlayer || !TerraCommandConfig.Instance.TransferControlOnDeath)
				return true;
			if (Player.GetModPlayer<Actors.RigPlayer>().Kind == Actors.ActorKind.Brain)
				return true; // the brain itself died: game over (BrainPlayer)
			CloneUnit next = UnitSystem.TeamUnits(Player.team)
				.OrderBy(u => Vector2.DistanceSquared(u.NPC.Center, Player.Center)).FirstOrDefault();
			if (next == null)
				return true; // no bodies left: a real death

			// The body dies: it drops everything it carried, and control jumps to the nearest idle body.
			// the body's remains stay where it died (everyone sees them: this isn't a vanilla death)
			Player.GetModPlayer<Combat.DamagePlayer>().LeaveRemains(damage, hitDirection, broadcast: true);
			Player.DropItems();
			Player.statLife = 1;
			Player.immune = true;
			Player.immuneTime = 60;
			UnitSystem.RequestSwap(Player, next, leaveBody: false);
			CombatText.NewText(Player.Hitbox, Color.OrangeRed, "Body lost - transferring control", dramatic: true);
			Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.PlayerKilled, Player.Center);
			return false;
		}
	}
}
