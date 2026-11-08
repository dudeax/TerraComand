using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;
using TerraCommand.Common.Physics;

namespace TerraCommand.Common.Combat
{
	/// <summary>
	/// Cortex Command style aiming for guns, bows and magic: spread depends on stance (crouch = steady, walking,
	/// airborne and jetpacking = wild), recoil builds up per shot and settles over time, and heavy weapons kick
	/// the shooter back. A reticle around the cursor shows the current accuracy.
	/// </summary>
	public class AimPlayer : ModPlayer
	{
		public float Recoil;      // radians, added to spread
		public float Spread;      // radians, stance-based spread of the held weapon

		/// <summary>|deviation| of recent shots (radians), for tests.</summary>
		public readonly List<float> ShotLog = new();
		/// <summary>Cone half-angle at each recent shot (radians), for tests.</summary>
		public readonly List<float> ConeLog = new();

		public static bool IsAimed(Item item) =>
			item != null && !item.IsAir && item.shoot > 0 && item.damage > 0 && !item.channel
			&& (item.CountsAsClass(DamageClass.Ranged) || item.CountsAsClass(DamageClass.Magic));

		public float StanceMultiplier()
		{
			var body = Player.GetModPlayer<BodyPhysicsPlayer>();
			if (body.Jetting)
				return Tuning.AimJetting;
			if (!body.Grounded)
				return Tuning.AimAirborne;
			if (body.Crouching)
				return Tuning.AimCrouched;
			return Math.Abs(Player.velocity.X) > 0.6f ? Tuning.AimMoving : 1f;
		}

		public float WeaponSpread(Item item) => Tuning.AimBaseSpread + (item.useTime < 10 ? Tuning.AimFastWeaponSpread : 0f);

		public override void PostUpdate()
		{
			Item held = Player.HeldItem;
			Spread = IsAimed(held) ? WeaponSpread(held) * StanceMultiplier() : 0f;
			Recoil *= Player.GetModPlayer<BodyPhysicsPlayer>().Crouching ? Tuning.RecoilDecayCrouched : Tuning.RecoilDecay;
		}
	}

	public class AimGlobalItem : GlobalItem
	{
		public override void ModifyShootStats(Item item, Player player, ref Vector2 position, ref Vector2 velocity, ref int type, ref int damage, ref float knockback)
		{
			if (!AimPlayer.IsAimed(item))
				return;
			var aim = player.GetModPlayer<AimPlayer>();
			float cone = aim.WeaponSpread(item) * aim.StanceMultiplier() + aim.Recoil; // stance at the moment of firing
			float dev = Main.rand.NextFloat(-cone, cone);
			velocity = velocity.RotatedBy(dev);
			aim.Recoil = Math.Min(Tuning.RecoilMax, aim.Recoil + Tuning.RecoilPerShot * (0.5f + damage / 40f));
			if (Testing.AutoTest.Enabled) {
				aim.ShotLog.Add(Math.Abs(dev));
				aim.ConeLog.Add(cone);
			}

			// physical kick, much stronger without footing
			if (player.whoAmI == Main.myPlayer && velocity != Vector2.Zero) {
				var body = player.GetModPlayer<BodyPhysicsPlayer>();
				float kick = knockback * (body.Grounded ? Tuning.KickGrounded : Tuning.KickAirborne);
				player.velocity -= Vector2.Normalize(velocity) * kick;
			}
		}
	}

	/// <summary>Reticle: a ring around the cursor whose size is the current spread at that distance.</summary>
	public class ReticleSystem : ModSystem
	{
		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Cursor");
			if (idx < 0)
				idx = layers.Count;
			layers.Insert(idx, new LegacyGameInterfaceLayer("TerraCommand: Reticle", () => {
				Player p = Main.LocalPlayer;
				if (Main.gameMenu || p.dead || p.mouseInterface || !AimPlayer.IsAimed(p.HeldItem))
					return true;
				var aim = p.GetModPlayer<AimPlayer>();
				float dist = Vector2.Distance(p.Center, Main.MouseWorld);
				float radius = Math.Clamp(dist * (float)Math.Tan(aim.Spread + aim.Recoil), 3f, 220f);
				Vector2 c = Main.MouseWorld - Main.screenPosition;
				Texture2D px = TextureAssets.MagicPixel.Value;
				int dots = 20;
				for (int i = 0; i < dots; i++) {
					Vector2 at = c + (MathHelper.TwoPi * i / dots).ToRotationVector2() * radius;
					Main.spriteBatch.Draw(px, at, new Rectangle(0, 0, 1, 1), Color.White * 0.7f, 0f, new Vector2(0.5f), 2f, SpriteEffects.None, 0f);
				}
				return true;
			}, InterfaceScaleType.Game));
		}
	}
}
