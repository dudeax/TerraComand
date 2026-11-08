using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraCommand.Common.Actors
{
	/// <summary>
	/// Unit conversions and Cortex Command constants (see docs/cc-actor-design.md). CC simulates at 60 Hz with 20 px per metre,
	/// the same tick rate as Terraria, so its pixels map 1:1.
	/// </summary>
	public static class CC
	{
		public const float PPM = 20f;
		public const float Dt = 1f / 60f;
		/// <summary>m/s -> px/tick</summary>
		public static float Speed(float mps) => mps * PPM / 60f;
		/// <summary>m/s^2 -> px/tick^2</summary>
		public static float Accel(float mps2) => mps2 * PPM / 3600f;
		/// <summary>px/tick -> m/s</summary>
		public static float ToMps(float pxPerTick) => pxPerTick * 60f / PPM;

		public static readonly float Gravity = Accel(20f);           // scene GlobalAcceleration (0, 20)
		public const float Mass = 150f;                             // Soldier Light: body + head + limbs + jetpack (kg)

		// stability: faster than this and the body is thrown off its feet (StableVelocityThreshold 15, 25 m/s)
		public static readonly float StableVelX = Speed(15f), StableVelY = Speed(25f);
		public const int StableRecoverTicks = 60;                   // StableRecoveryDelay 1000 ms

		// travel impulse damage (ImpulseDamageThreshold 2400; torso GibImpulseLimit 4800), N*s
		public const float ImpulseDamageThreshold = 2400f, GibImpulseLimit = 4800f;

		// jetpack ("Jetpack Heavy" on Soldier Light)
		public const float JetForce = 4050f;                        // 10800/min x 1.25 kg x 18 m/s
		public const float JetThrottleFull = 1.3f, JetThrottleEmpty = 0.7f;
		public const float JetBurstImpulse = 360f;                  // 8 particles x 2 burst scale x 1.25 kg x 18 m/s
		public const float JetBurstFuelTicks = 8f;
		public const float JetTimeTicks = 90f;                      // JumpTime 1.5 s
		public const float JetReplenish = 1.5f;                     // JumpReplenishRate
		public const float JetMinStartTicks = 15f;                  // 250 ms
		public const float JetAngleRange = 0.16f;                   // JumpAngleRange

		// legs (limb path travel speeds in m/s, push forces in N)
		public const float WalkSpeed = 2.5f, RunSpeed = 4.7f, CrawlSpeed = 1.8f, StandSpeed = 0.7f;
		public const float WalkPush = 4400f, RunPush = 4900f, CrawlPush = 4200f, StandPush = 4200f;
		public const float CrouchWalkMultiplier = 0.7f;
		public const float AgainstTravelPush = 0.3f;

		// body lean targets (rad, CC sign: positive = counter-clockwise when facing right)
		public const float WalkLean = -0.1f, RunLean = -0.12f, CrouchLean = -0.8f;

		// hitbox heights for postures (a standing Terraria body is 42)
		public const int StandHeight = 42, CrouchHeight = 32, LyingHeight = 20;

		public const float AimRange = MathHelper.PiOver2;
		public static readonly float LyingFriction = Accel(9f);     // ~0.45 friction x 20 m/s^2
		/// <summary>Blast impulses are tuned for Terraria creatures (gravity 0.4); a CC body under CC gravity gets half.</summary>
		public const float BlastScale = 0.5f;
	}

	public enum ActorStatus : byte { Stable, Unstable, Dying, Dead }

	public enum MoveState : byte { Stand, Walk, Run, Crouch, Prone, Crawl, Jump, NoMove }

	public enum ProneState : byte { NotProne, GoProne, LayingProne }

	/// <summary>One tick of control input, CC's Controller states.</summary>
	public struct ActorInput
	{
		public bool Left, Right, Jump, JumpStart, Crouch, Prone, Fast, SharpAim;
		/// <summary>World-space vector from the body's centre to the aim point (mouse).</summary>
		public Vector2 Aim;
	}

	/// <summary>
	/// A Cortex Command AHuman's body logic, independent of what entity carries it: movement states, leg gait,
	/// jetpack, the torso's rotation spring, stability (ragdoll) and travel-impulse damage. Positions/velocities are in
	/// Terraria pixels and ticks; rotations use Terraria's convention (positive = clockwise on screen).
	/// </summary>
	public class CCActor
	{
		// ---- state
		public Vector2 Vel;                 // px/tick
		public float Rot, AngVel;           // rad (screen-clockwise), rad/s
		public ActorStatus Status = ActorStatus.Stable;
		public MoveState Move = MoveState.Stand;
		public ProneState Prone = ProneState.NotProne;
		public bool Flipped;                // facing left
		public float AimAngle;              // CC aim angle relative to facing: + up, - down, +-pi/2
		public float CrouchAmount;          // 0..1
		public float SharpAim;              // 0..1
		public float Fuel = CC.JetTimeTicks, FuelMax = CC.JetTimeTicks, FuelReplenish = CC.JetReplenish, JetForce = CC.JetForce;
		public bool Jetting, WasJetting, Grounded = true;
		public int UnstableTicks;           // ticks spent unstable (recovery timer)
		public int ProneTicks;
		public float LastImpulse;           // N*s of the last terrain impact (for tests)

		public readonly Gait[] Legs = { new(), new() };   // [0] front (FG), [1] back (BG)

		public int FlipFactor => Flipped ? -1 : 1;
		public bool Lying => Status == ActorStatus.Unstable || Prone != ProneState.NotProne;

		/// <summary>Leg speed scale and whether this body can run (the brain robot is slow).</summary>
		public float SpeedScale = 1f;
		public bool CanRun = true;

		/// <summary>Max health for impulse damage (CC damage is a fraction of max health).</summary>
		public float MaxHealth = 100f;

		// ------------------------------------------------------------ controls -> states

		/// <summary>Runs before physics each tick: movement state, facing, aim, crouch, prone and jetpack (CC PreControllerUpdate).</summary>
		public void Control(in ActorInput input)
		{
			bool active = Status == ActorStatus.Stable;

			// aim: mouse sets the angle directly; aiming behind turns the body around
			if (input.Aim.LengthSquared() > 1f && Status != ActorStatus.Dying && Status != ActorStatus.Dead) {
				bool wantFlip = input.Aim.X < 0;
				if (wantFlip != Flipped && active) {
					Flipped = wantFlip;
					if (Prone == ProneState.GoProne)
						Prone = ProneState.LayingProne;
					foreach (Gait g in Legs)
						g.Terminate();
				}
				float a = MathF.Atan2(-input.Aim.Y, Math.Abs(input.Aim.X)); // + is up
				AimAngle = Math.Clamp(a, -CC.AimRange, CC.AimRange);
			}

			// jetpack (AEJetpack::UpdateBurstState)
			WasJetting = Jetting;
			Jetting = false;
			bool canStart = Fuel > CC.JetMinStartTicks || WasJetting || Fuel >= FuelMax;
			if (Status != ActorStatus.Dying && Status != ActorStatus.Dead && canStart && Fuel > 0f && (input.JumpStart || input.Jump)) {
				Jetting = true;
				if (input.JumpStart) {
					Vel += JetDirection(input) * (CC.JetBurstImpulse / CC.Mass) * (CC.PPM / 60f);
					Fuel -= CC.JetBurstFuelTicks;
				}
				Fuel -= 1f;
				Move = MoveState.Jump;
			}
			else {
				Fuel += FuelReplenish;
				if (Move == MoveState.Jump)
					Move = MoveState.Stand;
			}
			Fuel = Math.Clamp(Fuel, 0f, FuelMax);
			if (Jetting)
				Vel += JetDirection(input) * CC.Accel(JetForce * Throttle() / CC.Mass);

			// prone / crouch / walk / run / crawl
			bool moving = (input.Left || input.Right) && active;
			if (input.Prone && Prone == ProneState.NotProne && active) {
				Prone = ProneState.GoProne;
				ProneTicks = 0;
			}
			if (moving || Move == MoveState.Jump) {
				if (Move != MoveState.Jump || Vel.LengthSquared() < 0.1f) {
					MoveState old = Move;
					Move = Prone != ProneState.NotProne ? MoveState.Crawl
						: input.Fast && !input.Crouch && CanRun ? MoveState.Run
						: MoveState.Walk;
					if (old != Move)
						foreach (Gait g in Legs)
							g.Terminate();
				}
			}
			else if (Move != MoveState.Jump) {
				Move = input.Prone ? (Prone == ProneState.NotProne ? MoveState.Prone : MoveState.NoMove)
					: CrouchAmount >= 0.99f ? MoveState.Crouch
					: MoveState.Stand;
			}
			if (!input.Prone && Prone != ProneState.NotProne)
				Prone = ProneState.NotProne;

			float wantCrouch = input.Crouch && !input.Prone ? 1f : 0f;
			CrouchAmount = Math.Clamp(MathHelper.Lerp(CrouchAmount, wantCrouch, 0.3f), 0f, 1f);
			if (Math.Abs(CrouchAmount - wantCrouch) < 0.01f)
				CrouchAmount = wantCrouch;

			// sharp aim: ramps up after a short delay while steady; the further the cursor, the further it reaches
			bool canSharp = input.SharpAim && active && !input.Jump && Vel.Length() < CC.Speed(5f)
				&& Move is MoveState.Stand or MoveState.Crouch or MoveState.Prone or MoveState.NoMove or MoveState.Walk;
			if (canSharp) {
				float mag = Move == MoveState.Walk ? 0.3f : 1f;
				SharpAim = SharpAim < mag ? SharpAim + (mag - SharpAim) * 0.035f : mag;
			}
			else
				SharpAim = Math.Max(SharpAim * 0.95f - 0.1f, 0f);
		}

		float Throttle()
		{
			float t = 2f * (Fuel / Math.Max(1f, FuelMax)) - 1f;
			return t >= 0 ? 1f + t * (CC.JetThrottleFull - 1f) : 1f + t * (1f - CC.JetThrottleEmpty);
		}

		/// <summary>Direction the jetpack pushes (screen space, unit). CC tilts the nozzle with the aim and the move direction.</summary>
		public Vector2 JetDirection(in ActorInput input)
		{
			float maxAngle = MathHelper.PiOver2 * CC.JetAngleRange;
			bool against = (Flipped && input.Right) || (!Flipped && input.Left);
			float jetAngle = (AimAngle > 0 ? AimAngle * CC.JetAngleRange : -AimAngle * CC.JetAngleRange * 0.5f) - maxAngle;
			if (against)
				jetAngle = -jetAngle;
			// thrust points up, tilted forward by -jetAngle (CC angle, facing space), then rotated with the torso
			float forward = -jetAngle; // >0 tilts toward the facing
			Vector2 dir = new(MathF.Sin(forward) * FlipFactor, -MathF.Cos(forward));
			return dir.RotatedBy(Rot);
		}

		// ------------------------------------------------------------ physics

		/// <summary>
		/// Gravity, leg propulsion and the torso's balance. Call after Control, before the host moves the body through the terrain.
		/// <paramref name="slope"/> is the terrain angle under the feet (rad, + = rising toward the facing).
		/// </summary>
		public void Physics(in ActorInput input)
		{
			Vel.Y += CC.Gravity;

			if (Status == ActorStatus.Stable && Grounded) {
				int moveDir = input.Right ? 1 : input.Left ? -1 : 0;
				float pathSpeed = 0f, push = 0f;
				switch (Move) {
					case MoveState.Walk:
						pathSpeed = CC.WalkSpeed * MathHelper.Lerp(1f, CC.CrouchWalkMultiplier, CrouchAmount);
						push = CC.WalkPush;
						break;
					case MoveState.Run:
						pathSpeed = CC.RunSpeed;
						push = CC.RunPush;
						break;
					case MoveState.Crawl:
						if (Prone == ProneState.LayingProne) {
							pathSpeed = CC.CrawlSpeed;
							push = CC.CrawlPush;
						}
						break;
					case MoveState.Stand:
					case MoveState.Crouch:
					case MoveState.Prone:
						push = CC.StandPush; // legs plant and hold the body still
						break;
				}

				pathSpeed *= SpeedScale;
				float target = moveDir * CC.Speed(pathSpeed);
				// slow bodies move their legs faster to get going (UpdateLimbPathSpeed)
				float maxBody = CC.Speed(pathSpeed) * 0.5f;
				if (pathSpeed > 0 && maxBody > 0) {
					float mult = MathHelper.Lerp(CC.Speed(2f) / maxBody, 1f, Math.Clamp(Math.Abs(Vel.X) / maxBody, 0f, 1f));
					foreach (Gait g in Legs)
						g.SpeedMultiplier = mult;
				}
				if (push > 0f && (pathSpeed == 0f || AnyFootPlanted())) {
					float maxDv = CC.Accel(push / CC.Mass);
					float dv = target - Vel.X;
					// pushing back against the travel direction is weak (CC cuts it to 30%) - you can't brake hard while walking
					if (moveDir != 0 && Math.Sign(dv) != moveDir)
						maxDv *= CC.AgainstTravelPush;
					Vel.X += Math.Clamp(dv, -maxDv, maxDv);
				}
			}
			// a torso lying on the ground drags on it (CC: terrain friction on the body's atoms)
			if (Grounded && (Status == ActorStatus.Unstable || Math.Abs(MathF.Sin(Rot)) > 0.5f)) {
				float f = CC.LyingFriction;
				Vel.X = Math.Abs(Vel.X) <= f ? 0f : Vel.X - Math.Sign(Vel.X) * f;
			}

			UpdateGait(input);
			UpdateRotation();
		}

		bool AnyFootPlanted() => Legs[0].Planted || Legs[1].Planted;

		void UpdateGait(in ActorInput input)
		{
			LimbPath path = Move switch {
				MoveState.Walk => LimbPaths.Walk,
				MoveState.Run => LimbPaths.Run,
				MoveState.Crawl => LimbPaths.Crawl,
				_ => null,
			};
			if (path == null || !Grounded || Status != ActorStatus.Stable) {
				foreach (Gait g in Legs)
					g.Terminate();
				return;
			}
			// legs alternate: one may only restart its stride once the other is half-way through
			for (int i = 0; i < 2; i++) {
				Gait g = Legs[i], other = Legs[1 - i];
				if (g.Path != path)
					g.Begin(path, i == 0 ? 0f : 0.5f);
				if (g.Ended && other.Progress < 0.5f && !other.Ended)
					continue;
				if (g.Ended)
					g.Begin(path, 0f);
				g.Advance(CC.Speed(path.TravelSpeed) * g.SpeedMultiplier);
			}
		}

		/// <summary>The torso's balancing spring (AHuman::Update "Balance stuff").</summary>
		void UpdateRotation()
		{
			// CC works counter-clockwise-positive; convert in and out
			float rot = -Rot;
			while (Math.Abs(rot) > MathHelper.TwoPi)
				rot -= Math.Sign(rot) * MathHelper.TwoPi;
			if (Math.Abs(rot) > MathHelper.Pi) {
				rot -= Math.Sign(rot) * MathHelper.TwoPi;
				if (Status == ActorStatus.Stable)
					Status = ActorStatus.Unstable;
				UnstableTicks = 0;
			}
			float angVel = -AngVel;
			float dt = CC.Dt;

			if (Status == ActorStatus.Stable) {
				if (Prone != ProneState.NotProne) {
					float target = Flipped ? MathHelper.PiOver2 : -MathHelper.PiOver2;
					float diff = target - rot;
					if (Prone == ProneState.GoProne) {
						ProneTicks++;
						if (ProneTicks <= 20) {
							if (Math.Abs(diff) > 0.1f && Math.Abs(diff) < MathHelper.Pi) {
								angVel += diff * 24f * dt;
								Vel.X += CC.Speed((Flipped ? -Math.Abs(diff) : Math.Abs(diff)) / Math.Max(CC.ToMps(Vel.Length()), 4f) * 60f * dt);
							}
						}
						else {
							angVel *= 0.5f;
							Prone = ProneState.LayingProne;
						}
					}
					else if (Math.Abs(diff) > MathHelper.Pi / 16f && Math.Abs(diff) < MathHelper.PiOver2)
						angVel += diff * 39f * dt;
					else if (Math.Abs(angVel) > 0.3f)
						angVel *= MathF.Exp(-10f * dt);
				}
				else {
					float lean = Move switch {
						MoveState.Walk => CC.WalkLean,
						MoveState.Run => CC.RunLean,
						MoveState.Crouch => CC.CrouchLean,
						_ => 0f,
					};
					float target = lean * (AimAngle > 0 ? 1f - AimAngle / MathHelper.PiOver2 : 1f) * FlipFactor;
					if (Move != MoveState.Crouch)
						target += (CC.CrouchLean * FlipFactor - target) * CrouchAmount;
					float diff = rot - target;
					if (Math.Abs(diff) > MathHelper.Pi * 0.75f)
						rot = target;
					else
						angVel = angVel * (0.98f - 0.06f) - diff * 0.5f; // health ratio taken as 1: the spring is per-tick in CC
				}
			}
			else if (Status == ActorStatus.Unstable) {
				float target = Math.Abs(CC.ToMps(Vel.X)) > 1f ? (Flipped ? MathHelper.PiOver2 : -MathHelper.PiOver2)
					: (rot > 0 ? MathHelper.PiOver2 : -MathHelper.PiOver2);
				float diff = target - rot;
				if (Math.Abs(diff) > 0.1f && Math.Abs(diff) < MathHelper.Pi)
					angVel += diff * 3f * dt;
				// CC topples a thrown body through its atoms' collisions; without them, a body on its feet tips over like an
				// inverted pendulum toward the target side, and settles once flat
				if (Grounded) {
					if (Math.Abs(diff) > 0.15f)
						angVel += Math.Sign(diff) * 0.6f;
					else
						angVel *= 0.6f;
				}
			}

			rot += angVel * dt;
			Rot = -rot;
			AngVel = -angVel;
		}

		// ------------------------------------------------------------ after the host moved the body

		/// <summary>Feed back what the terrain did: the velocity after collision and whether the feet are on something.</summary>
		public void AfterMove(Vector2 velAfter, bool grounded)
		{
			Grounded = grounded;
			Vel = velAfter;

			// stability (Actor::Update)
			bool tooFast = Math.Abs(Vel.X) > CC.StableVelX || Math.Abs(Vel.Y) > CC.StableVelY;
			if (Status == ActorStatus.Stable) {
				if (tooFast)
					Status = ActorStatus.Unstable;
				UnstableTicks = 0;
			}
			else if (Status == ActorStatus.Unstable) {
				UnstableTicks++;
				if (UnstableTicks >= CC.StableRecoverTicks && !tooFast)
					Status = ActorStatus.Stable;
			}
		}

		/// <summary>
		/// CC travel impulse damage for a terrain impact that removed <paramref name="speedLost"/> px/tick: only an unstable
		/// (thrown, falling) body is hurt. Returns damage as a fraction of max health; 1+ is lethal (and gibs).
		/// </summary>
		public float ImpactDamage(float speedLost)
		{
			float impulse = CC.Mass * CC.ToMps(speedLost);
			LastImpulse = impulse;
			if (Status != ActorStatus.Unstable || impulse <= CC.ImpulseDamageThreshold)
				return 0f;
			return (impulse - CC.ImpulseDamageThreshold) / (CC.GibImpulseLimit - CC.ImpulseDamageThreshold);
		}

		/// <summary>A hard shove (explosion, hit): changes velocity and may throw the body off its feet.</summary>
		public void Shove(Vector2 dv, bool knockOver)
		{
			Vel += dv;
			if (knockOver && Status == ActorStatus.Stable) {
				Status = ActorStatus.Unstable;
				UnstableTicks = 0;
				AngVel += Math.Sign(dv.X == 0 ? FlipFactor : dv.X) * 4f; // tumble the way it was pushed
			}
		}

		/// <summary>Hitbox height for the current posture.</summary>
		public int WantedHeight()
		{
			if (Status == ActorStatus.Stable && Prone == ProneState.NotProne)
				return (int)MathF.Round(MathHelper.Lerp(CC.StandHeight, CC.CrouchHeight, CrouchAmount));
			float lying = Math.Abs(MathF.Sin(Rot));
			return (int)MathF.Round(MathHelper.Lerp(CC.StandHeight, CC.LyingHeight, Math.Clamp((lying - 0.3f) / 0.5f, 0f, 1f)));
		}
	}

	// ------------------------------------------------------------ limb paths and gait

	/// <summary>A CC LimbPath: a foot's route relative to the hip, in pixels (x toward the facing, y down).</summary>
	public sealed class LimbPath
	{
		public readonly Vector2 Start;
		public readonly Vector2[] Segs;
		public readonly float TravelSpeed;  // m/s
		public readonly float Length;
		/// <summary>The path is laid along the body (crawling): its "down" strokes push the body forward.</summary>
		public bool AlongBody;

		public LimbPath(Vector2 start, float travelSpeed, params Vector2[] segs)
		{
			Start = start;
			Segs = segs;
			TravelSpeed = travelSpeed;
			foreach (Vector2 s in segs)
				Length += s.Length();
		}

		/// <summary>Point at a distance along the path.</summary>
		public Vector2 At(float dist, out int seg)
		{
			Vector2 p = Start;
			for (seg = 0; seg < Segs.Length; seg++) {
				float l = Segs[seg].Length();
				if (dist <= l)
					return p + Segs[seg] * (l > 0 ? dist / l : 0f);
				dist -= l;
				p += Segs[seg];
			}
			seg = Segs.Length - 1;
			return p;
		}
	}

	public static class LimbPaths
	{
		// Data/Base.rte/Actors/Shared.ini (CC Community Project, AGPL-3.0)
		public static readonly LimbPath Walk = new(new(10, -2), 2.5f,
			new(0, 3), new(0, 3), new(0, 5), new(0, 3), new(-3, 2), new(-5, 1), new(-5, 1), new(-6, 1), new(-4, 0),
			new(0, -2), new(3, -3), new(4, -2), new(5, -1));
		public static readonly LimbPath Run = new(new(13, -2), 4.7f,
			new(0, 3), new(0, 3), new(0, 5), new(0, 3), new(-5, 2), new(-6, 1), new(-6, 1), new(-6, 1), new(-4, -1),
			new(0, -2), new(3, -3), new(4, -2), new(5, -1));
		public static readonly LimbPath Crawl = new(new(-10, -8), 1.8f,
			new(8, 0), new(5, 0), new(3, 2), new(1, 8), new(0, 10), new(-4, 0), new(0, -10)) { AlongBody = true };
		/// <summary>Where a standing foot rests (Soldier Stand Path is a static point under the hip).</summary>
		public static readonly Vector2 Stand = new(0, 12);
		public static readonly Vector2 Crouch = new(-4, 7);
	}

	/// <summary>A foot's progress along a limb path.</summary>
	public sealed class Gait
	{
		public LimbPath Path;
		public float Dist;
		public float SpeedMultiplier = 1f;
		public bool Ended = true;
		public int Segment;

		public float Progress => Path == null || Path.Length <= 0 ? 0f : Dist / Path.Length;

		/// <summary>On a segment where the foot sweeps backwards, i.e. pushing the body along.</summary>
		public bool Planted => Path != null && !Ended && (Path.AlongBody ? Path.Segs[Segment].Y > 0 : Path.Segs[Segment].X < 0);

		public void Begin(LimbPath path, float progress)
		{
			Path = path;
			Dist = progress * path.Length;
			Ended = false;
			Path.At(Dist, out Segment);
		}

		public void Advance(float px)
		{
			if (Path == null || Ended)
				return;
			Dist += px;
			if (Dist >= Path.Length) {
				Dist = Path.Length;
				Ended = true;
			}
			Path.At(Dist, out Segment);
		}

		public void Terminate()
		{
			Ended = true;
			Path = null;
		}

		/// <summary>Foot position relative to the hip (x toward the facing), or the resting point.</summary>
		public Vector2 Foot(Vector2 rest) => Path == null ? rest : Path.At(Dist, out _);
	}
}
