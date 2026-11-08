using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace TerraCommand.Common.Physics
{
	/// <summary>
	/// Cortex Command style movement for the body a player controls: momentum walking, heavy gravity, a short
	/// hop plus a fuel-limited jetpack, crouching, impact damage and knockdowns. Vanilla flight, double jumps,
	/// dashes, mounts and fall damage are disabled; wings / rocket boots / extra-jump accessories become jetpack
	/// upgrades instead.
	/// </summary>
	public class BodyPhysicsPlayer : ModPlayer
	{
		public float Fuel = Tuning.JetBaseFuel, FuelMax = Tuning.JetBaseFuel, FuelRegen = Tuning.JetBaseRegen, Thrust = Tuning.JetThrust;
		public bool Jetting, Crouching;
		public int KnockedTicks;
		int knockDir = 1;
		Vector2 velBefore;
		int sinceBurn = 999;
		float impactAccum;
		int impactTimer;
		bool skipImpact = true;

		/// <summary>
		/// Standing on something, as of the end of the last update (after collision). During the update
		/// vanilla has already added gravity, so velocity.Y == 0 isn't a reliable test there.
		/// </summary>
		public bool Grounded { get; private set; } = true;

		// ------------------------------------------------------------ gear -> jetpack

		public override void PostUpdateEquips()
		{
			float fuel = Tuning.JetBaseFuel, regen = Tuning.JetBaseRegen, thrust = Tuning.JetThrust;
			if (Player.wingTimeMax > 0) {
				fuel += Player.wingTimeMax * 0.6f;
				thrust += Player.wingTimeMax / 900f;
				regen += 0.2f;
			}
			if (Player.rocketBoots > 0) {
				fuel += 20f + Player.rocketTimeMax * 2f;
				regen += 0.1f;
			}
			foreach (ExtraJump jump in ExtraJumpLoader.OrderedJumps) {
				if (Player.GetJumpState(jump).Enabled) {
					fuel += 12f;
					regen += 0.15f;
				}
			}
			FuelMax = fuel;
			FuelRegen = regen;
			Thrust = thrust;
			Fuel = Math.Min(Fuel, FuelMax);

			// vanilla mobility off: it's all jetpack now
			Player.wingTimeMax = 0;
			Player.wingTime = 0;
			Player.rocketTimeMax = 0;
			Player.rocketTime = 0;
			Player.blockExtraJumps = true;
			Player.dashType = 0;
			Player.noFallDmg = true;
			Player.jumpSpeedBoost += Tuning.JumpSpeedBoost;
			if (Player.mount.Active)
				Player.mount.Dismount(Player);
		}

		// ------------------------------------------------------------ controls

		public override void SetControls()
		{
			if (KnockedTicks > 0) {
				Player.controlLeft = Player.controlRight = Player.controlJump = Player.controlUp = Player.controlDown = false;
				Player.controlUseItem = Player.controlUseTile = Player.controlHook = false;
			}
		}

		// ------------------------------------------------------------ movement

		public override void PreUpdateMovement()
		{
			velBefore = Player.velocity;
			Crouching = Player.controlDown && Grounded && KnockedTicks == 0;
			Player.gravity = Tuning.Gravity;
			Player.maxFallSpeed = Tuning.MaxFallSpeed;

			bool hooked = Player.grappling[0] >= 0 || Player.pulley || Player.mount.Active;
			Jetting = false;
			if (Player.controlJump && !Grounded && Player.jump == 0 && Fuel >= 1f && !hooked && KnockedTicks == 0) {
				Jetting = true;
				Fuel -= 1f;
				sinceBurn = 0;
				// cancel this tick's gravity and push up
				Player.velocity.Y = Math.Max(Player.velocity.Y - Player.gravity - Thrust, -Tuning.JetMaxRise);
				if (Player.controlLeft)
					Player.velocity.X -= Tuning.JetSteer;
				if (Player.controlRight)
					Player.velocity.X += Tuning.JetSteer;
				JetEffects();
			}
			else {
				// Refuel only after a short pause, and never while still holding jump in the air
				// (otherwise tapping the last drops of fuel would let you hover forever).
				sinceBurn++;
				bool holdingInAir = Player.controlJump && !Grounded;
				if (sinceBurn > Tuning.JetRefuelDelay && !holdingInAir)
					Fuel = Math.Min(FuelMax, Fuel + FuelRegen * (Grounded ? 2f : 1f));
			}
		}

		void JetEffects()
		{
			if (Main.dedServ)
				return;
			Vector2 nozzle = Player.Center + new Vector2(-Player.direction * 7f, 6f);
			var d = Dust.NewDustDirect(nozzle - new Vector2(3), 6, 6, DustID.Torch, -Player.velocity.X * 0.3f, 4f, 100, default, 1.5f);
			d.noGravity = true;
			if (Main.rand.NextBool(2))
				Dust.NewDust(nozzle - new Vector2(3), 6, 6, DustID.Smoke, 0f, 2f, 150, default, 1.1f);
			if (Player.miscCounter % 9 == 0)
				SoundEngine.PlaySound(SoundID.Item24 with { Volume = 0.5f }, Player.Center);
		}

		public override void PostUpdateRunSpeeds()
		{
			// vanilla resets these after PreUpdateMovement; this hook runs before the vertical clamp
			Player.gravity = Tuning.Gravity;
			Player.maxFallSpeed = Tuning.MaxFallSpeed;
			// keep accessory speed bonuses, relative to vanilla's base of 3
			Player.maxRunSpeed = Player.maxRunSpeed / 3f * Tuning.WalkSpeed;
			Player.accRunSpeed = Math.Max(Player.maxRunSpeed, Player.accRunSpeed / 3f * Tuning.WalkSpeed);
			if (Crouching) {
				Player.maxRunSpeed = Player.accRunSpeed = Tuning.CrouchSpeed;
			}
			if (KnockedTicks > 0) {
				Player.runAcceleration = 0f;
				Player.runSlowdown = Grounded ? Tuning.KnockedFriction : Tuning.AirDrag;
			}
			else if (Grounded) {
				Player.runAcceleration = Player.runAcceleration / 0.08f * Tuning.WalkAccel;
				Player.runSlowdown = Tuning.GroundFriction;
			}
			else {
				Player.runAcceleration = Tuning.AirAccel;
				Player.runSlowdown = Tuning.AirDrag;
			}
		}

		// ------------------------------------------------------------ impacts

		public override void PostUpdate()
		{
			Grounded = Player.velocity.Y == 0f;
			if (KnockedTicks > 0) {
				KnockedTicks--;
				Player.fullRotation = knockDir * MathHelper.PiOver2 * Math.Min(1f, KnockedTicks / 10f);
				Player.fullRotationOrigin = new Vector2(Player.width / 2f, Player.height / 2f);
				if (KnockedTicks == 0)
					Player.fullRotation = 0f;
			}

			if (Player.whoAmI != Main.myPlayer || Player.dead) {
				skipImpact = true;
				return;
			}
			// A sudden loss of speed (hitting ground/wall/ceiling) hurts. Vanilla stops a body over a couple of
			// ticks (first shortening the move to touch the surface, then zeroing it), so speed lost over
			// consecutive ticks is added up and judged as one impact. Teleports/swaps/shoves are skipped.
			float loss = Vector2.Distance(velBefore, Player.velocity);
			if (skipImpact || Player.grappling[0] >= 0) {
				impactAccum = 0f;
				impactTimer = 0;
			}
			else if (loss > 2.5f) {
				impactAccum += loss;
				impactTimer = 3;
			}
			else if (impactTimer > 0 && --impactTimer == 0) {
				Impact(impactAccum, velBefore);
				impactAccum = 0f;
			}
			skipImpact = false;
		}

		void Impact(float speedLost, Vector2 direction)
		{
			Testing.AutoTest.Trace($"impact {speedLost:0.0}");
			if (speedLost <= Tuning.ImpactThreshold)
				return;
			float over = speedLost - Tuning.ImpactThreshold;
			int dmg = (int)(over * over * Tuning.ImpactDamageScale);
			if (dmg > 0)
				Player.Hurt(PlayerDeathReason.ByCustomReason(NetworkText.FromLiteral($"{Player.name} hit the ground too hard.")),
					dmg, 0, dodgeable: false, knockback: 0f);
			if (speedLost > Tuning.KnockdownImpact)
				KnockDown(Math.Sign(direction.X == 0 ? Player.direction : direction.X));
		}

		/// <summary>Call after teleporting the body (no impact damage from the position/velocity jump).</summary>
		public void SkipImpactCheck() => skipImpact = true;

		public void KnockDown(int dir)
		{
			knockDir = dir == 0 ? 1 : dir;
			KnockedTicks = Math.Max(KnockedTicks, Tuning.KnockdownTicks);
		}

		/// <summary>Shove this body (local player only; other players are moved by their own client).</summary>
		public void ApplyImpulse(Vector2 impulse)
		{
			Testing.AutoTest.Trace($"impulse {impulse} on {Player.name} (me={Player.whoAmI == Main.myPlayer})");
			if (Player.whoAmI != Main.myPlayer)
				return;
			Player.velocity += impulse;
			skipImpact = true; // the shove itself isn't an impact
			if (impulse.Length() > Tuning.KnockdownImpulse)
				KnockDown(Math.Sign(impulse.X));
		}

		public override void OnRespawn() => KnockedTicks = 0;

		/// <summary>Explosions push through the blast physics (Explosions.Blast) instead of vanilla knockback.</summary>
		public override void ModifyHitByProjectile(Projectile proj, ref Player.HurtModifiers modifiers)
		{
			if (ProjectileID.Sets.Explosive[proj.type] || proj.aiStyle == ProjAIStyleID.Explosive)
				modifiers.Knockback *= 0f;
		}
	}

	/// <summary>Mounts don't exist in this world: bodies move on their own legs and jetpacks.</summary>
	public class NoMountsGlobalItem : GlobalItem
	{
		public override bool CanUseItem(Item item, Player player) => item.mountType < 0;
	}
}
