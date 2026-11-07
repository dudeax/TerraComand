using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Orbital
{
	/// <summary>
	/// Crafting directly inside the orbital stockpile. A crafting station counts as available when its
	/// item is in the stockpile (launch a Work Bench to orbit and you can craft Work Bench recipes there).
	/// Water / lava / honey requirements are met by having the matching bucket in orbit.
	/// </summary>
	public static class OrbitalCrafting
	{
		static int cachedVersion = -1;
		static readonly HashSet<int> stations = new();

		// Vanilla stations that also count as other stations (Player.AdjTiles hardcodes these).
		static readonly Dictionary<int, ushort[]> vanillaAdj = new() {
			[TileID.Hellforge] = new ushort[] { TileID.Furnaces },
			[TileID.AdamantiteForge] = new ushort[] { TileID.Furnaces, TileID.Hellforge },
			[TileID.MythrilAnvil] = new ushort[] { TileID.Anvils },
			[TileID.AlchemyTable] = new ushort[] { TileID.Bottles },
			[TileID.BewitchingTable] = new ushort[0],
			[TileID.LunarCraftingStation] = new ushort[] { TileID.LunarCraftingStation },
			[TileID.GlassKiln] = new ushort[0],
			[TileID.Tables] = new ushort[0],
			[TileID.Tables2] = new ushort[] { TileID.Tables },
			[TileID.Chairs] = new ushort[0],
		};

		public static HashSet<int> AvailableStations
		{
			get
			{
				if (cachedVersion != OrbitalStockpile.Version) {
					stations.Clear();
					foreach (var k in OrbitalStockpile.Stock.Keys) {
						int tile = ContentSamples.ItemsByType[k.Type].createTile;
						if (tile < 0)
							continue;
						stations.Add(tile);
						if (vanillaAdj.TryGetValue(tile, out ushort[] adj))
							foreach (ushort a in adj)
								stations.Add(a);
						if (TileLoader.GetTile(tile) is ModTile mt && mt.AdjTiles != null)
							stations.UnionWith(mt.AdjTiles);
					}
					cachedVersion = OrbitalStockpile.Version;
				}
				return stations;
			}
		}

		public static IEnumerable<int> AcceptedTypes(Recipe r, Item req)
		{
			yield return req.type;
			foreach (int g in r.acceptedGroups) {
				if (RecipeGroup.recipeGroups.TryGetValue(g, out RecipeGroup group) && group.ContainsItem(req.type)) {
					foreach (int t in group.ValidItems)
						if (t != req.type)
							yield return t;
				}
			}
		}

		static bool ConditionMet(Condition c)
		{
			if (c == Condition.NearWater)
				return OrbitalStockpile.CountType(ItemID.WaterBucket) > 0 || OrbitalStockpile.CountType(ItemID.BottomlessBucket) > 0;
			if (c == Condition.NearLava)
				return OrbitalStockpile.CountType(ItemID.LavaBucket) > 0 || OrbitalStockpile.CountType(ItemID.BottomlessLavaBucket) > 0;
			if (c == Condition.NearHoney)
				return OrbitalStockpile.CountType(ItemID.HoneyBucket) > 0 || OrbitalStockpile.CountType(ItemID.BottomlessHoneyBucket) > 0;
			// Other conditions (progression, biome, etc.) are evaluated normally. On a dedicated server
			// player-environment conditions cannot be evaluated, so they are trusted from the client.
			return Main.dedServ || c.IsMet();
		}

		public static bool StationsAndConditionsMet(Recipe r)
		{
			if (r.Disabled || r.createItem.IsAir)
				return false;
			var st = AvailableStations;
			foreach (int t in r.requiredTile)
				if (!st.Contains(t))
					return false;
			foreach (Condition c in r.Conditions)
				if (!ConditionMet(c))
					return false;
			return true;
		}

		/// <summary>How many times this recipe can be crafted from the stockpile (0 if not at all).</summary>
		public static long MaxCrafts(Recipe r)
		{
			if (!StationsAndConditionsMet(r))
				return 0;
			long max = long.MaxValue;
			foreach (Item req in r.requiredItem) {
				if (req.IsAir || req.stack <= 0)
					continue;
				long have = AcceptedTypes(r, req).Distinct().Sum(OrbitalStockpile.CountType);
				max = Math.Min(max, have / req.stack);
				if (max == 0)
					return 0;
			}
			return max == long.MaxValue ? 0 : max;
		}

		/// <summary>Authority only. Crafts up to <paramref name="times"/> and adds the results to orbit.</summary>
		public static int Craft(Recipe r, int times)
		{
			int done = 0;
			for (; done < times; done++) {
				if (MaxCrafts(r) < 1)
					break;
				foreach (Item req in r.requiredItem) {
					if (req.IsAir || req.stack <= 0)
						continue;
					OrbitalStockpile.TryTake(AcceptedTypes(r, req).ToHashSet(), req.stack);
				}
				var made = new Item(r.createItem.type, r.createItem.stack);
				if (made.CanHavePrefixes())
					made.Prefix(-1);
				OrbitalStockpile.Add(made);
			}
			return done;
		}

		public static IEnumerable<int> CraftableRecipeIndices()
		{
			for (int i = 0; i < Recipe.numRecipes; i++) {
				Recipe r = Main.recipe[i];
				if (r != null && MaxCrafts(r) > 0)
					yield return i;
			}
		}
	}
}
