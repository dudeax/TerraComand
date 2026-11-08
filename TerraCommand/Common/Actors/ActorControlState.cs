using Terraria;
using TerraCommand.Common.UI;
using TerraCommand.Common.Orbital;
using TerraCommand.Common.Units;

namespace TerraCommand.Common.Actors
{
	/// <summary>Menus consume the actor's controls while the world continues to simulate.</summary>
	public static class ActorControlState
	{
		public static bool Blocked => Main.playerInventory || Main.mapFullscreen || Main.drawingPlayerChat
			|| Main.editSign || Main.editChest || Main.LocalPlayer.talkNPC >= 0
			|| ShipHoldUI.OpenShip >= 0 || ActorControlsUI.PieOpen || OrbitalUISystem.Visible || ShipSystem.PickingLz
			|| UnitPlayer.Pick != UnitPlayer.Picking.None;
	}
}
