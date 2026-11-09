using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraCommand.Common.Destruction
{
	/// <summary>Which tiles can be carved per-pixel, how hard they are, and how quickly they erode.</summary>
	public static class TerrainRules
	{
		static readonly HashSet<int> Soft = new() {
			TileID.Dirt, TileID.Mud, TileID.Sand, TileID.Ebonsand, TileID.Crimsand, TileID.Pearlsand,
			TileID.Silt, TileID.Slush, TileID.Ash, TileID.ClayBlock, TileID.SnowBlock, TileID.HardenedSand,
			TileID.Grass, TileID.CorruptGrass, TileID.CrimsonGrass, TileID.HallowedGrass, TileID.JungleGrass,
			TileID.MushroomGrass, TileID.AshGrass, TileID.HayBlock, TileID.LeafBlock, TileID.LivingMahoganyLeaves,
			TileID.Cloud, TileID.RainCloud, TileID.SnowCloud, TileID.CorruptJungleGrass, TileID.CrimsonJungleGrass,
		};

		/// <summary>Grass is a surface coating; growing or stripping it does not replace its soil.</summary>
		public static ushort Substrate(int type) => type switch {
			TileID.JungleGrass or TileID.CorruptJungleGrass or TileID.CrimsonJungleGrass or TileID.MushroomGrass => TileID.Mud,
			TileID.AshGrass => TileID.Ash,
			_ when TileID.Sets.Grass[type] => TileID.Dirt,
			_ => (ushort)type,
		};

		/// <summary>True if this tile participates in pixel destruction (plain solid blocks only).</summary>
		public static bool IsCarvable(int i, int j)
		{
			if (!WorldGen.InWorld(i, j, 2))
				return false;
			Tile t = Main.tile[i, j];
			if (!t.HasTile)
				return false;
			int type = t.TileType;
			return Main.tileSolid[type] && !Main.tileSolidTop[type] && !Main.tileFrameImportant[type]
				&& !TileID.Sets.Platforms[type] && type != TileID.LihzahrdAltar;
		}

		/// <summary>Minimum pickaxe power, mirroring vanilla requirements.</summary>
		public static int MinPick(int type)
		{
			ModTile mt = TileLoader.GetTile(type);
			if (mt != null)
				return mt.MinPick;
			switch (type) {
				case TileID.Meteorite: return 50;
				case TileID.Demonite:
				case TileID.Crimtane:
				case TileID.Obsidian: return 55;
				case TileID.Ebonstone:
				case TileID.Crimstone:
				case TileID.Pearlstone:
				case TileID.Hellstone:
				case TileID.BlueDungeonBrick:
				case TileID.GreenDungeonBrick:
				case TileID.PinkDungeonBrick: return 65;
				case TileID.Cobalt:
				case TileID.Palladium: return 100;
				case TileID.Mythril:
				case TileID.Orichalcum: return 110;
				case TileID.Adamantite:
				case TileID.Titanium: return 150;
				case TileID.Chlorophyte: return 200;
				case TileID.LihzahrdBrick: return 210;
				case TileID.LunarOre: return 225;
			}
			return 0;
		}

		/// <summary>Extra rules for explosions, matching vanilla's boss gating.</summary>
		public static bool ExplosionCanAffect(int type)
		{
			if (type == TileID.LihzahrdBrick)
				return NPC.downedGolemBoss;
			if (Main.tileDungeon[type])
				return NPC.downedBoss3;
			return true;
		}

		/// <summary>Radius multiplier: dirt/sand erode quickly, stone slower, bricks slowest.</summary>
		public static float Softness(int type)
		{
			if (Soft.Contains(type) || Main.tileSand[type])
				return 1f;
			if (Main.tileBrick[type])
				return 0.6f;
			return 0.8f;   // a blast still bowls out a round crater in stone, just a smaller one
		}

		public static Physics.PhysicalMaterial Material(int type) {
			type = Substrate(type);
			if (type == TileID.IceBlock || type == TileID.BreakableIce) return Physics.PhysicalMaterial.Ice;
			if (Main.tileSand[type] || type == TileID.Silt || type == TileID.Slush) return Physics.PhysicalMaterial.Sand;
			if (type == TileID.WoodBlock || type == TileID.LivingWood) return Physics.PhysicalMaterial.Wood;
			if (Soft.Contains(type)) return Physics.PhysicalMaterial.Earth;
			if (TileID.Sets.Ore[type]) return Physics.PhysicalMaterial.Metal;
			if (Main.tileBrick[type]) return Physics.PhysicalMaterial.Concrete;
			return Physics.PhysicalMaterial.Stone;
		}

		/// <summary>Explosive power used when comparing against tile hardness.</summary>
		public static int ExplosivePower => Main.hardMode ? 160 : 70;
	}
}
