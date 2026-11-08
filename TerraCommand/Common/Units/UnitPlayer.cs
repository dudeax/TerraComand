using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameInput;
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

		internal void CycleOrders()
		{
			var units = UnitSystem.TeamUnits(Player.team).ToList();
			if (units.Count == 0)
				return;
			UnitOrder next = units[0].Order == UnitOrder.Sentry ? UnitOrder.Follow : UnitOrder.Sentry;
			UnitSystem.RequestOrders(Player, next);
			Main.NewText($"Units ordered to {(next == UnitOrder.Follow ? "follow" : "hold position")}.", Color.LightGreen);
		}

		public override bool PreKill(double damage, int hitDirection, bool pvp, ref bool playSound, ref bool genDust, ref PlayerDeathReason damageSource)
		{
			if (Player.whoAmI != Main.myPlayer || !TerraCommandConfig.Instance.TransferControlOnDeath)
				return true;
			CloneUnit next = UnitSystem.TeamUnits(Player.team)
				.OrderBy(u => Vector2.DistanceSquared(u.NPC.Center, Player.Center)).FirstOrDefault();
			if (next == null)
				return true; // no bodies left: a real death

			// The body dies: it drops everything it carried, and control jumps to the nearest idle body.
			if (Player.GetModPlayer<Combat.DamagePlayer>().IsViolentDeath(damage))
				Combat.DamageRules.Gib(Player.Hitbox, new Vector2(hitDirection * 3f, -2f));
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
