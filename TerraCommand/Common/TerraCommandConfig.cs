using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace TerraCommand.Common
{
	public class TerraCommandConfig : ModConfig
	{
		public override ConfigScope Mode => ConfigScope.ServerSide;
		public static TerraCommandConfig Instance => ModContent.GetInstance<TerraCommandConfig>();

		[Header("Conversion")]
		[DefaultValue(true)]
		public bool ConversionMode { get; set; } = true;

		[Header("Terrain")]
		[DefaultValue(true)]
		public bool PixelMining { get; set; } = true;

		[DefaultValue(true)]
		public bool PixelExplosions { get; set; } = true;

		[DefaultValue(true)]
		public bool ProjectilesChipTerrain { get; set; } = true;

		[Range(0.5f, 1f)]
		[Increment(0.05f)]
		[DefaultValue(0.8f)]
		public float CrumbleThreshold { get; set; } = 0.8f;

		[Header("Bodies")]
		[DefaultValue(false)]
		public bool NaturalLifeRegen { get; set; } = false;

		[Header("Units")]
		[DefaultValue(true)]
		public bool TransferControlOnDeath { get; set; } = true;

		[Range(1, 50)]
		[DefaultValue(12)]
		public int MaxUnitsPerTeam { get; set; } = 12;

		[DefaultValue(true)]
		public bool EnemyFaction { get; set; } = true;

		[DefaultValue(10)]
		[Range(1, 120)]
		public int EnemyFirstAttackMinutes { get; set; } = 10;

		[DefaultValue(7)]
		[Range(1, 120)]
		public int EnemyAttackMinutes { get; set; } = 7;
	}
}
