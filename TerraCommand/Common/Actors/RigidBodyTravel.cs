using System;
using Microsoft.Xna.Framework;
using static TerraCommand.Common.Actors.ActorContacts;

namespace TerraCommand.Common.Actors {
	public interface IRigidBodyShape {
		Vector2 CentreOffset(float rotation);
		int Build(Vector2 anchor, float rotation, Vector2[] atoms);
		float SweepRadius { get; }
	}

	/// <summary>Compound bodies can assign different source materials to individual contact atoms.</summary>
    public interface IRigidBodyMaterials {
        ContactSurface MaterialForAtom(int index);
    }

    public static class ActorMaterials {
        public static ContactSurface For(ActorKind kind, int region) => region == 6 || kind is ActorKind.Brain or ActorKind.Crab or ActorKind.Turret
            ? new(.4f,.75f) // Civilian/Military Stuff
            : new(.6f,.6f); // Flesh, Kevlared Flesh, Armoured Flesh
    }

	public sealed class RigidBodyMotion {
		public Vector2 Position, Velocity, TravelImpulse;
		public float Rotation, AngularVelocity, Mass = 1, Inertia = 1;
		public float Friction = .45f, Restitution = 1;
		public bool Grounded, Stuck;
		public int ContactCount;
		public Action<Vector2,Vector2> ContactImpulse;
		internal IRigidBodyShape Shape;
		internal readonly Vector2[] Atoms = new Vector2[4096];
		internal readonly ShiftedActorTerrain TerrainFrame = new();
		internal bool Blocked(Vector2 anchor, float angle, IActorTerrain terrain, bool drop) {
			int count = Shape.Build(anchor, angle, Atoms);
			for (int i = 0; i < count; i++) if (Solid(terrain, Atoms[i], Atoms[i], drop, out _)) return true;
			return false;
		}
		public Vector2 PointVelocity(Vector2 point) => Velocity + Perpendicular(point - Position - Shape.CentreOffset(Rotation)) * (AngularVelocity * CC.Dt);
		public void Impulse(Vector2 impulse, Vector2 worldPoint) {
			float omega = AngularVelocity * CC.Dt;
			ApplyImpulse(ref Velocity, ref omega, impulse, worldPoint - Position - Shape.CentreOffset(Rotation), Mass, Inertia);
			AngularVelocity = omega / CC.Dt;
		}
	}

	internal sealed class ActorBodyShape : IRigidBodyShape, IRigidBodyMaterials {
		public CCActor Actor;
        readonly byte[] regions = new byte[4096];
        public ContactSurface MaterialForAtom(int index) => ActorMaterials.For(Actor.Kind,regions[index]);
		public Vector2 CentreOffset(float angle) => ActorContacts.CentreOffset(Actor, angle);
		public float SweepRadius => 40 * CC.WorldScale;
		public int Build(Vector2 feet, float angle, Vector2[] atoms) => ContactGeometry.Build(Actor, feet, angle, atoms, regions: regions);
	}

	/// <summary>Shared swept translation/rotation and simultaneous contact impulses for actors and craft.</summary>
	public static class RigidBodyTravel {
		const float StepLength = 0.75f;
		public static void Step(RigidBodyMotion motion, IRigidBodyShape shape, IActorTerrain terrain, bool drop = false) {
			motion.Shape = shape;
			Vector2 origin = motion.Position;
			motion.TerrainFrame.Source = terrain; motion.TerrainFrame.Origin = origin;
			motion.Position = Vector2.Zero;
			try { StepLocal(motion, shape, motion.TerrainFrame, drop); }
			finally { motion.Position += origin; }
		}

		static void StepLocal(RigidBodyMotion motion, IRigidBodyShape shape, IActorTerrain terrain, bool drop) {
			Vector2 feet = motion.Position;
			float angle = motion.Rotation;
			Vector2 centre = feet + shape.CentreOffset(angle);
			float inertia = motion.Inertia;
			float omega = motion.AngularVelocity * CC.Dt;
			motion.TravelImpulse = Vector2.Zero; motion.ContactCount = 0; motion.Grounded = motion.Stuck = false;
			// Resolve terrain inserted into the actor, with bounded recovery and no unbounded teleport.
			if (motion.Blocked( feet, angle, terrain, drop)) {
				bool freed = false;
				Vector2 original = feet;
				for (int distance = 1; distance <= 24 * CC.WorldScale && !freed; distance++) {
					foreach (Vector2 direction in RecoveryDirections) {
						Vector2 candidate = original + direction * distance;
						if (motion.Blocked( candidate, angle, terrain, drop)) continue;
						feet = candidate; centre = feet + shape.CentreOffset( angle); freed = true; break;
					}
				}
				if (!freed) { motion.Stuck = true; motion.Velocity = Vector2.Zero; omega = 0; }
			}

			float remaining = motion.Stuck ? 0 : 1;
			int iterations = 0;
			while (remaining > 0.00001f && iterations++ < 512) {
				float travel = motion.Velocity.Length() + Math.Abs(omega) * shape.SweepRadius;
				float dt = Math.Min(remaining, StepLength / Math.Max(StepLength, travel));
				motion.Position=feet; motion.Rotation=angle;
				Vector2 nextCentre = centre + motion.Velocity * dt;
				float nextAngle = angle + omega * dt;
				Vector2 nextFeet = nextCentre - shape.CentreOffset( nextAngle);
				int count = shape.Build( nextFeet, nextAngle, motion.Atoms);
				bool hit = false;
				// AtomGroup::Travel distributes a simultaneous manifold with ImpulseFactor = 1/hitCount.
				int hitCount = 0;
				for (int i = 0; i < count; i++) {
					Vector2 radius = motion.Atoms[i] - nextCentre;
					Vector2 pv = motion.Velocity + Perpendicular(radius) * omega;
					if (Solid(terrain, motion.Atoms[i], motion.Atoms[i] - pv * dt, drop, out _)) hitCount++;
				}
				Vector2 totalImpulse = Vector2.Zero;
				float totalTorque = 0;
				for (int i = 0; i < count && hitCount > 0; i++) {
					Vector2 point = motion.Atoms[i], radius = point - nextCentre;
					Vector2 pointVelocity = motion.Velocity + Perpendicular(radius) * omega;
					Vector2 previous = point - pointVelocity * dt;
					if (!Solid(terrain, point, previous, drop, out ContactSurface surface)) continue;
					Vector2 incoming = pointVelocity * (motion.Mass / hitCount);
					if (terrain is IPenetrableTerrain penetrable && penetrable.Penetrate(point,incoming.Length()*60/CC.PPM,out float loss)) {
						Vector2 resistance = -incoming * loss;
						totalImpulse += resistance; totalTorque += Cross(radius,resistance);
						motion.ContactImpulse?.Invoke(point + motion.TerrainFrame.Origin,resistance);
						continue;
					}
					hit = true;
					Vector2 normal = Normal(terrain, point, previous, drop);
					float vn = Vector2.Dot(pointVelocity, normal);
					if (vn >= -0.00001f) continue;
					ContactSurface bodyMaterial = shape is IRigidBodyMaterials materials ? materials.MaterialForAtom(i) : new(motion.Friction,motion.Restitution);
                    float restitution = vn < -0.75f ? surface.Restitution * bodyMaterial.Restitution : 0;
					float jn = -(1 + restitution) * vn * EffectiveMass(motion.Mass, inertia, radius, normal) / hitCount;
					Vector2 tangent = Perpendicular(normal);
					float jt = -Vector2.Dot(pointVelocity, tangent) * EffectiveMass(motion.Mass, inertia, radius, tangent) / hitCount;
					jt = Math.Clamp(jt, -jn * surface.Friction * bodyMaterial.Friction, jn * surface.Friction * bodyMaterial.Friction);
					Vector2 impulse = normal * jn + tangent * jt;
					totalImpulse += impulse; totalTorque += Cross(radius, impulse);
					motion.ContactImpulse?.Invoke(point + motion.TerrainFrame.Origin,impulse);
					motion.ContactCount++;
					if (normal.Y < -0.4f) motion.Grounded = true;
				}
				motion.Velocity += totalImpulse / motion.Mass; omega += totalTorque / inertia;
				motion.TravelImpulse += totalImpulse;

				if (!hit) { centre += motion.Velocity * dt; angle += omega * dt; feet = centre - shape.CentreOffset(angle); }
				else {
					// Re-test the corrected motion; translation and rotation are swept together.
					nextCentre = centre + motion.Velocity * dt;
					nextAngle = angle + omega * dt;
					nextFeet = nextCentre - shape.CentreOffset( nextAngle);
					if (!motion.Blocked( nextFeet, nextAngle, terrain, drop)) { centre = nextCentre; feet = nextFeet; angle = nextAngle; }
					else {
						// Advance to the free side of the contact, then solve the remaining time.
						// Consuming the entire tick here pins a crawling body whenever a small
						// inward angular velocity survives the averaged manifold impulse.
						float low = 0, high = 1;
						for (int j = 0; j < 12; j++) {
							float fraction = (low + high) * 0.5f;
							Vector2 testCentre = centre + motion.Velocity * (dt * fraction);
							float testAngle = angle + omega * (dt * fraction);
							if (motion.Blocked( testCentre - shape.CentreOffset( testAngle), testAngle, terrain, drop)) high = fraction;
							else low = fraction;
						}
						centre += motion.Velocity * (dt * low); angle += omega * (dt * low);
						feet = centre - shape.CentreOffset( angle);
						dt *= Math.Max(low, 0.01f);
					}
				}
				remaining -= dt;
			}
			motion.Position = feet; motion.Rotation = angle; motion.AngularVelocity = omega / CC.Dt;
		}
	}
}
