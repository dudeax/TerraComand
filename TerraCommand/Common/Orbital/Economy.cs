using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using TerraCommand.Common.Combat;
using TerraCommand.Common.Net;
using TerraCommand.Content.Items;

namespace TerraCommand.Common.Orbital
{
	/// <summary>
	/// Cortex Command's gold, as Terraria coins: the coins in the orbital stockpile are the team's funds. Bodies and CC devices
	/// are bought from orbit (into the stockpile, then shipped down), calling a ship costs money, and anything in the stockpile
	/// can be sold for Terraria's sell price (mined ore and gems, loot...). Server-authoritative.
	/// </summary>
	public static class Economy
	{
		static readonly (int type, long value)[] coins = {
			(ItemID.PlatinumCoin, 1_000_000), (ItemID.GoldCoin, 10_000), (ItemID.SilverCoin, 100), (ItemID.CopperCoin, 1),
		};

		public static readonly long ShipCost = Item.buyPrice(silver: 50);

		/// <summary>What calling each craft type costs (a crate is cheap but one-way; a dropship is dear but can be flown).</summary>
		public static long CostOf(Content.NPCs.ShipStyle style) => style switch {
			Content.NPCs.ShipStyle.Dropship => Item.buyPrice(gold: 1),
			Content.NPCs.ShipStyle.Crate => Item.buyPrice(silver: 20),
			_ => ShipCost,
		};
		public static readonly long StartingFunds = Item.buyPrice(gold: 5);

		static bool IsClient => Main.netMode == NetmodeID.MultiplayerClient;

		/// <summary>The funds in orbit, in copper.</summary>
		public static long Funds()
		{
			long total = 0;
			foreach ((int type, long value) in coins)
				total += OrbitalStockpile.Count(new StockKey(type, 0)) * value;
			return total;
		}

		public static string Format(long copper)
		{
			if (copper <= 0)
				return "0c";
			var parts = new List<string>();
			foreach ((int type, long value) in coins) {
				long n = copper / value;
				copper %= value;
				if (n > 0)
					parts.Add(n + (type == ItemID.PlatinumCoin ? "p" : type == ItemID.GoldCoin ? "g" : type == ItemID.SilverCoin ? "s" : "c"));
			}
			return string.Join(" ", parts);
		}

		/// <summary>Authority: pay from the stockpile's coins (making change). False if the funds aren't there.</summary>
		public static bool TrySpend(long copper)
		{
			long have = Funds();
			if (copper > have)
				return false;
			SetFunds(have - copper);
			return true;
		}

		/// <summary>Authority: add money to the stockpile.</summary>
		public static void Earn(long copper) => SetFunds(Funds() + Math.Max(0, copper));

		static void SetFunds(long total)
		{
			foreach ((int type, long value) in coins) {
				var key = new StockKey(type, 0);
				long want = total / value;
				total %= value;
				OrbitalStockpile.Change(key, want - OrbitalStockpile.Count(key));
			}
		}

		// ------------------------------------------------------------ catalogue

		static List<int> catalogue;

		/// <summary>What can be bought from orbit: every body type and every CC device.</summary>
		public static IReadOnlyList<int> Catalogue => catalogue ??= ModContent.GetContent<ModItem>()
			.Where(m => m is BodyItem or CCFirearm or CCThrowable || m.GetType().Namespace == typeof(Content.Items.Devices.Medikit).Namespace)
			.OrderBy(m => m is BodyItem ? 0 : m is CCFirearm ? 1 : m is CCThrowable ? 2 : 3).ThenBy(m => m.Item.value)
			.Select(m => m.Type).Distinct().ToList();

		public static long Price(int type) => Math.Max(1, ContentSamples.ItemsByType[type].value);
		public static long SellPrice(int type) => ContentSamples.ItemsByType[type].value / 5;

		// ------------------------------------------------------------ requests

		public static void RequestBuy(int type, int count)
		{
			if (IsClient) {
				ModPacket p = PacketHandler.Create(PacketType.Buy);
				p.Write(type);
				p.Write(count);
				p.Send();
				return;
			}
			DoBuy(type, count, -1);
		}

		public static void RequestSell(StockKey key, long count)
		{
			if (IsClient) {
				ModPacket p = PacketHandler.Create(PacketType.Sell);
				p.Write(key.Type);
				p.Write(key.Prefix);
				p.Write(count);
				p.Send();
				return;
			}
			DoSell(key, count, -1);
		}

		static void DoBuy(int type, int count, int player)
		{
			if (!Catalogue.Contains(type) || count <= 0)
				return;
			long cost = Price(type) * count;
			if (!TrySpend(cost)) {
				Tell(player, $"Not enough funds: {Format(cost)} needed, {Format(Funds())} in orbit.");
				return;
			}
			OrbitalStockpile.Change(new StockKey(type, 0), count);
			Testing.AutoTest.Trace($"bought {count} {ContentSamples.ItemsByType[type].Name} for {Format(cost)}");
		}

		static void DoSell(StockKey key, long count, int player)
		{
			if (coins.Any(c => c.type == key.Type))
				return;
			count = Math.Min(count, OrbitalStockpile.Count(key));
			long each = SellPrice(key.Type);
			if (count <= 0 || each <= 0)
				return;
			OrbitalStockpile.Change(key, -count);
			Earn(each * count);
			Testing.AutoTest.Trace($"sold {count} {ContentSamples.ItemsByType[key.Type].Name} for {Format(each * count)}");
		}

		static void Tell(int player, string text)
		{
			if (Main.netMode == NetmodeID.Server && player >= 0)
				ChatHelper.SendChatMessageToClient(NetworkText.FromLiteral(text), Microsoft.Xna.Framework.Color.Orange, player);
			else if (!Main.dedServ)
				Main.NewText(text, Microsoft.Xna.Framework.Color.Orange);
		}

		public static void Receive(PacketType type, BinaryReader r, int from)
		{
			if (type == PacketType.Buy) {
				int t = r.ReadInt32(), n = r.ReadInt32();
				if (Main.netMode == NetmodeID.Server)
					DoBuy(t, Math.Clamp(n, 1, 999), from);
			}
			else {
				var key = new StockKey(r.ReadInt32(), r.ReadInt32());
				long n = r.ReadInt64();
				if (Main.netMode == NetmodeID.Server)
					DoSell(key, n, from);
			}
		}

		/// <summary>Authority: a ship costs money to call (false: not enough funds, and the team is told).</summary>
		public static bool PayForShip(int player, Content.NPCs.ShipStyle style)
		{
			long cost = CostOf(style);
			if (TrySpend(cost))
				return true;
			Tell(player, $"Not enough funds to call a {style.ToString().ToLower()} ({Format(cost)}; {Format(Funds())} in orbit).");
			return false;
		}
	}
}
