using System;
using Microsoft.Xna.Framework;
namespace TerraCommand.Common.Physics {
	public readonly record struct RoundProfile(float Mass,float Sharpness) {
		public static RoundProfile Get(byte kind)=>kind switch {1=>new(.1f,27),2=>new(.063f,40),_=>new(.1f,33)};
	}
	public interface IBulletMatter { bool Sample(Vector2 point,out float resistance); void Spend(Vector2 point,float work); }
	public readonly record struct BulletTravelResult(Vector2 Position,Vector2 Velocity,bool Stopped,int Contacts);
	public static class BulletTravel {
        /// <summary>Low ballistic arc, including the solver's semi-implicit substep gravity.</summary>
        public static Vector2 AimVelocity(Vector2 displacement,float speed,int updates) {
            if(speed<=0 || !float.IsFinite(speed))return Vector2.Zero;
            if(displacement.LengthSquared()<.000001f)return Vector2.UnitX*speed;
            float time=displacement.Length()/speed,g=Actors.CC.Gravity;
            for(int i=0;i<5;i++) {
                float y=displacement.Y-g*time/(2*Math.Max(1,updates));
                float b=speed*speed+g*y,c=displacement.X*displacement.X+y*y;
                float disc=b*b-g*g*c;
                if(disc<0 || b<=0)return Vector2.Normalize(displacement)*speed;
                time=MathF.Sqrt(2*c/(b+MathF.Sqrt(disc)));
            }
            return new(displacement.X/time,(displacement.Y-g*(time*time+time/Math.Max(1,updates))*.5f)/time);
        }
		/// <summary>CC Atom::Travel: momentum * sharpness versus integrity, followed by integrity/impulse retardation.</summary>
		public static BulletTravelResult Step(Vector2 position,Vector2 velocity,RoundProfile profile,int updates,IBulletMatter terrain) {
			float speed=velocity.Length(); if(!float.IsFinite(speed) || speed<.001f)return new(position,Vector2.Zero,true,0);
			Vector2 direction=velocity/speed; float remaining=1,conversion=60f*Math.Max(1,updates)/Actors.CC.PPM;
			int contacts=0,steps=0;
			while(remaining>.00001f && speed>.001f && steps++<8192) {
				float dt=Math.Min(remaining,.5f/speed); Vector2 next=position+direction*speed*dt;
				if(terrain.Sample(next,out float resistance)) {
					contacts++;
					if(!float.IsFinite(resistance))return new(position,Vector2.Zero,true,contacts);
					float work=profile.Mass*speed*conversion*profile.Sharpness;
					float spent=Math.Min(work,Math.Max(0,resistance)); terrain.Spend(next,spent);
					if(work<=resistance+.00001f)return new(position,Vector2.Zero,true,contacts);
					speed=Math.Max(0,speed-spent/(profile.Mass*conversion*profile.Sharpness));
				}
				position=next;remaining-=dt;
			}
			return new(position,direction*speed,remaining>.00001f || speed<=.001f,contacts);
		}
	}
}
