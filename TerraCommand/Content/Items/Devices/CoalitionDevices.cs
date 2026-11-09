using Terraria;
using Terraria.ID;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Combat;

namespace TerraCommand.Content.Items.Devices
{
	// Data ports from Data/Coalition.rte/Devices/Weapons in the local CC Community reference.
	// See docs/coalition-content.md for source paths and deliberate engine adaptations.
	public class CoalitionPistol : CCFirearm
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/ServicePistol";
		public override FirearmSpec Spec { get; } = new() {
			Mass = 4, RateOfFire = 300, FullAuto = false, Magazine = 10, ReloadTicks = 60,
			ShakeDeg = 2, SharpShakeDeg = 1, SharpLength = 125, NoSupportFactor = 1.5f,
			RoundProfile=2, FireVelocity = CC.Speed(79), Recoil = 1.5f, OneHanded = true,
		};
		protected override int Damage => 14;
		protected override int ValueCopper => Item.buyPrice(silver: 10);
	}

	public class CoalitionAssaultRifle : CCFirearm
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/FieldRifle";
		public override FirearmSpec Spec { get; } = new() {
			Mass = 13, RateOfFire = 800, Magazine = 35, ReloadTicks = 108,
			ShakeDeg = 5, SharpShakeDeg = 3, SharpLength = 250, NoSupportFactor = 2,
			FireVelocity = CC.Speed(100), Recoil = 2,
		};
		protected override int Damage => 11;
		protected override int ValueCopper => Item.buyPrice(silver: 50);
	}

	public class CoalitionCompactRifle : CCFirearm
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/TriBurstRifle";
		public override FirearmSpec Spec { get; } = new() {
			Mass = 9, RateOfFire = 1000, Magazine = 24, ReloadTicks = 96,
			ShakeDeg = 7, SharpShakeDeg = 2.5f, SharpLength = 150, NoSupportFactor = 1.5f,
			FireVelocity = CC.Speed(90), Recoil = 1.8f,
		};
		protected override int Damage => 10;
		protected override int ValueCopper => Item.buyPrice(silver: 30);
	}

	public class CoalitionSniperRifle : CCFirearm
	{
		public override string Texture => "TerraCommand/Content/Items/Devices/Longshot";
		public override FirearmSpec Spec { get; } = new() {
			Mass = 15, RateOfFire = 150, FullAuto = false, Magazine = 8, ReloadTicks = 132,
			ShakeDeg = 5, SharpShakeDeg = 0.1f, SharpLength = 420, NoSupportFactor = 1.5f,
			Projectile = ProjectileID.BulletHighVelocity, FireVelocity = CC.Speed(140), Recoil = 6,
			Sound = SoundID.Item40,
		};
		protected override int Damage => 50;
		protected override int ValueCopper => Item.buyPrice(silver: 70);
	}
}
