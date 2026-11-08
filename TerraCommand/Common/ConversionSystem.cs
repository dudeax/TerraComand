using Terraria;
using Terraria.ModLoader;

namespace TerraCommand.Common
{
	/// <summary>Terraria supplies the landscape; the enemy faction supplies the combatants.</summary>
	public class ConversionSpawns : GlobalNPC
	{
		public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
		{
			if (!TerraCommandConfig.Instance.ConversionMode)
				return;
			spawnRate = int.MaxValue;
			maxSpawns = 0;
		}
	}
}
