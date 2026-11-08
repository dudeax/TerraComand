using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Net;

namespace TerraCommand.Common.Orbital
{
	public class OrbitalPlayer : ModPlayer
	{
		/// <summary>The stockpile entry the Orbital Constructor places. Client-local.</summary>
		public StockKey? BuildKey;

		public override void OnEnterWorld()
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
				PacketHandler.Create(PacketType.FullSyncRequest).Send();
		}

		public override IEnumerable<Item> AddStartingItems(bool mediumCoreDeath)
		{
			if (mediumCoreDeath)
				yield break;
			// bodies arrive by ship (the brain's first delivery); the constructor builds from orbit
			yield return new Item(ModContent.ItemType<Content.Items.OrbitalConstructor>());
		}
	}
}
