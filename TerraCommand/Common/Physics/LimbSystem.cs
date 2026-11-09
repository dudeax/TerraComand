using System;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Net;
using TerraCommand.Content.NPCs;
namespace TerraCommand.Common.Physics {
	public readonly record struct DetachedLimb(ActorKind Kind,int Limb,int Frame,bool Flip,Vector2 Position,Vector2 Velocity,float Rotation,float Spin,ulong BodyId=0) {
		public void Write(BinaryWriter w) { w.Write((byte)Kind); w.Write((byte)Limb); w.Write((byte)Frame); w.Write(Flip); w.Write(Position.X);w.Write(Position.Y);w.Write(Velocity.X);w.Write(Velocity.Y);w.Write(Rotation);w.Write(Spin);w.Write(BodyId); }
		public static DetachedLimb Read(BinaryReader r)=>new((ActorKind)r.ReadByte(),r.ReadByte(),r.ReadByte(),r.ReadBoolean(),new(r.ReadSingle(),r.ReadSingle()),new(r.ReadSingle(),r.ReadSingle()),r.ReadSingle(),r.ReadSingle(),r.ReadUInt64());
	}
	public static class LimbSystem {
		static DetachedLimb Describe(CCActor actor,Vector2 feet,int limb) {
			Vector2 hip=feet+ContactGeometry.HipOffset(actor),joint=ContactGeometry.Joint(actor,hip,actor.Rot,limb);
			Vector2 tip=actor.Contacts.Initialized?actor.Contacts.Limbs[limb].Position+feet-actor.Contacts.Anchor:joint+new Vector2(0,limb<2?14:12);
			if(limb<2) tip-=new Vector2(0,2);
			float maximum=actor.Kind==ActorKind.Crab || limb>=2 && actor.Kind==ActorKind.Brain?12:14;
			int count=actor.Kind==ActorKind.Brain?6:5;
			int frame=Math.Clamp((int)MathF.Floor((Vector2.Distance(joint,tip)/maximum-.5f)*2*count),0,count-1);
			var mask=DetachedLimbMasks.Get(actor.Kind,limb,frame);
			Vector2 origin=limb<2||actor.Kind==ActorKind.Crab?(actor.Kind==ActorKind.Brain?new(2,7):new(1,9)):
				actor.Kind==ActorKind.Heavy?new(5,4):actor.Kind==ActorKind.Brain?new(2,4):new(2.5f,2);
			float angle=MathF.Atan2(tip.Y-joint.Y,tip.X-joint.X);
			if(actor.Flipped) { origin.X=mask.Width-origin.X; angle+=MathHelper.Pi; }
			Vector2 position=joint+ContactGeometry.Rotate(new Vector2(mask.Width/2f,mask.Height/2f)-origin,angle);
			Vector2 centre=feet+ActorContacts.CentreOffset(actor,actor.Rot);
			Vector2 velocity=actor.Vel+ActorContacts.Perpendicular(position-centre)*actor.AngVel*CC.Dt;
			return new(actor.Kind,limb,frame,actor.Flipped,position,velocity,angle,actor.AngVel,actor.BodyId);
		}
		static void Spawn(DetachedLimb limb) {
			int id=NPC.NewNPC(Entity.GetSource_NaturalSpawn(),(int)limb.Position.X,(int)limb.Position.Y,ModContent.NPCType<ActorLimbWreck>());
			if(id<Main.maxNPCs && Main.npc[id].ModNPC is ActorLimbWreck wreck) wreck.Initialize(limb);
		}
		public static bool Detach(CloneUnit unit,int limb) {
			if(Main.netMode==NetmodeID.MultiplayerClient || unit.Actor.Kind==ActorKind.Turret || !unit.Actor.Limbs.Has(limb)) return false;
			var part=Describe(unit.Actor,unit.NPC.Bottom,limb);
			unit.Actor.Limbs.Remove(limb); unit.Actor.Mass=Math.Max(1,unit.Actor.Mass-LimbState.PartMass(unit.Actor.Kind,limb));
			unit.Actor.Vel-=(part.Velocity-unit.Actor.Vel)*(LimbState.PartMass(unit.Actor.Kind,limb)/unit.Actor.Mass);
			unit.NPC.velocity=unit.Actor.Vel;
			if(unit.Actor.Kind!=ActorKind.Crab && !unit.Actor.Limbs.Arms && !unit.Loadout.Held.IsAir) { Item.NewItem(unit.NPC.GetSource_FromAI(),unit.NPC.Hitbox,unit.Loadout.Held.Clone());unit.Loadout.Held.TurnToAir(); }
			unit.NPC.netUpdate=true; Spawn(part); return true;
		}
		public static bool Detach(Player player,int limb) {
			var body=player.GetModPlayer<BodyPhysicsPlayer>(); var actor=body.Actor;
			if(player.whoAmI!=Main.myPlayer || actor.Kind==ActorKind.Turret || !actor.Limbs.Has(limb)) return false;
			var part=Describe(actor,player.Bottom,limb); actor.Limbs.Remove(limb);
			actor.Apply(ActorStats.Get(actor.Kind),ActorInventory.Mass(player));
			actor.Vel-=(part.Velocity-actor.Vel)*(LimbState.PartMass(actor.Kind,limb)/actor.Mass); player.velocity=actor.Vel;
			if(!actor.Limbs.Arms && actor.Kind!=ActorKind.Crab) player.GetModPlayer<ActorInventory>().DropHeld();
			if(Main.netMode==NetmodeID.SinglePlayer) Spawn(part);
			else {
                // Publish the owner position before validating a world-space separation point.
                NetMessage.SendData(MessageID.PlayerControls,number:player.whoAmI);
                var packet=PacketHandler.Create(PacketType.PlayerLimbDetached); part.Write(packet); packet.Send(); }
			return true;
		}
		public static void Receive(BinaryReader r,int from) {
			var part=DetachedLimb.Read(r);
			if(Main.netMode!=NetmodeID.Server || from<0 || from>=Main.maxPlayers || part.Limb>3 || part.Frame>5) return;
			var p=Main.player[from]; var body=p.GetModPlayer<BodyPhysicsPlayer>();
			if(!p.active || p.dead || part.BodyId==0 || body.BodyLocked && part.BodyId!=body.Actor.BodyId || part.Kind!=body.Actor.Kind || part.Kind==ActorKind.Turret || (body.SpawnedLimbs&(1<<part.Limb))!=0) return;
			if(!float.IsFinite(part.Position.X) || !float.IsFinite(part.Position.Y) || !float.IsFinite(part.Velocity.X) || !float.IsFinite(part.Velocity.Y) || !float.IsFinite(part.Rotation) || !float.IsFinite(part.Spin)
				|| Vector2.DistanceSquared(part.Position,p.Center)>96*96 || Vector2.DistanceSquared(part.Velocity,p.velocity)>32*32 || Math.Abs(part.Spin)>100 || part.Frame>=(part.Kind==ActorKind.Brain?6:5)) return;
			body.Actor.BodyId=part.BodyId;
			body.SpawnedLimbs|=(byte)(1<<part.Limb); body.Actor.Limbs.Remove(part.Limb);
			body.Actor.Apply(ActorStats.Get(part.Kind),ActorInventory.Mass(p)); Spawn(part); body.SyncPlayer(-1,-1,false);
		}
	}
}
