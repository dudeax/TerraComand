using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace TerraCommand.Common.Actors
{
	public enum ActorKind : byte { Soldier, Brain, Light, Heavy, Crab, Turret }

	/// <summary>
	/// Unit conversions and Cortex Command constants (see docs/cc-actor-design.md). CC simulates at 60 Hz with 20 px per metre,
	/// the same tick rate as Terraria; one CC pixel occupies one 2px Terraria art pixel.
	/// </summary>
	public static class CC
	{
		public const int WorldScale = 2;
        public const float PPM = 20f * WorldScale;
        public const float TerrainCellArea = 4f / (WorldScale * WorldScale);
		public const float Dt = 1f / 60f;
		/// <summary>m/s -> px/tick</summary>
		public static float SourceSpeed(float mps) => mps * 20f / 60f;
        public static float Speed(float mps) => mps * PPM / 60f;
		/// <summary>m/s^2 -> px/tick^2</summary>
		public static float Accel(float mps2) => mps2 * PPM / 3600f;
		/// <summary>px/tick -> m/s</summary>
		public static float ToMps(float pxPerTick) => pxPerTick * 60f / PPM;

		public static readonly float Gravity = Accel(20f);           // scene GlobalAcceleration (0, 20)
		// Soldier Light: torso, head, helmet, arms, legs, feet and jetpack; carried items add to it.
		public const float Mass = 48f + 32f + 4f + 2f * 8f + 2f * 12f + 2f * 4f + 1f;

		// stability: faster than this and the body is thrown off its feet (StableVelocityThreshold 15, 25 m/s)
		public static readonly float StableVelX = Speed(15f), StableVelY = Speed(25f);
		public const int StableRecoverTicks = 60;                   // StableRecoveryDelay 1000 ms

		// travel impulse damage (ImpulseDamageThreshold 2400; torso GibImpulseLimit 4800), N*s
		public const float ImpulseDamageThreshold = 2400f, GibImpulseLimit = 4800f;

		// jetpack ("Jetpack Heavy" on Soldier Light)
		// Resolved Coalition Light: 12000/min across the three inherited emissions.
		public const float JetForce = (12000f / 60f / 3f) * 1.25f * (18f + 17.5f + 15f);
		public const float JetThrottleFull = 1.3f, JetThrottleEmpty = 0.7f;
		// BurstSize=8 resolves to 3 particles per emission, with velocity scale 2.
		public const float JetBurstImpulse = 3f * 2f * 1.25f * (18f + 17.5f + 15f);
		public const float JetBurstFuelTicks = 9f;
		public const int JetBurstSpacingTicks = 10; // 160 ms, tested at 60 Hz
		public const int SharpAimDelayTicks = 15; // Coalition Light: 250 ms
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
		public const int StandHeight = 42 * WorldScale, CrouchHeight = 32 * WorldScale, LyingHeight = 20 * WorldScale;
		/// <summary>A CC actor stands on a narrow footprint; Terraria's 20px player would bridge holes its feet should step into.</summary>
		public const int BodyWidth = 14 * WorldScale;

		public const float AimRange = MathHelper.PiOver2;
		public static readonly float LyingFriction = Accel(9f);     // ~0.45 friction x 20 m/s^2
		/// <summary>Blast impulses are tuned for Terraria creatures (gravity 0.4); a CC body under CC gravity gets half.</summary>
		public const float BlastScale = 0.5f * WorldScale;
	}

	/// <summary>What sets the actor types apart (CC: each actor's Mass, health, jetpack and limbs).</summary>
	public sealed class ActorStats
	{
		public ActorKind Kind;
		public float Mass = CC.Mass, LifeScale = 1f, Fuel = CC.JetTimeTicks, JetForce = CC.JetForce, Speed = 1f;
		public bool CanRun = true, CanJet = true, CanCrouch = true, Moves = true;
		public int StandHeight = CC.StandHeight;

		static readonly ActorStats soldier = new(), brain = new() { Kind = ActorKind.Brain, Fuel = 0, CanJet = false, Speed = 0.6f, CanRun = false },
			light = new() { Kind = ActorKind.Light, Mass = 110f, LifeScale = 0.8f, Fuel = 110f, JetForce = 2900f, Speed = 1.15f },
			heavy = new() { Kind = ActorKind.Heavy, StandHeight = 44 * CC.WorldScale, Mass = 200f, LifeScale = 1.6f, Fuel = 60f, JetForce = 5800f, Speed = 0.85f },
			crab = new() { Kind = ActorKind.Crab, Mass = 120f, LifeScale = 1.2f, Fuel = 0, CanJet = false, CanRun = false, CanCrouch = false, Speed = 0.9f, StandHeight = 22 * CC.WorldScale },
			turret = new() { Kind = ActorKind.Turret, Mass = 300f, LifeScale = 2f, Fuel = 0, CanJet = false, CanRun = false, CanCrouch = false, Moves = false, Speed = 0f, StandHeight = 26 * CC.WorldScale };

		public static ActorStats Get(ActorKind k) => k switch {
			ActorKind.Brain => brain, ActorKind.Light => light, ActorKind.Heavy => heavy,
			ActorKind.Crab => crab, ActorKind.Turret => turret, _ => soldier,
		};
	}

	public enum ActorStatus : byte { Stable, Unstable, Dying, Dead }

	public enum MoveState : byte { Stand, Walk, Run, Crouch, Prone, Crawl, Jump, NoMove }

	public enum ProneState : byte { NotProne, GoProne, LayingProne }

	/// <summary>One tick of control input, CC's Controller states.</summary>
	public struct ActorInput
	{
		public bool Left, Right, Jump, JumpStart, Crouch, Prone, Fast, SharpAim, Reloading, HoldingDevice;
		/// <summary>World-space vector from the body's centre to the aim point (mouse).</summary>
		public Vector2 Aim;
		public float AutoCrouch;
	}

	/// <summary>
	/// A Cortex Command AHuman's body logic, independent of what entity carries it: movement states, leg gait,
	/// jetpack, the torso's rotation spring, stability (ragdoll) and travel-impulse damage. Positions/velocities are in
	/// Terraria pixels and ticks; rotations use Terraria's convention (positive = clockwise on screen).
	/// </summary>
	public class CCActor
	{
		public ActorKind Kind;
		public bool ContactPhysics;
		public readonly ActorContactState Contacts = new();

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
		public int BurstCooldown, SharpAimTicks, SharpAimRevertTicks = CC.SharpAimDelayTicks;
		public float Health = 100f;
		public float GroundSlope; // screen-space terrain angle, clamped to +/-40 degrees
		public Vector2 StrideScale = Vector2.One;
		public float LastImpulse;           // N*s of the last terrain impact (for tests)

		public readonly Gait[] Hands = { new(), new() };
		public ulong BodyId=BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(),0);
		public readonly LimbState Limbs=new();
		public readonly ActorWounds Wounds = new();
		public bool FrontArmFree = true;
		public readonly Gait[] Legs = { new(), new() };   // [0] front (FG), [1] back (BG)

		public int FlipFactor => Flipped ? -1 : 1;
		public bool Lying => Status == ActorStatus.Unstable || Prone != ProneState.NotProne;

		/// <summary>Total mass (kg): the body plus what it carries. Legs and jetpack push against all of it (CC).</summary>
		public float Mass = CC.Mass;

		/// <summary>Leg speed scale and whether this body can run (the brain robot is slow).</summary>
		public float SpeedScale = 1f;
		public bool CanRun = true, CanCrouch = true;
		public int StandHeight = CC.StandHeight;

		/// <summary>Takes on an actor type's build (mass, legs, jetpack, height). Carried mass is added by the host.</summary>
		public void Apply(ActorStats s, float carriedMass)
		{
			if (Kind != s.Kind) Contacts.Reset();
			Kind = s.Kind;
			Mass = Math.Max(1,s.Mass + carriedMass-Limbs.LostMass(s.Kind));
			SpeedScale = s.Speed;
			CanRun = s.CanRun;
			CanCrouch = s.CanCrouch;
			StandHeight = s.StandHeight;
			if (!s.CanJet)
				FuelMax = 0f;
		}

		/// <summary>Max health for impulse damage (CC damage is a fraction of max health).</summary>
		public float MaxHealth = 100f;

		// ------------------------------------------------------------ controls -> states

		/// <summary>Runs before physics each tick: movement state, facing, aim, crouch, prone and jetpack (CC PreControllerUpdate).</summary>
		public void Control(in ActorInput inputIn)
		{
			ActorInput input = inputIn;
			if(Kind!=ActorKind.Crab && Kind!=ActorKind.Turret && Limbs.Legs==0) input.Prone=true;
			if(!Limbs.Arms && Kind!=ActorKind.Crab) input.HoldingDevice=false;
			FrontArmFree = !input.HoldingDevice;
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

			// jetpack (AEJetpack::UpdateBurstState + AEmitter burst spacing)
			if (BurstCooldown > 0)
				BurstCooldown--;
			float throttle = Throttle();
			bool burst = false;
			WasJetting = Jetting;
			Jetting = false;
			bool canStart = Fuel > CC.JetMinStartTicks || WasJetting || Fuel >= FuelMax;
			if (Status != ActorStatus.Dying && Status != ActorStatus.Dead && canStart && Fuel > 0f && (input.JumpStart || input.Jump)) {
				Jetting = true;
				if (input.JumpStart) {
					burst = BurstCooldown == 0;
					if (burst) {
						Vel += JetDirection(input) * (CC.JetBurstImpulse / Mass) * (CC.PPM / 60f);
						BurstCooldown = CC.JetBurstSpacingTicks;
					}
					Fuel -= CC.JetBurstFuelTicks * (burst ? 1f : 0.5f);
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
				Vel += JetDirection(input) * CC.Accel(JetForce * throttle * (burst ? 2f : 1f) / Mass);

			// prone / crouch / walk / run / crawl
			bool moving = (input.Left || input.Right) && active;
			if (!CanCrouch) {
				input.Prone = false;   // drones and turrets don't crouch or lie down
				input.Crouch = false;
			}
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

			float wantCrouch = input.Prone ? 0 : Math.Max(input.Crouch ? 1f : 0f, input.AutoCrouch);
			CrouchAmount = Math.Clamp(MathHelper.Lerp(CrouchAmount, wantCrouch, 0.3f), 0f, 1f);
			if (Math.Abs(CrouchAmount - wantCrouch) < 0.01f)
				CrouchAmount = wantCrouch;

			// sharp aim: ramps up after a short delay while steady; the further the cursor, the further it reaches
			bool canSharp = input.SharpAim && input.HoldingDevice && active && !input.Jump && Vel.Length() < CC.Speed(5f)
				&& Move is MoveState.Stand or MoveState.Crouch or MoveState.Prone or MoveState.NoMove or MoveState.Walk;
			if (canSharp) {
				SharpAimTicks++;
				float mag = Move == MoveState.Walk ? 0.3f : 1f;
				if (SharpAimTicks > CC.SharpAimDelayTicks) {
					SharpAim = SharpAim < mag ? SharpAim + (mag - SharpAim) * 0.035f : mag;
					SharpAimRevertTicks = 0;
				}
				else {
					SharpAim *= 0.95f;
					SharpAimRevertTicks = CC.SharpAimDelayTicks - SharpAimTicks;
				}
			}
			else {
				SharpAim = Math.Max(SharpAim * 0.95f - 0.1f, 0f);
				SharpAimRevertTicks++;
				SharpAimTicks = SharpAimRevertTicks > CC.SharpAimDelayTicks ? 0 : CC.SharpAimDelayTicks - SharpAimRevertTicks;
			}
			if (input.Reloading) {
				SharpAim = 0;
				SharpAimTicks = 0;
			}
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
			float cos = MathF.Cos(Rot), sin = MathF.Sin(Rot);
			return new Vector2(dir.X * cos - dir.Y * sin, dir.X * sin + dir.Y * cos);
		}

		// ------------------------------------------------------------ physics

		/// <summary>
		/// Gravity, leg propulsion and the torso's balance. Call after Control, before the host moves the body through the terrain.
		/// <paramref name="slope"/> is the terrain angle under the feet (rad, + = rising toward the facing).
		/// </summary>
		public void Physics(in ActorInput input)
		{
			Vel.Y += CC.Gravity;

			if (!ContactPhysics && Status == ActorStatus.Stable && Grounded) {
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
					float fraction = Math.Clamp(Math.Abs(Vel.X) / maxBody, 0f, 1f);
					StrideScale = new Vector2(MathHelper.Lerp(0.8f, 1.2f, fraction), MathHelper.Lerp(0.9f, 1f, fraction));
					float crouchSpeed = Move == MoveState.Walk ? MathHelper.Lerp(1f, CC.CrouchWalkMultiplier, CrouchAmount) : 1f;
					float mult = crouchSpeed * MathHelper.Lerp(CC.Speed(2f) / maxBody, 1f, fraction);
					foreach (Gait g in Legs)
						g.SpeedMultiplier = mult;
				}
				// CC calls PushAsLimb separately for each supporting leg. Both may contribute in double support.
				int supports = pathSpeed == 0f ? 2 : PlantedFeet();
				float armPush = Move == MoveState.Crawl ? 3400f * ((Hands[0].Planted ? 1 : 0) + (Hands[1].Planted ? 1 : 0)) : 0f;
				if (push * supports + armPush > 0) {
					float maxDv = CC.Accel((push * supports + armPush) / Mass);
					float dv = target - Vel.X;
					// pushing back against the travel direction is weak (CC cuts it to 30%) - you can't brake hard while walking
					if (moveDir != 0 && Math.Sign(dv) != moveDir)
						maxDv *= CC.AgainstTravelPush;
					Vel.X += Math.Clamp(dv, -maxDv, maxDv);
				}
			}
			// a torso lying on the ground drags on it (CC: terrain friction on the body's atoms)
			if (!ContactPhysics && Grounded && (Status == ActorStatus.Unstable || Math.Abs(MathF.Sin(Rot)) > 0.5f)) {
				float f = CC.LyingFriction;
				Vel.X = Math.Abs(Vel.X) <= f ? 0f : Vel.X - Math.Sign(Vel.X) * f;
			}

			if (ContactPhysics) UpdateContactPathSpeed();
			UpdateGait(input);
			UpdateRotation();
		}

		void UpdateContactPathSpeed()
		{
			float travel = Kind == ActorKind.Crab ? LimbPaths.Crab.TravelSpeed : Move switch {
				MoveState.Walk => CC.WalkSpeed, MoveState.Run => CC.RunSpeed, MoveState.Crawl => CC.CrawlSpeed, _ => 0
			};
			if (travel <= 0) return;
			float crouch = Move == MoveState.Walk ? MathHelper.Lerp(1, CC.CrouchWalkMultiplier, CrouchAmount) : 1;
			float maxBody = travel * SpeedScale * crouch * 0.5f;
			if (maxBody <= 0) return;
			float fraction = Math.Clamp(Math.Abs(CC.ToMps(Vel.X)) / maxBody, 0, 1);
			StrideScale = new Vector2(MathHelper.Lerp(0.8f, 1.2f, fraction), MathHelper.Lerp(0.9f, 1, fraction));
			foreach (Gait leg in Legs) leg.SpeedMultiplier = crouch * MathHelper.Lerp(2 / maxBody, 1, fraction);
			if (Kind == ActorKind.Crab) foreach (Gait leg in Hands) leg.SpeedMultiplier = Legs[0].SpeedMultiplier;
		}

		int PlantedFeet() => (Legs[0].Planted ? 1 : 0) + (Legs[1].Planted ? 1 : 0);

		void UpdateGait(in ActorInput input)
		{
			LimbPath path = Move switch {
				MoveState.Walk => LimbPaths.Walk,
				MoveState.Run => LimbPaths.Run,
				MoveState.Crawl => LimbPaths.Crawl,
				_ => null,
			};
			if (path == null || !ContactPhysics && !Grounded || Status != ActorStatus.Stable) {
				foreach (Gait g in Legs)
					g.Terminate();
				foreach (Gait hand in Hands) hand.Terminate();
				return;
			}
			if (Kind == ActorKind.Crab) path = LimbPaths.Crab;
			AdvancePair(Legs, path);
			if (Kind == ActorKind.Crab) AdvancePair(Hands, path);
			else if (Move == MoveState.Crawl && Prone == ProneState.LayingProne)
				AdvancePair(Hands, LimbPaths.ArmCrawl, !FrontArmFree);
			else foreach (Gait hand in Hands) hand.Terminate();
		}

		void AdvancePair(Gait[] limbs, LimbPath path, bool frontOccupied = false)
		{
			// A limb restarts once its partner has reached half a stroke.
			for (int i = 0; i < 2; i++) {
				Gait g = limbs[i], other = limbs[1 - i];
				if (i == 0 && frontOccupied) { g.Terminate(); continue; }
				if (g.Path != path)
					g.Begin(path, (i == 0 ? 0f : 0.5f) + (Kind == ActorKind.Crab && ReferenceEquals(limbs, Hands) ? 0.25f : 0));
				if (g.Ended && other.RegularProgress < 0.5f && !(i == 0 && (other.Ended || Vel.Length() < CC.Speed(0.5f))))
					continue;
				if (g.Ended)
					g.Begin(path, 0f);
				if (!ContactPhysics) g.Advance(CC.Speed(path.TravelSpeed) * SpeedScale * g.SpeedMultiplier);
			}
		}

		public void AdvanceRemoteGait(Vector2 velocity)
		{
			Vel = velocity;
			UpdateGait(default);
		}

		public void WriteGait(BinaryWriter writer) => GaitState.Capture(this).Write(writer);
		public void ReadGait(BinaryReader reader) => GaitState.Read(reader).Apply(this);

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
						angVel = angVel * (0.98f - 0.06f * Math.Clamp(Health / Math.Max(1f, MaxHealth), 0f, 1f)) - diff * 0.5f; // the reference spring is per-tick
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
				if (!ContactPhysics && Grounded) {
					if (Math.Abs(diff) > 0.15f)
						angVel += Math.Sign(diff) * 0.6f;
					else
						angVel *= 0.6f;
				}
			}

			if (!ContactPhysics) rot += angVel * dt;
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
			float impulse = Mass * CC.ToMps(speedLost);
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

		public int WantedWidth()
		{
			if (Kind == ActorKind.Crab) return ActorSpriteMasks.Get(Kind,"Body").Width * CC.WorldScale;
            if (Kind == ActorKind.Turret) return Math.Max(ActorSpriteMasks.Get(Kind,"Base").Width,ActorSpriteMasks.Get(Kind,"Head").Width) * CC.WorldScale;
			if (Status == ActorStatus.Stable && Prone == ProneState.NotProne)
				return CC.BodyWidth;
			return (int)MathF.Round(CC.BodyWidth + (StandHeight - CC.BodyWidth) * Math.Abs(MathF.Sin(Rot)));
		}

		/// <summary>Hitbox height for the current posture.</summary>
		public int WantedHeight()
		{
			if (Status == ActorStatus.Stable && Prone == ProneState.NotProne)
				return (int)MathF.Round(MathHelper.Lerp(StandHeight, Math.Min(StandHeight, CC.CrouchHeight), CrouchAmount));
			float thickness = Math.Min(StandHeight, CC.LyingHeight);
			return (int)MathF.Round(thickness + (StandHeight - thickness) * Math.Abs(MathF.Cos(Rot)));
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
		public int EndSegments, StartSegments;

		public LimbPath(Vector2 start, float travelSpeed, params Vector2[] segs)
		{
			Start = start * CC.WorldScale;
			Segs = Array.ConvertAll(segs, v => v * CC.WorldScale);
			TravelSpeed = travelSpeed;
			foreach (Vector2 s in Segs)
				Length += s.Length();
		}

		public Vector2 SegmentTarget(int segment) {
			Vector2 target = Start;
			for (int i = 0; i <= segment; i++) target += Segs[i];
			return target;
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
			new(0, -2), new(3, -3), new(4, -2), new(5, -1)) { EndSegments = 4, StartSegments = 3 };
		public static readonly LimbPath Run = new(new(13, -2), 4.7f,
			new(0, 3), new(0, 3), new(0, 5), new(0, 3), new(-5, 2), new(-6, 1), new(-6, 1), new(-6, 1), new(-4, -1),
			new(0, -2), new(3, -3), new(4, -2), new(5, -1)) { EndSegments = 4, StartSegments = 3 };
		public static readonly LimbPath Crawl = new(new(-10, -8), 1.8f,
			new(8, 0), new(5, 0), new(3, 2), new(1, 8), new(0, 10), new(-4, 0), new(0, -10)) { AlongBody = true, EndSegments = 2, StartSegments = 2 };
		/// <summary>Where a standing foot rests (Soldier Stand Path is a static point under the hip).</summary>
		public static readonly LimbPath Crab = new(new(0, -9), 3,
			new(15, 6), new(-2, 6), new(-1, 3), new(-2, 2), new(-10, 0), new(-10, 0), new(6, -3), new(6, -3)) { EndSegments = 2, StartSegments = 2 };
		public static readonly LimbPath ArmCrawl = new(new(-8, -6), 1.5f,
			new(0, -4), new(3, -4), new(5, -1), new(5, 1), new(1, 8), new(-2, 10)) { AlongBody = true, StartSegments = 3 };
		public static readonly Vector2 Stand = new Vector2(0, 12) * CC.WorldScale;
		public static readonly Vector2 Crouch = new Vector2(-4, 7) * CC.WorldScale;
	}

	/// <summary>A value snapshot can be fully consumed even when an inactive/own actor ignores the packet.</summary>
	public readonly record struct GaitState(MoveState Move, bool Grounded, byte Front, byte Back, float Slope,
		Vector2 Scale, float Speed, bool FrontArmFree, byte FrontHand, byte BackHand)
	{
		static byte Phase(Gait limb) => (byte)Math.Clamp((int)(limb.Progress * 255), 0, 255);
		public static GaitState Capture(CCActor actor) => new(actor.Move, actor.Grounded,
			Phase(actor.Legs[0]), Phase(actor.Legs[1]), actor.GroundSlope, actor.StrideScale,
			actor.Legs[0].SpeedMultiplier, actor.FrontArmFree, Phase(actor.Hands[0]), Phase(actor.Hands[1]));

		public void Write(BinaryWriter writer)
		{
			writer.Write((byte)Move); writer.Write(Grounded); writer.Write(Front); writer.Write(Back);
			writer.Write(Slope); writer.Write(Scale.X); writer.Write(Scale.Y); writer.Write(Speed);
			writer.Write(FrontArmFree); writer.Write(FrontHand); writer.Write(BackHand);
		}
		public static GaitState Read(BinaryReader reader) => new((MoveState)reader.ReadByte(), reader.ReadBoolean(),
			reader.ReadByte(), reader.ReadByte(), reader.ReadSingle(), new Vector2(reader.ReadSingle(), reader.ReadSingle()),
			reader.ReadSingle(), reader.ReadBoolean(), reader.ReadByte(), reader.ReadByte());

		public void Apply(CCActor actor)
		{
			actor.Move = Move; actor.Grounded = Grounded; actor.GroundSlope = Slope;
			actor.StrideScale = Scale; actor.FrontArmFree = FrontArmFree;
			LimbPath path = Move switch { MoveState.Walk when actor.Kind == ActorKind.Crab => LimbPaths.Crab, MoveState.Walk => LimbPaths.Walk, MoveState.Run => LimbPaths.Run, MoveState.Crawl => LimbPaths.Crawl, _ => null };
			for (int i = 0; i < 2; i++) {
				actor.Legs[i].SpeedMultiplier = Speed;
				if (path == null) actor.Legs[i].Terminate();
				else actor.Legs[i].Begin(path, (i == 0 ? Front : Back) / 255f);
				if (actor.Kind == ActorKind.Crab && path != null) actor.Hands[i].Begin(path, (i == 0 ? FrontHand : BackHand) / 255f);
				else if (Move != MoveState.Crawl || i == 0 && !FrontArmFree) actor.Hands[i].Terminate();
				else actor.Hands[i].Begin(LimbPaths.ArmCrawl, (i == 0 ? FrontHand : BackHand) / 255f);
			}
		}
	}

	/// <summary>A foot's progress along a limb path.</summary>
	public sealed class Gait
	{
		public LimbPath Path;
		public float Dist;
		public float SpeedMultiplier = 1f;
		public bool Ended = true;
		public int Segment;
		public int Cycle;

		public float RegularProgress {
			get {
				if (Path == null || Ended) return 0;
				float initial = 0;
				for (int i = 0; i < Path.StartSegments; i++) initial += Path.Segs[i].Length();
				return (Dist - initial) / Math.Max(0.01f, Path.Length - initial);
			}
		}

		public float Progress => Path == null || Path.Length <= 0 ? 0f : Dist / Path.Length;

		/// <summary>On a segment where the foot sweeps backwards, i.e. pushing the body along.</summary>
		public bool Planted => Path != null && !Ended && (Path.AlongBody ? Path.Segs[Segment].Y > 0 : Path.Segs[Segment].X < 0);

		public void Begin(LimbPath path, float progress)
		{
			Path = path;
			Cycle++;
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

		/// <summary>CC LimbPath::ReportProgress: follow actual limb position, searching only forward.</summary>
		public void ReportPosition(Vector2 position) {
			if (Path == null || Ended) return;
			bool collisionsDisabled = Path.EndSegments > 0 && Path.Segs.Length - Segment <= Path.EndSegments;
			int closest = Segment;
			float distance = float.MaxValue;
			for (int i = Segment; i < Path.Segs.Length; i++) {
				if (!collisionsDisabled && Path.EndSegments > 0 && Path.Segs.Length - i <= Path.EndSegments) break;
				float candidate = Vector2.DistanceSquared(position, Path.SegmentTarget(i));
				if (candidate < distance) { distance = candidate; closest = i; }
			}
			Segment = closest;
			if (distance < 2.5f * 2.5f) {
				if (Segment == Path.Segs.Length - 1) Ended = true;
				else Segment++;
			}
			Dist = 0;
			for (int i = 0; i < Segment; i++) Dist += Path.Segs[i].Length();
			float length = Path.Segs[Segment].Length();
			Dist += Math.Max(0, length - Vector2.Distance(position, Path.SegmentTarget(Segment)));
			if (Ended) Dist = Path.Length;
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
