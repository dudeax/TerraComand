using System;
using Microsoft.Xna.Framework;

namespace TerraCommand.Common.Actors
{
	public sealed class ContactMask
	{
		public readonly int Width, Height;
		public readonly short[] Points;
		public ContactMask(int width, int height, short[] points) { Width = width; Height = height; Points = points; }
	}

	/// <summary>Pure geometry shared by the contact simulation and the rendered rig. No GPU/content access.</summary>
	public static class ContactGeometry
	{
		public static Vector2 Rotate(Vector2 p, float angle) {
			float c = MathF.Cos(angle), s = MathF.Sin(angle);
			return new Vector2(p.X * c - p.Y * s, p.X * s + p.Y * c);
		}
		public static Vector2 Unit(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));
		public static Vector2 Normalized(Vector2 p, Vector2 fallback) => p.LengthSquared() > 0.000001f ? Vector2.Normalize(p) : fallback;
		public static Vector2 HipOffset(CCActor a) => HipOffset(a, a.Rot);
		public static Vector2 HipOffset(CCActor a, float rotation) {
			float lying = Math.Clamp((Math.Abs(MathF.Sin(rotation)) - 0.3f) / 0.5f, 0, 1);
			float h = MathHelper.Lerp(15, a.Kind == ActorKind.Brain ? 11 : 9, a.Status == ActorStatus.Stable && a.Prone == ProneState.NotProne ? a.CrouchAmount : 0);
			return new Vector2(-a.FlipFactor * 6.5f * lying, -MathHelper.Lerp(h, 6, lying));
		}
		public static Vector2 Shoulder(CCActor a) => a.Kind == ActorKind.Heavy ? new(1, -15) : a.Kind == ActorKind.Brain ? new(0, -12) : new(1, -13);
		public static Vector2 Neck(CCActor a) => a.Kind == ActorKind.Heavy ? new(1, -17) : a.Kind == ActorKind.Brain ? new(0, -8) : new(1, -15);
		public static Vector2 Joint(CCActor a, Vector2 hip, float rot, int limb) {
			if (a.Kind == ActorKind.Crab) {
				float x = limb switch { 0 => -6, 1 => 6, 2 => -4, _ => 8 };
				return hip - HipOffset(a, rot) - new Vector2(0, 12) + Rotate(new Vector2(x * a.FlipFactor, 3), rot);
			}
			if (limb < 2) return hip + Rotate(new Vector2((limb == 0 ? 1 : -1) * a.FlipFactor, 0), a.Lying ? rot : a.GroundSlope);
			Vector2 shoulder = Shoulder(a); shoulder.X *= limb == 2 ? a.FlipFactor : -a.FlipFactor;
			return hip + Rotate(shoulder, rot);
		}
		public static Vector2 Knee(Vector2 from, Vector2 to, float length, Vector2 bend) {
			Vector2 d = to - from;
			float distance = Math.Clamp(d.Length(), 0.01f, 2 * length - 0.01f);
			float angle = MathF.Atan2(d.Y, d.X), off = MathF.Acos(Math.Clamp(distance / (2 * length), -1, 1));
			Vector2 p = from + Unit(angle + off) * length, q = from + Unit(angle - off) * length;
			return Vector2.Dot(p - from, bend) >= Vector2.Dot(q - from, bend) ? p : q;
		}

		public static int Build(CCActor a, Vector2 feet, float rotation, Vector2[] into, bool limbs = false) {
			Vector2 hip = feet + HipOffset(a, rotation);
			int n = 0, dir = a.FlipFactor;
			void Part(string part, Vector2 at, float angle, Vector2 origin) {
				ContactMask mask = ActorSpriteMasks.Get(a.Kind, part);
				for (int i = 0; i < mask.Points.Length && n < into.Length; i += 2) {
					Vector2 p = new(mask.Points[i] - origin.X, mask.Points[i + 1] - origin.Y);
					p.X *= dir;
					into[n++] = at + Rotate(p, angle);
				}
			}
			ContactMask torso = ActorSpriteMasks.Get(a.Kind, a.Kind == ActorKind.Crab ? "Body" : a.Kind == ActorKind.Turret ? "Base" : "Torso");
			if (a.Kind == ActorKind.Crab || a.Kind == ActorKind.Turret) {
				Part(a.Kind == ActorKind.Crab ? "Body" : "Base", feet - new Vector2(0, torso.Height / 2f), rotation, new Vector2(torso.Width / 2f, torso.Height / 2f));
				if (a.Kind == ActorKind.Crab && limbs && a.Contacts.Initialized) {
					for (int i = 0; i < 4; i++) {
						Vector2 joint = Joint(a, hip, rotation, i), tip = a.Contacts.Limbs[i].Position;
						Vector2 knee = Knee(joint, tip, 6, Rotate(-Vector2.UnitY, rotation));
						tip = knee + Normalized(tip - knee, Vector2.UnitY) * 6;
						for (int bone = 0; bone < 2; bone++) {
							string part = bone == 0 ? "Thigh" : "Shin";
							Vector2 from = part == "Thigh" ? joint : knee, to = part == "Thigh" ? knee : tip;
							ContactMask mask = ActorSpriteMasks.Get(a.Kind, part);
							Part(part, from, MathF.Atan2(to.Y - from.Y, to.X - from.X) - MathHelper.PiOver2, new Vector2(mask.Width / 2f, 1));
						}
					}
				}
				if (a.Kind == ActorKind.Turret) {
					ContactMask head = ActorSpriteMasks.Get(a.Kind, "Head");
					Part("Head", feet - new Vector2(0, 16), -a.AimAngle * dir, new Vector2(head.Width / 2f, head.Height / 2f));
				}
				return n;
			}
			Part("Torso", hip, rotation, new Vector2(torso.Width / 2f, torso.Height));
			Vector2 neck = Neck(a); neck.X *= dir;
			ContactMask hm = ActorSpriteMasks.Get(a.Kind, "Head");
			float headTurn = a.Status != ActorStatus.Stable ? 0 : Math.Clamp(-a.AimAngle * 0.45f * dir - rotation * 0.4f, -0.6f, 0.6f);
			Part("Head", hip + Rotate(neck, rotation), rotation + headTurn, new Vector2(hm.Width / 2f, hm.Height));
			if (a.Kind != ActorKind.Brain) {
				Vector2 pack = a.Kind == ActorKind.Heavy ? new(-8 * dir, -10) : new(-7 * dir, -9);
				Part("Jetpack", hip + Rotate(pack, rotation), rotation, new Vector2(ActorSpriteMasks.Jetpack.Width / 2f, ActorSpriteMasks.Jetpack.Height / 2f));
			}
			if (!limbs || !a.Contacts.Initialized) return n;
			for (int i = 0; i < 4; i++) {
				Vector2 joint = Joint(a, hip, rotation, i), tip = a.Contacts.Limbs[i].Position;
				float length = i < 2 ? 7 : a.Kind == ActorKind.Brain ? 6 : 7;
				float frame = a.Lying ? rotation : a.GroundSlope;
				Vector2 end = i < 2 ? tip - Rotate(new Vector2(0, 2), frame) : tip;
				Vector2 bend = i < 2 ? Rotate(new Vector2(dir, 0), frame) : Rotate(Vector2.UnitY, rotation);
				Vector2 middle = Knee(joint, end, length, bend);
				end = middle + Normalized(end - middle, Vector2.UnitY) * length;
				void Bone(string part, Vector2 from, Vector2 to) {
					ContactMask mask = ActorSpriteMasks.Get(a.Kind, part);
					Part(part, from, MathF.Atan2(to.Y - from.Y, to.X - from.X) - MathHelper.PiOver2, new Vector2(mask.Width / 2f, 1));
				}
				Bone(i < 2 ? "Thigh" : "UpperArm", joint, middle);
				Bone(i < 2 ? "Shin" : "Forearm", middle, end);
				// Soles and hands are swept separately as constrained contact groups.
			}
			return n;
		}
	}
}
