using System;
using Microsoft.Xna.Framework;
using TerraCommand.Common.Actors;
using TerraCommand.Content.NPCs;
namespace TerraCommand.Common.Physics {
	public enum CraftPart : byte { LeftEngine, RightEngine, LeftGear, RightGear, LeftHatch, RightHatch }
	public readonly record struct DetachedCraftPart(CraftPart Kind, Vector2 Position, Vector2 Velocity, float Rotation, float Spin);
	public sealed class CraftAttachments {
		public byte Missing { get; private set; }
		readonly Vector2[] impulses=new Vector2[6];
		public readonly byte[] Wounds=new byte[6];
		public bool Wound(CraftPart part) => part<=CraftPart.RightGear && Has(part) && ++Wounds[(int)part]>=(part<=CraftPart.RightEngine?35:9);
		public bool Has(CraftPart part) => (Missing & (1<<(int)part))==0;
		public void RestoreMask(byte mask) => Missing=(byte)(mask&63);
		public static float Mass(CraftPart part) => part <= CraftPart.RightEngine ? 300 : part<=CraftPart.RightGear?70:200;
		public static float Strength(CraftPart part) => part <= CraftPart.RightEngine ? 4000 : part<=CraftPart.RightGear?25000:750;
		public void Begin() => Array.Clear(impulses);
		public void Accumulate(CraftPart part,Vector2 impulse) { if(Has(part)) impulses[(int)part]+=impulse*(part==CraftPart.LeftGear || part==CraftPart.RightGear?.5f:1); }
		public bool Overloaded(CraftPart part) => impulses[(int)part].Length()*60/CC.PPM > Strength(part);
		public bool Remove(CraftPart part) {
			if(!Has(part)) return false;
			Missing|=(byte)(1<<(int)part); return true;
		}
	}
	public sealed class CraftWreckPhysics : IRigidBodyShape {
		public readonly RigidBodyMotion Motion=new();
		public readonly CraftPart Kind;
		public readonly ContactMask Mask;
		public readonly Vector2 Origin;
		public CraftWreckPhysics(CraftPart kind) {
			Kind=kind;
			Mask=kind switch { CraftPart.LeftEngine=>ShipCollisionMasks.DropshipEngineLeftHull,CraftPart.RightEngine=>ShipCollisionMasks.DropshipEngineRightHull,CraftPart.LeftHatch=>ShipCollisionMasks.DropshipHatchLeftHull,CraftPart.RightHatch=>ShipCollisionMasks.DropshipHatchRightHull,_=>ShipCollisionMasks.RocketGearHull };
			Origin=kind switch { CraftPart.LeftEngine=>new(12,21),CraftPart.RightEngine=>new(22,21),CraftPart.LeftGear=>new(Mask.Width-8,6),CraftPart.LeftHatch or CraftPart.RightHatch=>new(18,16),_=>new(8,6) };
			Motion.Mass=CraftAttachments.Mass(kind);
			float radiusSum=0;
			for(int i=0;i<Mask.Points.Length;i+=2) radiusSum+=Vector2.DistanceSquared(new Vector2(Mask.Points[i],Mask.Points[i+1]),kind==CraftPart.LeftGear?new Vector2(8,6):Origin);
			Motion.Inertia=Motion.Mass*CC.WorldScale*CC.WorldScale*radiusSum/Math.Max(1,Mask.Points.Length/2);
			Motion.Shape=this; Motion.Friction=.4f; Motion.Restitution=.5f;
		}
		public float SweepRadius=>Math.Max(Mask.Width,Mask.Height)*CC.WorldScale;
		public Vector2 CentreOffset(float rotation)=>Vector2.Zero;
		public int Build(Vector2 anchor,float rotation,Vector2[] atoms) {
			int count=0;
			for(int i=0;i<Mask.Points.Length && count<atoms.Length;i+=2) {
				Vector2 pixel=new(Mask.Points[i],Mask.Points[i+1]);
				if(Kind==CraftPart.LeftGear) pixel.X=Mask.Width-pixel.X;
				Vector2 local=pixel-Origin;
				atoms[count++]=anchor+ContactGeometry.Rotate(local*CC.WorldScale,rotation);
			}
			return count;
		}
		public bool Contains(Vector2 world) {
			Vector2 pixel=ContactGeometry.Rotate(world-Motion.Position,-Motion.Rotation)/CC.WorldScale+Origin;
			if(Kind==CraftPart.LeftGear) pixel.X=Mask.Width-pixel.X;
			var fill=Kind switch {CraftPart.LeftEngine=>ShipCollisionMasks.DropshipEngineLeftFill,CraftPart.RightEngine=>ShipCollisionMasks.DropshipEngineRightFill,CraftPart.LeftHatch=>ShipCollisionMasks.DropshipHatchLeftFill,CraftPart.RightHatch=>ShipCollisionMasks.DropshipHatchRightFill,_=>ShipCollisionMasks.RocketGearFill};
			if(pixel.X<0 || pixel.Y<0 || pixel.X>=fill.Width || pixel.Y>=fill.Height) return false;
			for(int i=0;i<fill.Points.Length;i+=2) if(fill.Points[i]/2==(int)pixel.X/2 && fill.Points[i+1]/2==(int)pixel.Y/2) return true;
			return false;
		}

		public void Step(IActorTerrain terrain) { Motion.Velocity.Y+=CC.Gravity; RigidBodyTravel.Step(Motion,this,terrain); Motion.Rotation=MathHelper.WrapAngle(Motion.Rotation); }
	}
}
