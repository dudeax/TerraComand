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
			yield return new Item(ModContent.ItemType<Content.Tiles.LaunchPadItem>());
			yield return new Item(ModContent.ItemType<Content.Items.OrbitalConstructor>());
			yield return new Item(ModContent.ItemType<Content.Items.CloneBody>(), 2);
		}
	}
}
