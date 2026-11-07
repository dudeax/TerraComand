using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Orbital;
using TerraCommand.Common.Units;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common
{
	/// <summary>
	/// Developer helpers. Usable from chat (singleplayer / host) or the dedicated server console.
	///   /tc give &lt;itemId&gt; &lt;count&gt;   add items to the orbital stockpile
	///   /tc crater &lt;radius&gt;          carve a crater at the cursor (chat) or world spawn (console)
	///   /tc selftest                 run automated checks of the core systems
	/// </summary>
	public class DebugCommand : ModCommand
	{
		public override CommandType Type => CommandType.Chat | CommandType.Console;
		public override string Command => "tc";
		public override string Usage => "/tc give <itemId> <count> | /tc crater <radius> | /tc selftest";

		public override void Action(CommandCaller caller, string input, string[] args)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				caller.Reply("Run this in singleplayer or on the server console.", Color.Orange);
				return;
			}
			switch (args.FirstOrDefault()) {
				case "give":
					OrbitalStockpile.Change(new StockKey(int.Parse(args[1]), 0), args.Length > 2 ? long.Parse(args[2]) : 1);
					caller.Reply("Added to orbit.");
					break;
				case "crater": {
					float r = args.Length > 1 ? float.Parse(args[1]) : 40f;
					Vector2 at = caller.Player != null && !Main.dedServ ? Main.MouseWorld : new Vector2(Main.spawnTileX * 16, Main.spawnTileY * 16);
					PixelTerrain.RequestCarve(at, r, 1000, CarveFlags.None);
					caller.Reply($"Crater r={r} at {at}.");
					break;
				}
				case "selftest":
					SelfTest(caller);
					break;
				default:
					caller.Reply(Usage);
					break;
			}
		}

		static void Check(CommandCaller c, ref int fails, bool ok, string what)
		{
			if (!ok) fails++;
			c.Reply((ok ? "[PASS] " : "[FAIL] ") + what, ok ? Color.LightGreen : Color.Red);
		}

		static void SelfTest(CommandCaller c)
		{
			int fails = 0;

			// --- terrain: find a solid dirt/stone column near spawn and carve a small hole in one tile
			int x = Main.spawnTileX, y = Main.spawnTileY;
			while (y < Main.maxTilesY - 50 && !TerrainRules.IsCarvable(x, y)) y++;
			y += 3;
			while (y < Main.maxTilesY - 50 && !TerrainRules.IsCarvable(x, y)) y++;
			Check(c, ref fails, TerrainRules.IsCarvable(x, y), $"found carvable tile at {x},{y} (type {Main.tile[x, y].TileType})");
			int type = Main.tile[x, y].TileType;
			PixelTerrain.RequestCarve(new Vector2(x * 16 + 2, y * 16 + 2), 3f, 1000, CarveFlags.NoDust);
			bool partial = PixelTerrain.TryGet(x, y, out var d) && d.Mask != 0 && Main.tile[x, y].HasTile;
			Check(c, ref fails, partial, $"small carve leaves tile partially damaged (mask {d.Mask:X16})");
			PixelTerrain.RequestCarve(new Vector2(x * 16 + 8, y * 16 + 8), 14f, 1000, CarveFlags.NoDust);
			Check(c, ref fails, !Main.tile[x, y].HasTile && !PixelTerrain.TryGet(x, y, out _), "large carve crumbles the tile and clears its mask");

			// hardness: lihzahrd brick must resist a copper-level carve
			int hx = x + 3, hy = y;
			var saved = Main.tile[hx, hy].TileType;
			bool had = Main.tile[hx, hy].HasTile;
			WorldGen.PlaceTile(hx, hy, TileID.LihzahrdBrick, mute: true, forced: true);
			PixelTerrain.RequestCarve(new Vector2(hx * 16 + 8, hy * 16 + 8), 20f, 35, CarveFlags.NoDust);
			Check(c, ref fails, Main.tile[hx, hy].HasTile && Main.tile[hx, hy].TileType == TileID.LihzahrdBrick && !PixelTerrain.TryGet(hx, hy, out _),
				"tiles above the carve power are untouched");
			WorldGen.KillTile(hx, hy, noItem: true);
			if (had) WorldGen.PlaceTile(hx, hy, saved, mute: true, forced: true);

			// persistence round trip for terrain
			PixelTerrain.RequestCarve(new Vector2((x + 6) * 16 + 2, y * 16 + 2), 3f, 1000, CarveFlags.NoDust);
			var sys = ModContent.GetInstance<PixelTerrain>();
			var tag = new TagCompound();
			int before = PixelTerrain.DamagedTileCount;
			sys.SaveWorldData(tag);
			sys.LoadWorldData(tag);
			Check(c, ref fails, before > 0 && PixelTerrain.DamagedTileCount == before, $"terrain damage survives save/load ({before} tiles)");

			// --- stockpile + crafting
			var stockSys = ModContent.GetInstance<OrbitalStockpile>();
			long wood0 = OrbitalStockpile.CountType(ItemID.Wood);
			OrbitalStockpile.Change(new StockKey(ItemID.Wood, 0), 20);
			Check(c, ref fails, OrbitalStockpile.CountType(ItemID.Wood) == wood0 + 20, "items can be added to the stockpile");

			int benchRecipe = Enumerable.Range(0, Recipe.numRecipes).FirstOrDefault(i => Main.recipe[i].createItem.type == ItemID.WorkBench);
			Recipe bench = Main.recipe[benchRecipe];
			long benches0 = OrbitalStockpile.CountType(ItemID.WorkBench);
			Check(c, ref fails, OrbitalCrafting.MaxCrafts(bench) >= 1, "work bench is craftable in orbit (no station needed)");
			OrbitalCrafting.Craft(bench, 1);
			Check(c, ref fails, OrbitalStockpile.CountType(ItemID.WorkBench) == benches0 + 1 && OrbitalStockpile.CountType(ItemID.Wood) == wood0 + 10,
				"crafting consumes ingredients and adds the result");

			int chairRecipe = Enumerable.Range(0, Recipe.numRecipes).First(i => Main.recipe[i].createItem.type == ItemID.WoodenChair);
			Check(c, ref fails, OrbitalCrafting.AvailableStations.Contains(TileID.WorkBenches) && OrbitalCrafting.MaxCrafts(Main.recipe[chairRecipe]) >= 1,
				"a work bench in orbit unlocks work bench recipes");

			var stag = new TagCompound();
			stockSys.SaveWorldData(stag);
			long total = OrbitalStockpile.Stock.Values.Sum();
			stockSys.LoadWorldData(stag);
			Check(c, ref fails, OrbitalStockpile.Stock.Values.Sum() == total, "stockpile survives save/load");

			// group ingredients: iron bar recipes accept lead
			OrbitalStockpile.Change(new StockKey(ItemID.LeadBar, 0), 10);
			OrbitalStockpile.Change(new StockKey(ItemID.IronAnvil, 0), 1);
			int bodyRecipe = Enumerable.Range(0, Recipe.numRecipes).First(i => Main.recipe[i].createItem.type == ModContent.ItemType<Content.Items.CloneBody>());
			OrbitalStockpile.Change(new StockKey(ItemID.Gel, 0), 20);
			OrbitalStockpile.Change(new StockKey(ItemID.Bottle, 0), 1);
			Check(c, ref fails, OrbitalCrafting.MaxCrafts(Main.recipe[bodyRecipe]) >= 1, "recipe groups (lead for iron) and anvil station work");

			// --- units: spawn, save/load
			int units0 = UnitSystem.AllUnits().Count();
			var l = new UnitLoadout();
			l.Inventory[0] = new Item(ItemID.CopperShortsword);
			int idx = NPC.NewNPC(Terraria.Entity.GetSource_NaturalSpawn(), Main.spawnTileX * 16, Main.spawnTileY * 16, ModContent.NPCType<CloneUnit>());
			((CloneUnit)Main.npc[idx].ModNPC).InitFrom(l, 0, UnitOrder.Sentry, 120);
			Check(c, ref fails, UnitSystem.AllUnits().Count() == units0 + 1 && Main.npc[idx].lifeMax == 120, "clone unit spawns with loadout");
			for (int t = 0; t < 120; t++) Main.npc[idx].UpdateNPC(idx);
			Check(c, ref fails, Main.npc[idx].active, "clone unit survives 120 AI ticks");

			var usys = ModContent.GetInstance<UnitSystem>();
			var utag = new TagCompound();
			usys.SaveWorldData(utag);
			var saved0 = utag.GetList<TagCompound>("units").Last();
			var reloaded = UnitLoadout.Load(saved0.GetCompound("loadout"));
			Check(c, ref fails, reloaded.Inventory[0].type == ItemID.CopperShortsword, "unit loadout survives save/load");

			using (var ms = new System.IO.MemoryStream()) {
				var w = new System.IO.BinaryWriter(ms);
				l.Write(w); l.Write(w, visualsOnly: true);
				ms.Position = 0;
				var r = new System.IO.BinaryReader(ms);
				var full = UnitLoadout.Read(r);
				var vis = UnitLoadout.Read(r);
				Check(c, ref fails, full.Inventory[0].type == ItemID.CopperShortsword && vis.Held.type == ItemID.CopperShortsword && ms.Position == ms.Length,
					"unit loadout network serialization round-trips");
			}
			Main.npc[idx].active = false;

			c.Reply(fails == 0 ? "Self-test: ALL PASSED" : $"Self-test: {fails} FAILED", fails == 0 ? Color.LightGreen : Color.Red);
		}
	}
}
