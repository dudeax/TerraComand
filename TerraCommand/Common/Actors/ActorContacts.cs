using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace TerraCommand.Common.Actors
{
	public readonly record struct ContactSurface(float Friction, float Restitution, bool OneWay = false, float Top = 0);
	public interface IActorTerrain { bool Sample(Vector2 point, out ContactSurface surface); }
	internal sealed class ShiftedActorTerrain : IActorTerrain
	{
		public IActorTerrain Source;
		public Vector2 Origin;
		public bool Sample(Vector2 point, out ContactSurface surface) {
			bool solid = Source.Sample(point + Origin, out surface);
			if (surface.OneWay) surface = surface with { Top = surface.Top - Origin.Y };
			return solid;
		}
	}

	public sealed class ContactLimb
	{
		public Vector2 Position, Velocity, Normal;
		public bool Supported;
		internal LimbPath Path;
		internal int PathCycle;
		internal int StalledTicks;
	}
	public sealed class ActorContactState
	{
		public bool Initialized, Grounded, Stuck;
		public Vector2 Anchor, TravelImpulse;
		public int ContactCount;
		public readonly ContactLimb[] Limbs = { new(), new(), new(), new() };
		internal readonly ShiftedActorTerrain TerrainFrame = new();
		internal readonly Vector2[] Atoms = new Vector2[1024];
		public void Reset() { Initialized = false; Stuck = false; foreach (ContactLimb limb in Limbs) { limb.Supported = false; limb.Velocity = Vector2.Zero; limb.Path = null; limb.StalledTicks = 0; } }
		public void BindPaths(CCActor actor) {
			if (!Initialized) return;
			for (int i = 0; i < Limbs.Length; i++) {
				Gait gait = i < 2 ? actor.Legs[i] : actor.Hands[i - 2];
				Limbs[i].Path = gait.Path; Limbs[i].PathCycle = gait.Cycle;
			}
		}

	}

	public readonly record struct ContactSnapshot(bool Initialized, Vector2 Anchor, Vector2 A, Vector2 B, Vector2 C, Vector2 D,
		Vector2 VA, Vector2 VB, Vector2 VC, Vector2 VD)
	{
		public static ContactSnapshot Capture(ActorContactState s) => new(s.Initialized, s.Anchor, s.Limbs[0].Position,
			s.Limbs[1].Position, s.Limbs[2].Position, s.Limbs[3].Position, s.Limbs[0].Velocity, s.Limbs[1].Velocity, s.Limbs[2].Velocity, s.Limbs[3].Velocity);
		public void Apply(ActorContactState s) {
			s.Reset(); s.Initialized = Initialized; s.Anchor = Anchor;
			s.Limbs[0].Position = A; s.Limbs[1].Position = B; s.Limbs[2].Position = C; s.Limbs[3].Position = D;
			s.Limbs[0].Velocity = VA; s.Limbs[1].Velocity = VB; s.Limbs[2].Velocity = VC; s.Limbs[3].Velocity = VD;
		}
		public void Write(BinaryWriter w) {
			w.Write(Initialized);
			void V(Vector2 p) { w.Write(p.X); w.Write(p.Y); }
			V(Anchor); V(A); V(B); V(C); V(D); V(VA); V(VB); V(VC); V(VD);
		}
		public static ContactSnapshot Read(BinaryReader r) {
			Vector2 V() => new(r.ReadSingle(), r.ReadSingle());
			return new(r.ReadBoolean(), V(), V(), V(), V(), V(), V(), V(), V(), V());
		}
	}

	/// <summary>
	/// Terrain-travel and constrained limb groups: swept sprite-alpha atoms, mass/inertia contact impulses,
	/// Coulomb friction, and force-limited reaction impulses at limb joints (CC AtomGroup::Travel/PushAsLimb).
	/// Velocities are pixels/tick; angular velocity is radians/tick inside the solver.
	/// </summary>
	public static class ActorContacts
	{
		const float Skin = 0.05f, StepLength = 0.75f;
		public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
		public static Vector2 Perpendicular(Vector2 a) => new(-a.Y, a.X);
		public static float EffectiveMass(float mass, float inertia, Vector2 radius, Vector2 normal) {
			float arm = Cross(radius, normal);
			return 1f / (1f / Math.Max(1, mass) + arm * arm / Math.Max(1, inertia));
		}
		public static void ApplyImpulse(ref Vector2 velocity, ref float omega, Vector2 impulse, Vector2 radius, float mass, float inertia) {
			velocity += impulse / mass;
			omega += Cross(radius, impulse) / inertia;
		}
		static Vector2 CentreOffset(CCActor a, float rot) => a.Kind == ActorKind.Crab || a.Kind == ActorKind.Turret
			? new Vector2(0, -12) : ContactGeometry.HipOffset(a, rot) + ContactGeometry.Rotate(new Vector2(0, -8), rot);
		static bool Solid(IActorTerrain terrain, Vector2 point, Vector2 previous, bool drop, out ContactSurface material) {
			if (!terrain.Sample(point, out material)) return false;
			return !material.OneWay || !drop && point.Y >= previous.Y && previous.Y <= material.Top + Skin;
		}
		static Vector2 Normal(IActorTerrain terrain, Vector2 point, Vector2 previous, bool drop) {
			bool S(Vector2 p) => Solid(terrain, p, previous, drop, out _);
			Vector2 gradient = new((S(point - Vector2.UnitX) ? 1 : 0) - (S(point + Vector2.UnitX) ? 1 : 0),
				(S(point - Vector2.UnitY) ? 1 : 0) - (S(point + Vector2.UnitY) ? 1 : 0));
			if (gradient.LengthSquared() > 0) return Vector2.Normalize(gradient);
			return ContactGeometry.Normalized(previous - point, -Vector2.UnitY);
		}
		public static Vector2 SweepPoint(IActorTerrain terrain, Vector2 from, Vector2 delta, bool drop, out Vector2 normal, out ContactSurface material) {
			normal = Vector2.Zero; material = default;
			int steps = Math.Max(1, (int)MathF.Ceiling(delta.Length() / StepLength));
			Vector2 result = from;
			for (int i = 1; i <= steps; i++) {
				Vector2 next = from + delta * (i / (float)steps);
				if (Solid(terrain, next, result, drop, out material)) { normal = Normal(terrain, next, result, drop); return result; }
				result = next;
			}
			return result;
		}

		public static float Inertia(CCActor actor, Vector2 feet, Vector2 centre, float rotation) {
			int count = ContactGeometry.Build(actor, feet, rotation, actor.Contacts.Atoms, false);
			float sum = 0;
			for (int i = 0; i < count; i++) sum += Vector2.DistanceSquared(actor.Contacts.Atoms[i], centre);
			return Math.Max(actor.Mass * 4, actor.Mass * sum / Math.Max(1, count));
		}

		public static float CrouchForClearance(CCActor actor, Vector2 feet, in ActorInput input, IActorTerrain terrain)
		{
			if (!actor.CanCrouch || actor.Status != ActorStatus.Stable || input.Prone || input.Jump || actor.Prone != ProneState.NotProne) return 0;
			float old = actor.CrouchAmount, need = 0;
			float predicted = actor.Vel.X * 6;
			for (int k = 0; k <= 10; k++) {
				actor.CrouchAmount = k / 10f;
				int n = ContactGeometry.Build(actor, feet, actor.Rot, actor.Contacts.Atoms, false);
				bool blocked = false;
				for (int i = 0; i < n; i++) {
					Vector2 p = actor.Contacts.Atoms[i];
					if (Solid(terrain, p - new Vector2(0, 2), p, false, out _) || Solid(terrain, p + new Vector2(predicted, -2), p, false, out _)) { blocked = true; break; }
				}
				if (!blocked) { need = k / 10f; break; }
				need = 1;
			}
			actor.CrouchAmount = old;
			return need;
		}

		public static Vector2 Step(CCActor actor, Vector2 feet, in ActorInput input, IActorTerrain terrain) {
			ActorContactState state = actor.Contacts;
			if (state.Initialized && Vector2.DistanceSquared(feet, state.Anchor) > 64 * 64) state.Reset();
			// Terraria worlds are tens of thousands of pixels across. Integrate in a local frame so
			// angular substeps do not lose small translations to world-space float rounding.
			state.TerrainFrame.Source = terrain; state.TerrainFrame.Origin = feet;
			if (state.Initialized) foreach (ContactLimb limb in state.Limbs) limb.Position -= feet;
			state.Anchor -= feet;
			Vector2 result;
			try { result = StepLocal(actor, Vector2.Zero, input, state.TerrainFrame); }
			finally { foreach (ContactLimb limb in state.Limbs) limb.Position += feet; }
			state.Anchor = result + feet;
			return state.Anchor;
		}

		static Vector2 StepLocal(CCActor actor, Vector2 feet, in ActorInput input, IActorTerrain terrain) {
			ActorContactState state = actor.Contacts;
			if (actor.Status == ActorStatus.Stable && (Math.Abs(actor.Vel.X) > CC.StableVelX || Math.Abs(actor.Vel.Y) > CC.StableVelY)) {
				actor.Status = ActorStatus.Unstable; actor.UnstableTicks = 0;
			}
			float angle = actor.Rot;
			Vector2 centre = feet + CentreOffset(actor, angle);
			float inertia = Inertia(actor, feet, centre, angle);
			float omega = actor.AngVel * CC.Dt;
			state.TravelImpulse = Vector2.Zero; state.ContactCount = 0; state.Grounded = false; state.Stuck = false;
			if (!state.Initialized) Initialize(actor, feet, terrain, input.Prone);
			if (actor.Kind != ActorKind.Turret)
				PushLimbs(actor, feet, centre, inertia, ref omega, input, terrain);

			// Resolve terrain inserted into the actor, with bounded recovery and no unbounded teleport.
			if (Blocked(actor, feet, angle, terrain, input.Prone)) {
				bool freed = false;
				Vector2 original = feet;
				for (int distance = 1; distance <= 24 && !freed; distance++) {
					foreach (Vector2 direction in RecoveryDirections) {
						Vector2 candidate = original + direction * distance;
						if (Blocked(actor, candidate, angle, terrain, input.Prone)) continue;
						feet = candidate; centre = feet + CentreOffset(actor, angle); freed = true; break;
					}
				}
				if (!freed) { state.Stuck = true; actor.Vel = Vector2.Zero; omega = 0; }
			}

			float remaining = state.Stuck ? 0 : 1;
			int iterations = 0;
			while (remaining > 0.00001f && iterations++ < 512) {
				float travel = actor.Vel.Length() + Math.Abs(omega) * 40;
				float dt = Math.Min(remaining, StepLength / Math.Max(StepLength, travel));
				Vector2 nextCentre = centre + actor.Vel * dt;
				float nextAngle = angle + omega * dt;
				Vector2 nextFeet = nextCentre - CentreOffset(actor, nextAngle);
				int count = ContactGeometry.Build(actor, nextFeet, nextAngle, state.Atoms);
				bool hit = false;
				// AtomGroup::Travel distributes a simultaneous manifold with ImpulseFactor = 1/hitCount.
				int hitCount = 0;
				for (int i = 0; i < count; i++) {
					Vector2 radius = state.Atoms[i] - nextCentre;
					Vector2 pv = actor.Vel + Perpendicular(radius) * omega;
					if (Solid(terrain, state.Atoms[i], state.Atoms[i] - pv * dt, input.Prone, out _)) hitCount++;
				}
				Vector2 totalImpulse = Vector2.Zero;
				float totalTorque = 0;
				for (int i = 0; i < count && hitCount > 0; i++) {
					Vector2 point = state.Atoms[i], radius = point - nextCentre;
					Vector2 pointVelocity = actor.Vel + Perpendicular(radius) * omega;
					Vector2 previous = point - pointVelocity * dt;
					if (!Solid(terrain, point, previous, input.Prone, out ContactSurface surface)) continue;
					hit = true;
					Vector2 normal = Normal(terrain, point, previous, input.Prone);
					float vn = Vector2.Dot(pointVelocity, normal);
					if (vn >= -0.00001f) continue;
					float restitution = vn < -0.75f ? surface.Restitution : 0;
					float jn = -(1 + restitution) * vn * EffectiveMass(actor.Mass, inertia, radius, normal) / hitCount;
					Vector2 tangent = Perpendicular(normal);
					float jt = -Vector2.Dot(pointVelocity, tangent) * EffectiveMass(actor.Mass, inertia, radius, tangent) / hitCount;
					jt = Math.Clamp(jt, -jn * surface.Friction * 0.45f, jn * surface.Friction * 0.45f);
					Vector2 impulse = normal * jn + tangent * jt;
					totalImpulse += impulse; totalTorque += Cross(radius, impulse);
					state.ContactCount++;
					if (normal.Y < -0.4f) state.Grounded = true;
				}
				actor.Vel += totalImpulse / actor.Mass; omega += totalTorque / inertia;
				state.TravelImpulse += totalImpulse;

				if (!hit) { centre = nextCentre; feet = nextFeet; angle = nextAngle; }
				else {
					// Re-test the corrected motion; translation and rotation are swept together.
					nextCentre = centre + actor.Vel * dt;
					nextAngle = angle + omega * dt;
					nextFeet = nextCentre - CentreOffset(actor, nextAngle);
					if (!Blocked(actor, nextFeet, nextAngle, terrain, input.Prone)) { centre = nextCentre; feet = nextFeet; angle = nextAngle; }
					else {
						// Advance to the free side of the contact, then solve the remaining time.
						// Consuming the entire tick here pins a crawling body whenever a small
						// inward angular velocity survives the averaged manifold impulse.
						float low = 0, high = 1;
						for (int j = 0; j < 12; j++) {
							float fraction = (low + high) * 0.5f;
							Vector2 testCentre = centre + actor.Vel * (dt * fraction);
							float testAngle = angle + omega * (dt * fraction);
							if (Blocked(actor, testCentre - CentreOffset(actor, testAngle), testAngle, terrain, input.Prone)) high = fraction;
							else low = fraction;
						}
						centre += actor.Vel * (dt * low); angle += omega * (dt * low);
						feet = centre - CentreOffset(actor, angle);
						dt *= Math.Max(low, 0.01f);
					}
				}
				remaining -= dt;
			}
			actor.Rot = angle; actor.AngVel = omega / CC.Dt;
			foreach (ContactLimb limb in state.Limbs) if (limb.Supported && limb.Normal.Y < -0.4f) state.Grounded = true;
			if (state.Grounded && Math.Abs(actor.Vel.Y) < 0.08f) actor.Vel.Y = 0;
			actor.AfterMove(actor.Vel, state.Grounded);
			state.Anchor = feet;
			return feet;
		}

		static readonly Vector2[] RecoveryDirections = { -Vector2.UnitY, -Vector2.UnitX, Vector2.UnitX, Vector2.UnitY,
			new(-0.7071f,-0.7071f), new(0.7071f,-0.7071f) };
		static bool Blocked(CCActor a, Vector2 feet, float angle, IActorTerrain terrain, bool drop) {
			int n = ContactGeometry.Build(a, feet, angle, a.Contacts.Atoms);
			for (int i = 0; i < n; i++) if (Solid(terrain, a.Contacts.Atoms[i], a.Contacts.Atoms[i], drop, out _)) return true;
			return false;
		}
		static Vector2 Rest(CCActor a, int limb) {
			if (a.Kind == ActorKind.Crab) {
				float x = limb switch { 0 => -6, 1 => 6, 2 => -4, _ => 8 };
				return new Vector2(x * 0.5f * a.FlipFactor, 9);
			}
			if (limb >= 2 && a.FrontArmFree) return new Vector2(0, 12);
			if (limb < 2) return new Vector2((limb == 0 ? 3.5f : -3.5f) * a.FlipFactor,
				MathHelper.Lerp(15, a.Kind == ActorKind.Brain ? 11 : 9, a.CrouchAmount));
			float aim = a.Flipped ? MathHelper.Pi + a.AimAngle : -a.AimAngle;
			return ContactGeometry.Unit(aim) * (limb == 2 ? 8 : 14) + new Vector2(0, 1.5f);
		}
		static void Initialize(CCActor a, Vector2 feet, IActorTerrain terrain, bool drop) {
			Vector2 hip = feet + ContactGeometry.HipOffset(a);
			for (int i = 0; i < 4; i++) {
				Vector2 joint = ContactGeometry.Joint(a, hip, a.Rot, i);
				Vector2 rest = Rest(a, i);
				Vector2 desired = a.Kind == ActorKind.Crab ? joint + rest : i < 2 ? hip + rest : joint + rest;
				ContactLimb limb = a.Contacts.Limbs[i];
				limb.Position = SweepPoint(terrain, joint, desired - joint, drop, out _, out _);
				limb.Velocity = Vector2.Zero;
			}
			a.Contacts.Initialized = true;
		}
		static void PushLimbs(CCActor a, Vector2 feet, Vector2 centre, float inertia, ref float omega, in ActorInput input, IActorTerrain terrain) {
			Vector2 hip = feet + ContactGeometry.HipOffset(a);
			int direction = input.Right ? 1 : input.Left ? -1 : 0;
			bool active = a.Status == ActorStatus.Stable;
			Vector2 jointVelocity = a.Vel;
			for (int i = 0; i < 4; i++) {
				ContactLimb limb = a.Contacts.Limbs[i]; limb.Supported = false;
				Gait gait = i < 2 ? a.Legs[i] : a.Hands[i - 2];
				bool returnStroke = active && gait.Path != null && !gait.Ended && gait.Path.EndSegments > 0 && gait.Path.Segs.Length - gait.Segment <= gait.Path.EndSegments;
				// Terrain can be placed/settled around a tip between frames. Never keep a static grip inside it.
				if (!returnStroke && Solid(terrain, limb.Position, limb.Position, input.Prone, out _)) {
					Vector2 embedded = limb.Position; bool free = false;
					for (int distance = 1; distance <= 18 && !free; distance++)
						foreach (Vector2 escape in RecoveryDirections) {
							Vector2 candidate = embedded + escape * distance;
							if (Solid(terrain, candidate, embedded, input.Prone, out _)) continue;
							limb.Position = candidate; limb.Velocity = Vector2.Zero; free = true; break;
						}
				}
				Vector2 joint = ContactGeometry.Joint(a, hip, a.Rot, i);
				bool motor = active && !a.Jetting && (gait.Path == null || !gait.Ended) && (a.Kind == ActorKind.Crab || i < 2 || a.Prone == ProneState.LayingProne && gait.Path != null);
				float force = a.Kind == ActorKind.Crab ? 3000 : i < 2 ? a.Move == MoveState.Run ? CC.RunPush : a.Move == MoveState.Crawl ? CC.CrawlPush : a.Move == MoveState.Stand ? 4200 : a.Move == MoveState.Crouch ? 4000 : CC.WalkPush : 3400;
				float reach = a.Kind == ActorKind.Crab ? 12 : i < 2 ? 16 : a.Kind == ActorKind.Brain ? 12 : 14;
				Vector2 target;
				float advance = 0;
				if (motor && gait.Path != null) {
					reach = Math.Max(reach, a.StandHeight * 0.75f);
					advance = CC.Speed(gait.Path.TravelSpeed) * a.SpeedScale * gait.SpeedMultiplier / (1 + Math.Abs(CC.ToMps(a.Vel.Y)) * 0.1f);
					Vector2 next = gait.Path.SegmentTarget(gait.Segment);
					int flip = direction == 0 ? a.FlipFactor : direction;
					float frame = a.Lying ? a.Rot : a.GroundSlope;
					Vector2 offset = next * (i < 2 ? a.StrideScale : Vector2.One); offset.X *= flip;
					target = joint + ContactGeometry.Rotate(offset, frame);
					if (limb.Path != gait.Path || limb.PathCycle != gait.Cycle) {
						// RestartFree scans the source's starting segments for the first obstacle.
						Vector2 LocalToWorld(Vector2 p) {
							p *= i < 2 ? a.StrideScale : Vector2.One; p.X *= flip;
							return joint + ContactGeometry.Rotate(p, frame);
						}
						Vector2 start = LocalToWorld(gait.Path.At(gait.Dist, out _));
						float distance = 0;
						int startSegments = gait.Dist < 0.01f ? gait.Path.StartSegments : 0;
						for (int segment = 0; segment < startSegments; segment++) {
							Vector2 end = LocalToWorld(gait.Path.SegmentTarget(segment));
							if (Solid(terrain, start, start, input.Prone, out _)) break;
							Vector2 free = SweepPoint(terrain, start, end - start, input.Prone, out Vector2 hitNormal, out _);
							gait.Segment = segment;
							gait.Dist = distance;
							if (hitNormal != Vector2.Zero) { start = free; break; }
							start = end; distance += gait.Path.Segs[segment].Length();
							gait.Segment = segment + 1; gait.Dist = distance;
						}
						if (!Solid(terrain, start, start, input.Prone, out _)) limb.Position = start;

						limb.Path = gait.Path;
						limb.PathCycle = gait.Cycle;
						target = LocalToWorld(gait.Path.SegmentTarget(gait.Segment));
					}
				}
				else if (active && a.Kind == ActorKind.Crab) target = joint + Rest(a, i);
				else if (active && i < 2 && !a.Lying) target = hip + Rest(a, i);
				else if (active && i >= 2 && (input.HoldingDevice || !a.Lying)) target = joint + Rest(a, i);
				else target = limb.Position + limb.Velocity + new Vector2(0, CC.Gravity);
				Vector2 offsetToTarget = target - joint;
				if (offsetToTarget.LengthSquared() > reach * reach) target = joint + Vector2.Normalize(offsetToTarget) * reach;
				Vector2 desired = target - limb.Position;
				if (motor && gait.Path == null) desired *= CC.Dt / 0.020f;
				float maxTravel = motor ? gait.Path != null ? advance : CC.Speed(a.Move == MoveState.Crouch ? 0.5f : 0.7f) : Math.Max(1, advance + a.Vel.Length());
				if (desired.Length() > maxTravel) desired = Vector2.Normalize(desired) * maxTravel;
				Vector2 desiredVelocity = desired + (motor || a.Jetting || active && !a.Lying ? jointVelocity : Vector2.Zero);
				returnStroke = motor && gait.Path != null && gait.Path.EndSegments > 0 && gait.Path.Segs.Length - gait.Segment <= gait.Path.EndSegments;
				Vector2 moved = SweepPoint(terrain, limb.Position, desiredVelocity, input.Prone, out Vector2 normal, out ContactSurface surface);
				if (returnStroke) { moved = limb.Position + desiredVelocity; normal = Vector2.Zero; }
				// A planted sole can push tangentially without moving into a solid side wall.
				if (!returnStroke && desiredVelocity.Y >= 0 && normal == Vector2.Zero && Solid(terrain, moved + new Vector2(0, 1), moved, input.Prone, out surface)) normal = -Vector2.UnitY;
				bool withinReach = Vector2.DistanceSquared(joint, moved) <= (reach + 1) * (reach + 1);
				if (normal != Vector2.Zero && withinReach) {
					limb.Supported = true; limb.Normal = normal;
					if (motor) {
						// AHuman explicitly disables rotational leverage for upright walking/standing, but enables it for crawl limbs.
						Vector2 radius = a.Lying ? joint - centre : Vector2.Zero;
						// Pushing the tip into terrain produces the opposite impulse at the joint.
						// PushTravel caps the driving velocity by force*time/mass, then
						// applies material restitution and proportional tangential friction.
						// This is distinct from the passive body's Coulomb contact model.
						float maxImpulse = force * CC.Dt * CC.PPM / 60f;
						Vector2 forceVelocity = desiredVelocity;
						float limit = maxImpulse / a.Mass;
						if (forceVelocity.Length() > limit) forceVelocity *= limit / forceVelocity.Length();
						float jn = Math.Max(0, -Vector2.Dot(forceVelocity, normal)) * a.Mass * (1 + surface.Restitution);
						Vector2 t = Perpendicular(normal);
						float jt = -Vector2.Dot(forceVelocity, t) * a.Mass * surface.Friction;
						Vector2 impulse = normal * jn + t * jt;
						if (direction != 0 && Math.Sign(impulse.X) == -direction) impulse.X *= CC.AgainstTravelPush;
						if (impulse.Length() > maxImpulse) impulse *= maxImpulse / impulse.Length();
						ApplyImpulse(ref a.Vel, ref omega, impulse, radius, a.Mass, inertia);
					}
					else limb.Velocity -= normal * Math.Min(0, Vector2.Dot(limb.Velocity, normal));
				}
				limb.Velocity = moved - limb.Position; limb.Position = moved;
				if (gait.Path != null && motor) {
					float previousDistance = gait.Dist;
					Vector2 local = ContactGeometry.Rotate(limb.Position - joint, -(a.Lying ? a.Rot : a.GroundSlope));
					local.X *= direction == 0 ? a.FlipFactor : direction;
					if (i < 2) { local.X /= a.StrideScale.X; local.Y /= a.StrideScale.Y; }
					gait.ReportPosition(local);
					float progress = gait.Dist - previousDistance;
					limb.StalledTicks = progress < 0.01f ? limb.StalledTicks + 1 : 0;
					int strokeLimit = Math.Max(20, (int)MathF.Ceiling(gait.Path.Length / Math.Max(0.05f, advance) * 1.1f));
					if (limb.StalledTicks > strokeLimit) { gait.Ended = true; limb.StalledTicks = 0; }
				}
				else { limb.Path = null; }
			}
		}
	}
}
