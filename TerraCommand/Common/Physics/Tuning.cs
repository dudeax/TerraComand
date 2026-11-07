namespace TerraCommand.Common.Physics
{
	/// <summary>Cortex Command style body physics constants (per tick, world pixels). Tune here.</summary>
	public static class Tuning
	{
		// gravity / falling
		public const float Gravity = 0.42f;          // vanilla 0.4
		public const float MaxFallSpeed = 16f;       // vanilla 10: falls get fast and dangerous

		// walking (vanilla: max 3, accel 0.08, slowdown 0.2)
		public const float WalkSpeed = 2.4f;
		public const float WalkAccel = 0.07f;
		public const float GroundFriction = 0.15f;
		public const float AirAccel = 0.025f;        // little air control: momentum carries you
		public const float AirDrag = 0.004f;
		public const float CrouchSpeed = 0.9f;

		// jumping: a short hop; the jetpack does the real climbing
		public const float JumpSpeedBoost = -1.8f;   // vanilla jumpSpeed 5.01

		// jetpack
		public const float JetThrust = 0.5f;         // upward accel per tick on top of cancelling gravity
		public const float JetMaxRise = 5.5f;
		public const float JetSteer = 0.12f;
		public const float JetBaseFuel = 30f;        // ticks of burn
		public const float JetBaseRegen = 0.45f;     // fuel per tick while not burning (x2 on the ground)
		public const int JetRefuelDelay = 20;        // ticks after burning before refuelling starts

		// impacts: damage = (speed change - threshold)^2 * scale
		public const float ImpactThreshold = 12f;
		public const float ImpactDamageScale = 1.5f;
		public const float KnockdownImpact = 15f;
		public const float KnockdownImpulse = 4f;    // explosion shove that knocks a body flat
		public const float KnockedFriction = 0.4f;   // a body on its back grinds to a halt
		public const int KnockdownTicks = 50;

		// explosions shove things: speed at the blast centre, falling off linearly to the edge
		public const float BlastImpulse = 9f;
	}
}
