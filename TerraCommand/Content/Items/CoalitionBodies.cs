using Terraria;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;

namespace TerraCommand.Content.Items
{
	/// <summary>A delivered loadout: a soldier with a rifle, ready to fight as soon as it leaves the ship.</summary>
	public class CoalitionTrooper : BodyItem
	{
		public override string Texture => "TerraCommand/Content/Items/CloneBody";
		public override ActorKind Kind => ActorKind.Soldier;
		public override int BuiltInWeapon => ModContent.ItemType<Devices.CoalitionAssaultRifle>();
		protected override int ValueCopper => Item.buyPrice(gold: 1, silver: 50);
	}
}
