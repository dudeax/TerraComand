using System.IO;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Physics;
namespace TerraCommand.Content.NPCs {
	/// <summary>Authoritative, colliding craft attachment; not cosmetic client gore.</summary>
	public class CraftWreck : ModNPC {
		public override string Texture=>"TerraCommand/Content/NPCs/DropshipEngineLeft";
		public CraftWreckPhysics Physics { get; private set; }=new(CraftPart.LeftEngine);
		public override void SetDefaults() {
			NPC.width=34; NPC.height=52; NPC.lifeMax=500; NPC.damage=0; NPC.friendly=true;
			NPC.noGravity=NPC.noTileCollide=true; NPC.aiStyle=-1; NPC.knockBackResist=0; NPC.netAlways=true; NPC.npcSlots=0;
		}
		public override bool CheckActive()=>false;
		public void Initialize(DetachedCraftPart part) {
			Physics=new(part.Kind); NPC.Center=part.Position; NPC.velocity=part.Velocity;
			Physics.Motion.Position=part.Position; Physics.Motion.Velocity=part.Velocity;
			Physics.Motion.Rotation=NPC.rotation=part.Rotation; Physics.Motion.AngularVelocity=part.Spin;
			NPC.width=Physics.Mask.Width; NPC.height=Physics.Mask.Height; NPC.Center=part.Position;
			NPC.netUpdate=true;
		}
		public override void AI() {
			NPC.timeLeft=600;
			Physics.Motion.Position=NPC.Center; Physics.Motion.Velocity=NPC.velocity;
			if(Main.netMode!=NetmodeID.MultiplayerClient) {
				Physics.Step(TerrariaActorTerrain.Instance);
				NPC.velocity=Physics.Motion.Velocity; NPC.Center=Physics.Motion.Position-NPC.velocity;
				if(++NPC.ai[0]>18000) { NPC.active=false; if(Main.netMode==NetmodeID.Server) NetMessage.SendData(MessageID.SyncNPC,number:NPC.whoAmI); }
				if(Main.GameUpdateCount%12==0) NPC.netUpdate=true;
			} else Physics.Motion.Rotation+=Physics.Motion.AngularVelocity*CC.Dt;
			NPC.rotation=Physics.Motion.Rotation;
		}
		public override void SendExtraAI(BinaryWriter w) { w.Write((byte)Physics.Kind); w.Write(Physics.Motion.AngularVelocity); w.Write(Physics.Motion.Rotation); }
		public override void ReceiveExtraAI(BinaryReader r) {
			var kind=(CraftPart)r.ReadByte(); float spin=r.ReadSingle(); float rotation=r.ReadSingle();
			if(kind!=Physics.Kind) { Physics=new(kind); NPC.width=Physics.Mask.Width; NPC.height=Physics.Mask.Height; }
			Physics.Motion.AngularVelocity=spin; Physics.Motion.Rotation=NPC.rotation=rotation;
		}
		public override bool PreDraw(SpriteBatch sb,Vector2 screenPos,Color color) {
			string name=Physics.Kind switch {CraftPart.LeftEngine=>"DropshipEngineLeft",CraftPart.RightEngine=>"DropshipEngineRight",CraftPart.LeftHatch=>"DropshipHatchLeft",CraftPart.RightHatch=>"DropshipHatchRight",_=>"RocketLeg4"};
			var texture=ModContent.Request<Texture2D>("TerraCommand/Content/NPCs/"+name,ReLogic.Content.AssetRequestMode.ImmediateLoad).Value;
			sb.Draw(texture,NPC.Center-screenPos,null,color,NPC.rotation,Physics.Origin,1,Physics.Kind==CraftPart.LeftGear?SpriteEffects.FlipHorizontally:SpriteEffects.None,0);
			return false;
		}
	}
	/// <summary>Wait until death processing finishes before reusing a destroyed NPC's slot.</summary>
	public class CraftWreckSpawner : ModSystem {
		static readonly Queue<DetachedCraftPart> pending=new();
		public static void Disassemble(ShipPhysics physics) {
			foreach(CraftPart part in System.Enum.GetValues<CraftPart>()) physics.Detach(part);
			while(physics.Detached.TryDequeue(out var part)) pending.Enqueue(part);
		}
		public override void ClearWorld()=>pending.Clear();
		public override void PostUpdateNPCs() {
			if(Main.netMode==NetmodeID.MultiplayerClient) return;
			while(pending.TryDequeue(out var part)) {
				int index=NPC.NewNPC(Entity.GetSource_NaturalSpawn(),(int)part.Position.X,(int)part.Position.Y,ModContent.NPCType<CraftWreck>());
				if(index<Main.maxNPCs && Main.npc[index].ModNPC is CraftWreck wreck) wreck.Initialize(part);
			}
		}
	}

}
