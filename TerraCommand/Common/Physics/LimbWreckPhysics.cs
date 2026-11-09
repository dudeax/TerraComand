using System;
using Microsoft.Xna.Framework;
using TerraCommand.Common.Actors;
namespace TerraCommand.Common.Physics {
	public sealed class LimbWreckPhysics : IRigidBodyShape {
		public readonly ContactMask Mask;
		public readonly RigidBodyMotion Motion=new();
		public bool Flipped;
		public Vector2 Origin=>new(Mask.Width/2f,Mask.Height/2f);
		public LimbWreckPhysics(ActorKind kind,int limb,int frame) {
			Mask=DetachedLimbMasks.Get(kind,limb,frame); Motion.Shape=this; Motion.Mass=LimbState.PartMass(kind,limb);
			float sum=0; for(int i=0;i<Mask.Points.Length;i+=2) sum+=Vector2.DistanceSquared(new Vector2(Mask.Points[i],Mask.Points[i+1]),Origin);
			Motion.Inertia=Math.Max(1,Motion.Mass*sum/Math.Max(1,Mask.Points.Length/2));
			Motion.Friction=kind==ActorKind.Heavy?.4f:.6f; Motion.Restitution=kind==ActorKind.Heavy?.5f:.6f;
		}
		public float SweepRadius=>Math.Max(Mask.Width,Mask.Height);
		public Vector2 CentreOffset(float rotation)=>Vector2.Zero;
		public int Build(Vector2 anchor,float angle,Vector2[] atoms) {
			int count=0;
			for(int i=0;i<Mask.Points.Length && count<atoms.Length;i+=2) {
				Vector2 point=new(Mask.Points[i]-Origin.X,Mask.Points[i+1]-Origin.Y); if(Flipped) point.X=-point.X;
				atoms[count++]=anchor+ContactGeometry.Rotate(point,angle);
			}
			return count;
		}
		public bool Contains(Vector2 world) {
			Vector2 pixel=ContactGeometry.Rotate(world-Motion.Position,-Motion.Rotation)+Origin;if(Flipped)pixel.X=Mask.Width-pixel.X;
			if(pixel.X<0||pixel.Y<0||pixel.X>=Mask.Width||pixel.Y>=Mask.Height)return false;
			for(int i=0;i<Mask.Points.Length;i+=2)if((int)pixel.X/2==Mask.Points[i]/2&&(int)pixel.Y/2==Mask.Points[i+1]/2)return true;
			return false;
		}

		public void Step(IActorTerrain terrain) { Motion.Velocity.Y+=CC.Gravity; RigidBodyTravel.Step(Motion,this,terrain); }
	}
}
