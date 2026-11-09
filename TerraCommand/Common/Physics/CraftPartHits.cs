using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Net;
using TerraCommand.Common.Units;
using TerraCommand.Content.NPCs;
namespace TerraCommand.Common.Physics {
	/// <summary>Projectile hits execute on their owner. Component damage always commits on the authority.</summary>
	public class CraftPartHits : GlobalProjectile {
		public override bool InstancePerEntity=>true;
		int lastShip=-1,lastActor=-1;
		public static void Hit(CargoShip ship,Projectile projectile,Vector2 point) {
			if(Main.netMode==NetmodeID.MultiplayerClient) {
				if(projectile.owner!=Main.myPlayer || ship.Physics.PartAt(point) is not CraftPart part) return;
				// Publish the projectile before reporting its hit, even when it dies in this update.
				NetMessage.SendData(MessageID.SyncProjectile,number:projectile.whoAmI);
				var packet=PacketHandler.Create(PacketType.CraftPartHit);
				packet.Write((short)ship.NPC.whoAmI); packet.Write(projectile.identity);
				packet.Write((byte)part);
				float rotation=ship.Physics.PartAngle(part);
				Vector2 local=ContactGeometry.Rotate(point-ship.Physics.World(ship.Physics.PartJoint(part)),-rotation);
				packet.Write(local.X); packet.Write(local.Y); packet.Send();
			} else Commit(ship,projectile,point);
		}
		static void Commit(CargoShip ship,Projectile projectile,Vector2 point) {
			var state=projectile.GetGlobalProjectile<CraftPartHits>();
			if(state.lastShip==ship.NPC.whoAmI) return;
			if(ship.Physics.PartAt(point) is not CraftPart part) return;
			state.lastShip=ship.NPC.whoAmI;
			if(ship.Physics.Attachments.Wound(part)) ship.DetachPart(part);
			ship.NPC.netUpdate=true;
		}
		public static void ActorHit(CloneUnit unit,Projectile projectile,int limb) {
			if(Main.netMode==NetmodeID.MultiplayerClient) {
				if(projectile.owner!=Main.myPlayer)return;
				NetMessage.SendData(MessageID.SyncProjectile,number:projectile.whoAmI);
				var packet=PacketHandler.Create(PacketType.ActorPartHit);packet.Write((short)unit.NPC.whoAmI);packet.Write(projectile.identity);packet.Write((byte)limb);packet.Write(unit.Actor.BodyId);packet.Send();
			} else ActorCommit(unit,projectile,limb);
		}
		static void ActorCommit(CloneUnit unit,Projectile projectile,int limb) {
			var state=projectile.GetGlobalProjectile<CraftPartHits>(); if(state.lastActor==unit.NPC.whoAmI || !unit.Actor.Limbs.Has(limb))return;
			state.lastActor=unit.NPC.whoAmI;
			if(unit.Actor.Limbs.Wound(limb)) LimbSystem.Detach(unit,limb);unit.NPC.netUpdate=true;
		}
		public static void ReceiveActor(BinaryReader r,int from) {
			int index=r.ReadInt16(),identity=r.ReadInt32(),limb=r.ReadByte();ulong bodyId=r.ReadUInt64();
			if(Main.netMode!=NetmodeID.Server || from<0 || from>=Main.maxPlayers || !Main.player[from].active || index<0 || index>=Main.maxNPCs || limb>3 || !Main.npc[index].active || Main.npc[index].ModNPC is not CloneUnit unit || unit.Team!=CloneUnit.EnemyTeam || unit.Actor.BodyId!=bodyId)return;
			var projectile=Main.projectile.FirstOrDefault(p=>p.active && p.owner==from && p.identity==identity && p.friendly && p.damage>0);
			if(projectile==null || Vector2.DistanceSquared(projectile.Center,unit.NPC.Center)>MathF.Pow(Math.Max(64,projectile.velocity.Length()*15),2))return;
			ActorCommit(unit,projectile,limb);
		}

		public static void Receive(BinaryReader reader,int from) {
			int index=reader.ReadInt16(),identity=reader.ReadInt32();
			var part=(CraftPart)reader.ReadByte();
			Vector2 local=new(reader.ReadSingle(),reader.ReadSingle());
			if(Main.netMode!=NetmodeID.Server || from<0 || from>=Main.maxPlayers || !Main.player[from].active
				|| index<0 || index>=Main.maxNPCs || !Main.npc[index].active || Main.npc[index].ModNPC is not CargoShip ship
				|| ship.Team!=CloneUnit.EnemyTeam || (byte)part>5 || !ship.Physics.Applicable(part) || !float.IsFinite(local.X) || !float.IsFinite(local.Y) || local.LengthSquared()>200*200) return;
			Projectile projectile=Main.projectile.FirstOrDefault(p=>p.active && p.owner==from && p.identity==identity && p.friendly && p.damage>0);
			if(projectile==null) return;
			ship.Physics.Motion.Position=ship.NPC.Bottom;
			float rotation=ship.Physics.PartAngle(part);
			Vector2 point=ship.Physics.World(ship.Physics.PartJoint(part))+ContactGeometry.Rotate(local,rotation);
			float reach=Math.Max(64,projectile.velocity.Length()*15+projectile.width+projectile.height);
			if(Vector2.DistanceSquared(projectile.Center,point)>reach*reach) return;
			Commit(ship,projectile,point);
		}
	}
}
