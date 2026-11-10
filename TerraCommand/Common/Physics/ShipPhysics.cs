using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using TerraCommand.Common.Actors;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Content.NPCs {
	public enum ShipStyle : byte { Rocket, Dropship, Crate }
}

namespace TerraCommand.Common.Physics {
	public readonly record struct CraftInput(float Steer, float Vertical, bool Main, bool Down);

	/// <summary>Resolved Base.rte craft data. Force is N, impulse N*s, mass kg; simulation uses CC's 20px/m and 60Hz.</summary>
	public readonly record struct CraftProfile(float EmptyMass, float Capacity, float MainForce, float MainBurst,
		float SideForce, float SideBurst, float DamageThreshold, float GibLimit) {
		public static CraftProfile Get(ShipStyle style) => style switch {
			ShipStyle.Rocket => new(600 + 200 + 2 * 70 + 4 * 2, 600, (6000 + 5700 + 1800) / 60f * 9 * 20,
				(8 + 6 + 2) * 9 * 20 * 1.5f, 2 * 3200 / 60f * 2 * 19.5f, 2 * 14 * 2 * 19.5f, 9500, 17000),
			ShipStyle.Dropship => new(2400 + 2 * 300 + 2 * 200 + 2 + 2 * 10, 800,
				10000 / 60f * 7.5f * 35, 0, 5000 / 60f * 2 * 18, 20 * 2 * 18, 15000, 45000),
			_ => new(300.2f, 250, 0, 0, 0, 0, 20000, 50000),
		};
	}

	/// <summary>One physical model for piloted flight, delivery, hovering and return flight. Controllers only request thrust.</summary>
	public sealed class ShipPhysics : IRigidBodyShape {
		public readonly ShipStyle Style;
		public readonly CraftProfile Profile;
		public readonly RigidBodyMotion Motion = new();
		public readonly CraftAttachments Attachments=new();
		public readonly Queue<DetachedCraftPart> Detached=new();
		public float EmptyMass => Profile.EmptyMass - (Style==ShipStyle.Dropship ?
			(Attachments.Has(CraftPart.LeftEngine)?0:300)+(Attachments.Has(CraftPart.RightEngine)?0:300)+(Attachments.Has(CraftPart.LeftHatch)?0:200)+(Attachments.Has(CraftPart.RightHatch)?0:200) : Style==ShipStyle.Rocket ?
			(Attachments.Has(CraftPart.LeftGear)?0:70)+(Attachments.Has(CraftPart.RightGear)?0:70) : 0);
		readonly ContactMask hull, fill;
		readonly HashSet<int> occupied = new();
		readonly Vector2 origin;
		public int Width { get; }
		public int Height { get; }
		public float SweepRadius { get; }
		public float HatchOpen;
		public float Gear = 1, LateralControl, LeftThrottle, RightThrottle;
		public float EngineAngle { get; private set; }
		public bool MainOn, LeftOn, RightOn, UpLeftOn, UpRightOn, BayOpen;
		public float LastImpact { get; private set; }
		bool previousMain, previousLeft, previousRight, previousUpLeft, previousUpRight;
		int burstCooldown;
		public bool Thrusting => MainOn || LeftOn || RightOn || UpLeftOn || UpRightOn;

		public ShipPhysics(ShipStyle style) {
			Style = style; Profile = CraftProfile.Get(style);
			(hull, fill, origin) = style switch {
				ShipStyle.Rocket => (ShipCollisionMasks.RocketHull, ShipCollisionMasks.RocketFill, new Vector2(17, 60)),
				ShipStyle.Dropship => (ShipCollisionMasks.DropshipHull, ShipCollisionMasks.DropshipFill, new Vector2(88, 40)),
				_ => (ShipCollisionMasks.CrateHull, ShipCollisionMasks.CrateFill, new Vector2(12, 17)),
			};
			int bottom = 0;
			for (int i = 0; i < fill.Points.Length; i += 2) {
				occupied.Add(fill.Points[i] / 2 + fill.Points[i + 1] / 2 * ((fill.Width + 1) / 2));
				bottom = Math.Max(bottom, fill.Points[i + 1] + 1);
			}
			Width = (style == ShipStyle.Rocket ? 40 : hull.Width) * CC.WorldScale;
			Height = (style == ShipStyle.Rocket ? 105 : style==ShipStyle.Dropship?86:bottom) * CC.WorldScale;
			SweepRadius = Math.Max(Width, Height);
			Motion.Shape = this; Motion.Mass = Profile.EmptyMass; Motion.Friction=.4f; Motion.Restitution=.5f;
			Motion.ContactImpulse=OnContactImpulse;
			UpdateInertia();
		}

		public Vector2 CentreOffset(float rotation) => new(0, origin.Y * CC.WorldScale - Height);
		public Vector2 Centre => Motion.Position + CentreOffset(Motion.Rotation);
		public Vector2 World(Vector2 local) => Centre + ContactGeometry.Rotate(local * CC.WorldScale, Motion.Rotation);
		public Vector2 TextureOrigin => origin;
		public Vector2 DrawPosition => Centre;
		public Vector2 GearOffset(int side) => new(side * (12 + 5 * Gear), 29 + 8 + 8 * Gear);

		bool InBay(Vector2 local) => Style==ShipStyle.Dropship
			? (BayOpen || !Attachments.Has(local.X<0?CraftPart.LeftHatch:CraftPart.RightHatch)) && Math.Abs(local.X)<24 && local.Y>5 && local.Y<46
			: BayOpen && Style==ShipStyle.Rocket && Math.Abs(local.X)<9 && local.Y>-20 && local.Y<30;
		// Leg::UpdateLegRotation: correct the native horizontal sprite toward the gear's ankle path.
        float GearExtension => Math.Clamp((new Vector2(5*Gear,8+8*Gear).Length()-MathF.Sqrt(125))/(MathF.Sqrt(349)-MathF.Sqrt(125)),0,1);
        public int GearFrame => Math.Min(4,(int)MathF.Floor(GearExtension*5));
        public float GearAngle(int side) {
            float correction=-MathF.Atan2(-5,10)-(MathF.Atan2(-5,18)-MathF.Atan2(-5,10))*MathF.Sin(GearExtension*MathHelper.PiOver2);
            return Motion.Rotation+MathF.Atan2(8+8*Gear,side*5*Gear)+(side<0?MathHelper.Pi:0)+side*correction;
        }
        public float PartAngle(CraftPart part) => part<= CraftPart.RightEngine?EngineAngle:part>=CraftPart.LeftHatch?Motion.Rotation+(part==CraftPart.LeftHatch?1:-1)*MathHelper.ToRadians(28)*HatchOpen:GearAngle(part==CraftPart.LeftGear?-1:1);
		public static Vector2 PartOrigin(CraftPart part) => part switch {CraftPart.LeftEngine=>new(29,21),CraftPart.RightEngine=>new(6,21),CraftPart.LeftHatch=>new(36,-22),CraftPart.RightHatch=>new(0,-22),_=>new(4,10)};
		public static ContactMask HatchMask(CraftPart part,bool fill) => part==CraftPart.LeftHatch?(fill?ShipCollisionMasks.DropshipHatchLeftFill:ShipCollisionMasks.DropshipHatchLeftHull):(fill?ShipCollisionMasks.DropshipHatchRightFill:ShipCollisionMasks.DropshipHatchRightHull);

		public int Build(Vector2 feet, float angle, Vector2[] atoms) {
			Vector2 centre = feet + CentreOffset(angle); int count = 0;
			if (Style == ShipStyle.Dropship) {
				Add(ShipCollisionMasks.DropshipBodyHull,centre,new Vector2(55,32),angle);
				if(Attachments.Has(CraftPart.LeftEngine)) Add(ShipCollisionMasks.DropshipEngineLeftHull,centre + ContactGeometry.Rotate(new Vector2(-49,-3) * CC.WorldScale,angle),new Vector2(29,21),EngineAngle);
				if(Attachments.Has(CraftPart.RightEngine)) Add(ShipCollisionMasks.DropshipEngineRightHull,centre + ContactGeometry.Rotate(new Vector2(49,-3) * CC.WorldScale,angle),new Vector2(6,21),EngineAngle);
				foreach(var part in new[]{CraftPart.LeftHatch,CraftPart.RightHatch}) if(Attachments.Has(part))
					Add(HatchMask(part,false),centre+ContactGeometry.Rotate(PartJoint(part) * CC.WorldScale,angle),PartOrigin(part),angle+(part==CraftPart.LeftHatch?1:-1)*MathHelper.ToRadians(28)*HatchOpen,false);
				return count;
			}
			void Add(ContactMask mask,Vector2 pivot,Vector2 spriteOrigin,float rotation,bool cutBay=true) {
				for(int i=0;i<mask.Points.Length && count<atoms.Length;i+=2) {
					Vector2 point=pivot+ContactGeometry.Rotate((new Vector2(mask.Points[i],mask.Points[i+1])-spriteOrigin) * CC.WorldScale,rotation);
					if (!cutBay || !InBay(ContactGeometry.Rotate(point-centre,-angle) / CC.WorldScale)) atoms[count++]=point;
				}
			}
			for (int i = 0; i < hull.Points.Length && count < atoms.Length; i += 2) {
				Vector2 local = new Vector2(hull.Points[i], hull.Points[i + 1]) - origin;
				if (!InBay(local)) atoms[count++] = centre + ContactGeometry.Rotate(local * CC.WorldScale, angle);
			}
			if (Style == ShipStyle.Rocket) {
				// Landing gear's source static paths: raised (0,8), lowered (5,16), under joints (+/-12,29).
				for (int side = -1; side <= 1; side += 2) {
					if(!Attachments.Has(side<0?CraftPart.LeftGear:CraftPart.RightGear)) continue;
					Vector2 foot = centre + ContactGeometry.Rotate(GearOffset(side) * CC.WorldScale, angle);
					for (int x = -3; x <= 3 && count < atoms.Length; x++) atoms[count++] = foot + new Vector2(x * CC.WorldScale, 0);
				}
			}
			return count;
		}

		public bool Contains(Vector2 worldPoint) {
			Vector2 local = ContactGeometry.Rotate(worldPoint - Centre, -Motion.Rotation) / CC.WorldScale;
			if(Style==ShipStyle.Dropship) foreach(var part in new[]{CraftPart.LeftHatch,CraftPart.RightHatch})
				if(Attachments.Has(part) && MaskContains(HatchMask(part,true),ContactGeometry.Rotate(worldPoint-World(PartJoint(part)),-PartAngle(part))/CC.WorldScale+PartOrigin(part))) return true;
			if (InBay(local)) return false;
			if (Style == ShipStyle.Dropship) {
				return MaskContains(ShipCollisionMasks.DropshipBodyFill,local + new Vector2(55,32))
					|| Attachments.Has(CraftPart.LeftEngine) && MaskContains(ShipCollisionMasks.DropshipEngineLeftFill,ContactGeometry.Rotate(worldPoint-World(new Vector2(-49,-3)),-EngineAngle)/CC.WorldScale+new Vector2(29,21))
					|| Attachments.Has(CraftPart.RightEngine) && MaskContains(ShipCollisionMasks.DropshipEngineRightFill,ContactGeometry.Rotate(worldPoint-World(new Vector2(49,-3)),-EngineAngle)/CC.WorldScale+new Vector2(6,21));
			}
			Vector2 pixel = local + origin;
			if (pixel.X < 0 || pixel.Y < 0 || pixel.X >= fill.Width || pixel.Y >= fill.Height) return false;
			return occupied.Contains((int)pixel.X / 2 + (int)pixel.Y / 2 * ((fill.Width + 1) / 2));
		}

		static bool MaskContains(ContactMask mask,Vector2 pixel) {
			if(pixel.X<0 || pixel.Y<0 || pixel.X>=mask.Width || pixel.Y>=mask.Height) return false;
			int x=(int)pixel.X/2,y=(int)pixel.Y/2;
			for(int i=0;i<mask.Points.Length;i+=2) if(mask.Points[i]/2==x && mask.Points[i+1]/2==y) return true;
			return false;
		}

		void UpdateInertia() {
			float sum = 0;
			int count=Build(Motion.Position,Motion.Rotation,Motion.Atoms);
			for(int i=0;i<count;i++) sum+=Vector2.DistanceSquared(Motion.Atoms[i],Centre);
			Motion.Inertia=Math.Max(Motion.Mass*4,Motion.Mass*sum/Math.Max(1,count));
		}

		void Force(Vector2 localPoint, Vector2 direction, float newtons, float impulse = 0) {
			// Convert N*dt and N*s to kg*px/tick before applying the lever arm.
			Vector2 momentum = direction * ((newtons * CC.Dt + impulse) * CC.PPM / 60f);
			Motion.Impulse(momentum, World(localPoint));
		}

		public static float ThrottleFactor(float throttle) => MathHelper.Lerp(0.1f, 1.3f, (Math.Clamp(throttle, -1, 1) + 1) / 2);
		static float ScaleThrottle(float throttle, float massScale) => Math.Clamp((ThrottleFactor(throttle) * massScale - 0.7f) / 0.6f, -1, 1);

		public void Step(in CraftInput control, float cargoMass, IActorTerrain terrain) {
			Motion.Mass = EmptyMass + Math.Max(0, cargoMass); UpdateInertia();
			Attachments.Begin();
			HatchOpen=Math.Clamp(HatchOpen+(BayOpen?1:-1)/15f,0,1);
			if (burstCooldown > 0) burstCooldown--;
			if (Style == ShipStyle.Rocket) Rocket(control);
			else if (Style == ShipStyle.Dropship) Dropship(control);
			else MainOn = LeftOn = RightOn = UpLeftOn = UpRightOn = false;
			Motion.Velocity.Y += CC.Gravity;
			RigidBodyTravel.Step(Motion, this, terrain);
			Motion.Rotation = MathHelper.WrapAngle(Motion.Rotation);
			LastImpact = Motion.TravelImpulse.Length() * 60f / CC.PPM;
			foreach(CraftPart part in Enum.GetValues<CraftPart>()) if(Applicable(part) && Attachments.Overloaded(part)) Detach(part);
			previousMain = MainOn; previousLeft = LeftOn; previousRight = RightOn;
			previousUpLeft = UpLeftOn; previousUpRight = UpRightOn;
		}

		void Rocket(in CraftInput control) {
			MainOn = control.Main; LeftOn = control.Steer > 0.1f; RightOn = control.Steer < -0.1f;
			UpLeftOn = UpRightOn = control.Down;
			float gearStep = CC.Speed(10) / (MathF.Sqrt(5 * 5 + 8 * 8) * CC.WorldScale);
			Gear = Math.Clamp(Gear + (MainOn ? -gearStep : gearStep), 0, 1);
			if (MainOn) {
				// CC angles are counter-clockwise; screen-space recoil leans beyond the hull angle to restore it.
				float gimbal = Motion.Rotation + MathF.Sin(Motion.Rotation) * MathHelper.ToRadians(15);
				bool burst = !previousMain && burstCooldown == 0;
				if (burst) burstCooldown = 6; // 96ms spacing
				Force(new Vector2(0,33), ContactGeometry.Rotate(-Vector2.UnitY,gimbal),
					Profile.MainForce * (burst ? 1.5f : 1), burst ? Profile.MainBurst : 0);
			}
			float tilt = MathHelper.Pi / 8;
			if (LeftOn) Force(new Vector2(-12,-41), ContactGeometry.Rotate(new Vector2(MathF.Cos(tilt),-MathF.Sin(tilt)), Motion.Rotation), Profile.SideForce, previousLeft ? 0 : Profile.SideBurst);
			if (RightOn) Force(new Vector2(12,-41), ContactGeometry.Rotate(new Vector2(-MathF.Cos(tilt),-MathF.Sin(tilt)), Motion.Rotation), Profile.SideForce, previousRight ? 0 : Profile.SideBurst);
			if (UpLeftOn) Force(new Vector2(-14,14), ContactGeometry.Rotate(new Vector2(MathF.Sin(tilt),MathF.Cos(tilt)), Motion.Rotation), Profile.SideForce, previousUpLeft ? 0 : Profile.SideBurst);
			if (UpRightOn) Force(new Vector2(14,14), ContactGeometry.Rotate(new Vector2(-MathF.Sin(tilt),MathF.Cos(tilt)), Motion.Rotation), Profile.SideForce, previousUpRight ? 0 : Profile.SideBurst);
		}

		void Dropship(in CraftInput control) {
			MainOn = true; LeftOn = RightOn = false;
			float target = (control.Vertical * 7.5f + CC.ToMps(Motion.Velocity.Y) - 2.6f) / 7.5f + 0.5f;
			float left = ScaleThrottle(target, Motion.Mass / Profile.EmptyMass), right = left;
			if (Motion.Rotation > MathHelper.Pi / 16) { right = 0.8f; left = -0.8f; }
			else if (Motion.Rotation < -MathHelper.Pi / 16) { right = -0.8f; left = 0.8f; }
			LeftThrottle = left > LeftThrottle ? left * 0.3f + LeftThrottle * 0.7f : left;
			RightThrottle = right > RightThrottle ? right * 0.3f + RightThrottle * 0.7f : right;
			float desiredLateral = -Math.Clamp(control.Steer, -1, 1);
			if (Math.Abs(desiredLateral) > 0.1f) LateralControl += Math.Clamp(desiredLateral - LateralControl, -6 * CC.Dt, 6 * CC.Dt);
			else LateralControl *= 54 * CC.Dt;
			LateralControl = Math.Clamp(LateralControl,-1,1);
			float maximum = MathHelper.ToRadians(20);
			EngineAngle = Math.Abs(Motion.Rotation) > maximum ? Motion.Rotation - Math.Sign(Motion.Rotation) * maximum : -maximum * LateralControl;
			Vector2 thrust = ContactGeometry.Rotate(-Vector2.UnitY, EngineAngle);
			if(Attachments.Has(CraftPart.LeftEngine)) Force(new Vector2(-49,-3), thrust, Profile.MainForce * ThrottleFactor(LeftThrottle));
			if(Attachments.Has(CraftPart.RightEngine)) Force(new Vector2(49,-3), thrust, Profile.MainForce * ThrottleFactor(RightThrottle));
			float balance = 0.9f * Motion.AngularVelocity + 0.8f * Motion.Rotation;
			UpLeftOn = balance > 0.2f; UpRightOn = balance < -0.2f;
			if (UpLeftOn) Force(new Vector2(-33,-16), ContactGeometry.Rotate(Vector2.UnitY,Motion.Rotation), Profile.SideForce, previousUpLeft ? 0 : Profile.SideBurst);
			if (UpRightOn) Force(new Vector2(33,-16), ContactGeometry.Rotate(Vector2.UnitY,Motion.Rotation), Profile.SideForce, previousUpRight ? 0 : Profile.SideBurst);
		}

		/// <summary>Request vertical thrust with approach damping before the hatch joints touch terrain.</summary>
		public float AutomaticDropshipVertical(float gap) => -Math.Clamp((gap*.04f-Motion.Velocity.Y*.8f)/CC.WorldScale,-2.5f,2.5f)*3/7.5f;

		/// <summary>Choose AI steering using the actual angular kick of a new side-engine burst.</summary>
		public float AutomaticRocketSteer(float wantedVelocity) {
			float tilt=Math.Clamp((wantedVelocity-Motion.Velocity.X)*(.04f/CC.WorldScale),-.12f,.12f);
			float wantedOmega=Math.Clamp((tilt-Motion.Rotation)*3,-1,1);
			float error=wantedOmega-Motion.AngularVelocity;
			int direction=Math.Sign(error);
			float lever=ActorContacts.Cross(new Vector2(-12,-41) * CC.WorldScale,new Vector2(MathF.Cos(MathHelper.Pi/8),-MathF.Sin(MathHelper.Pi/8)));
			bool emitting=direction>0?LeftOn:RightOn;
			float kick=lever*(Profile.SideForce*CC.Dt+(emitting?0:Profile.SideBurst))*CC.PPM/Math.Max(1,Motion.Inertia);
			return Math.Abs(error)>kick*.5f?direction:0;
		}

		public bool Applicable(CraftPart part) => Style==ShipStyle.Dropship ? part<=CraftPart.RightEngine || part>=CraftPart.LeftHatch : Style==ShipStyle.Rocket && (part==CraftPart.LeftGear || part==CraftPart.RightGear);
		public Vector2 PartJoint(CraftPart part) => part switch {
			CraftPart.LeftEngine=>new(-49,-3),CraftPart.RightEngine=>new(49,-3),CraftPart.LeftGear=>GearOffset(-1),CraftPart.RightGear=>GearOffset(1),CraftPart.LeftHatch=>new(1,-8),_=>new(-1,-8)
		};
		public bool Detach(CraftPart part) {
			if(!Applicable(part) || !Attachments.Remove(part)) return false;
			Vector2 joint=World((part==CraftPart.LeftGear || part==CraftPart.RightGear)?new Vector2(part==CraftPart.LeftGear?-12:12,29):PartJoint(part));
			float rotation=PartAngle(part);
			Vector2 spriteOrigin=part==CraftPart.LeftGear?new(ShipCollisionMasks.RocketGearHull.Width-4,10):PartOrigin(part);
			var wreck=new CraftWreckPhysics(part);
			Vector2 position=joint+ContactGeometry.Rotate((wreck.Origin-spriteOrigin) * CC.WorldScale,rotation);
			Vector2 velocity=Motion.PointVelocity(position);
			Detached.Enqueue(new(part,position,velocity,rotation,Motion.AngularVelocity));
			float partMass=CraftAttachments.Mass(part);
			Motion.Mass=Math.Max(1,Motion.Mass-partMass);
			Motion.Velocity-=(velocity-Motion.Velocity)*(partMass/Motion.Mass);
			UpdateInertia();
			return true;
		}
		public CraftPart? PartAt(Vector2 point) {
			foreach(CraftPart part in Enum.GetValues<CraftPart>()) {
				if(!Applicable(part) || !Attachments.Has(part)) continue;
				Vector2 joint=World(PartJoint(part));
				if(part<=CraftPart.RightEngine) {
					Vector2 pixel=ContactGeometry.Rotate(point-joint,-EngineAngle)/CC.WorldScale+(part==CraftPart.LeftEngine?new Vector2(29,21):new Vector2(6,21));
					if(MaskContains(part==CraftPart.LeftEngine?ShipCollisionMasks.DropshipEngineLeftFill:ShipCollisionMasks.DropshipEngineRightFill,pixel)) return part;
				} else if(part>=CraftPart.LeftHatch) {
					if(MaskContains(HatchMask(part,true),ContactGeometry.Rotate(point-joint,-PartAngle(part))/CC.WorldScale+PartOrigin(part))) return part;
				} else if(Vector2.DistanceSquared(point,joint)<8*8*CC.WorldScale*CC.WorldScale) return part;
			}
			return null;
		}
		void OnContactImpulse(Vector2 point,Vector2 impulse) { if(PartAt(point-Motion.TerrainFrame.Origin) is CraftPart part) Attachments.Accumulate(part,impulse); }

		public float ImpactDamage => Math.Max(0,(LastImpact - Profile.DamageThreshold) / (Profile.GibLimit - Profile.DamageThreshold));
		public void Write(BinaryWriter w) {
			w.Write(HatchOpen); w.Write(Attachments.Missing); foreach(byte wounds in Attachments.Wounds) w.Write(wounds);
			w.Write(Motion.Rotation); w.Write(Motion.AngularVelocity); w.Write(Motion.Mass);
			w.Write(Gear); w.Write(LateralControl); w.Write(LeftThrottle); w.Write(RightThrottle); w.Write(EngineAngle);
			w.Write(MainOn); w.Write(LeftOn); w.Write(RightOn); w.Write(UpLeftOn); w.Write(UpRightOn);
		}
		public void Read(BinaryReader r) {
			HatchOpen=r.ReadSingle(); Attachments.RestoreMask(r.ReadByte()); for(int i=0;i<6;i++) Attachments.Wounds[i]=r.ReadByte();
			Motion.Rotation=r.ReadSingle(); Motion.AngularVelocity=r.ReadSingle(); Motion.Mass=r.ReadSingle();
			Gear=r.ReadSingle(); LateralControl=r.ReadSingle(); LeftThrottle=r.ReadSingle(); RightThrottle=r.ReadSingle(); EngineAngle=r.ReadSingle();
			MainOn=r.ReadBoolean(); LeftOn=r.ReadBoolean(); RightOn=r.ReadBoolean(); UpLeftOn=r.ReadBoolean(); UpRightOn=r.ReadBoolean(); UpdateInertia();
		}
	}
}
