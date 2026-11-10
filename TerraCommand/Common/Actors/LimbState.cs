using System;
using System.IO;
using Microsoft.Xna.Framework;
namespace TerraCommand.Common.Actors {
	// Contact indices: front leg, back leg, front arm, back arm (crabs use four legs).
	public sealed class LimbState {
		public byte Missing;
        readonly Vector2[] jointImpulses = new Vector2[4];
        ulong impulseTick = ulong.MaxValue;
        public JointTransfer Impulse(ActorKind kind,int limb,Vector2 impulse,ulong tick) {
            if(!Has(limb) || kind is ActorKind.Brain or ActorKind.Crab or ActorKind.Turret) return default;
            if(impulseTick!=tick) {Array.Clear(jointImpulses);impulseTick=tick;}
            if(!float.IsFinite(impulse.X) || !float.IsFinite(impulse.Y)) return default;
            jointImpulses[limb]+=impulse;
            return LivingJoint.For(kind,limb).Transfer(jointImpulses[limb]);
        }
		public readonly byte[] Wounds=new byte[4];
		public bool Has(int limb)=>limb>=0 && limb<4 && (Missing&(1<<limb))==0;
		public int Legs=>(Has(0)?1:0)+(Has(1)?1:0);
		public ulong Stamp=>Missing|((ulong)Wounds[0]<<8)|((ulong)Wounds[1]<<16)|((ulong)Wounds[2]<<24)|((ulong)Wounds[3]<<32);
		public bool Arms=>Has(2)||Has(3);
        public bool SupportsWeapon(ActorKind kind,bool oneHanded)=>kind==ActorKind.Crab || kind==ActorKind.Turret || (oneHanded?Arms:Has(2)&&Has(3));
		public static float PartMass(ActorKind kind,int limb)=>kind==ActorKind.Crab?12:kind==ActorKind.Heavy?(limb<2?20:12):(limb<2?16:8);
		public float LostMass(ActorKind kind) { float mass=0; for(int i=0;i<4;i++) if(!Has(i)) mass+=PartMass(kind,i); return mass; }
		public bool Remove(int limb) { if(limb<0 || limb>3 || !Has(limb)) return false; Missing|=(byte)(1<<limb); return true; }
		public void TreatWounds() => Array.Clear(Wounds);
		public bool Wound(int limb,ActorKind kind=ActorKind.Soldier) { if(!Has(limb)) return false; return ++Wounds[limb]>=LivingJoint.For(kind,limb).WoundLimit; }
		public void CopyFrom(LimbState other) { Array.Clear(jointImpulses); impulseTick=ulong.MaxValue; Missing=(byte)(other.Missing&15); Array.Copy(other.Wounds,Wounds,4); }
		public void Write(BinaryWriter w) { w.Write(Missing); w.Write(Wounds); }
		public static LimbState Read(BinaryReader r) { var state=new LimbState {Missing=(byte)(r.ReadByte()&15)}; for(int i=0;i<4;i++) state.Wounds[i]=r.ReadByte(); return state; }
		public int? At(CCActor actor,Vector2 feet,Vector2 point) {
			if(!actor.Contacts.Initialized || actor.Kind==ActorKind.Turret) return null;
			Vector2 hip=feet+ContactGeometry.HipOffset(actor); float best=3.5f*3.5f*CC.WorldScale*CC.WorldScale; int? found=null;
			for(int i=0;i<4;i++) {
				if(!Has(i)) continue;
				Vector2 joint=ContactGeometry.Joint(actor,hip,actor.Rot,i),tip=actor.Contacts.Limbs[i].Position+feet-actor.Contacts.Anchor;
				Vector2 axis=tip-joint; float t=Math.Clamp(Vector2.Dot(point-joint,axis)/Math.Max(.001f,axis.LengthSquared()),0,1);
				float distance=Vector2.DistanceSquared(point,joint+axis*t);
				if(distance<best) { best=distance; found=i; }
			}
			return found;
		}
	}
}
