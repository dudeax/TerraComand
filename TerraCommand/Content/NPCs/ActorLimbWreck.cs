using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Physics;
namespace TerraCommand.Content.NPCs {
	public class ActorLimbWreck : ModNPC {
		public override string Texture=>"TerraCommand/Content/Actors/Soldier/ArmFront4";
		ActorKind kind; int limb,frame; bool flip;
		public LimbWreckPhysics Physics {get;private set;}=new(ActorKind.Soldier,2,4);
		public override void SetDefaults() { NPC.width=20;NPC.height=10;NPC.lifeMax=50;NPC.friendly=true;NPC.noGravity=NPC.noTileCollide=true;NPC.aiStyle=-1;NPC.knockBackResist=0;NPC.npcSlots=0;NPC.netAlways=true; }
		public override bool CheckActive()=>false;
		void Style(ActorKind k,int l,int f,bool flipped) { kind=k;limb=l;frame=f;flip=flipped; Physics=new(k,l,f){Flipped=flip}; NPC.width=Physics.Mask.Width*CC.WorldScale;NPC.height=Physics.Mask.Height*CC.WorldScale; }
		public void Initialize(DetachedLimb part) { Style(part.Kind,part.Limb,part.Frame,part.Flip);NPC.Center=part.Position;NPC.velocity=part.Velocity;NPC.rotation=part.Rotation;Physics.Motion.AngularVelocity=part.Spin;NPC.netUpdate=true; }
		public override void AI() {
			NPC.timeLeft=600; Physics.Motion.Position=NPC.Center;Physics.Motion.Velocity=NPC.velocity;Physics.Motion.Rotation=NPC.rotation;
			if(Main.netMode!=NetmodeID.MultiplayerClient) {
				Physics.Step(TerrariaActorTerrain.Instance);NPC.velocity=Physics.Motion.Velocity;NPC.Center=Physics.Motion.Position-NPC.velocity;
				if(++NPC.ai[0]>3600) { NPC.active=false;if(Main.netMode==NetmodeID.Server)NetMessage.SendData(MessageID.SyncNPC,number:NPC.whoAmI); }
				if(Main.GameUpdateCount%12==0)NPC.netUpdate=true;
			} else Physics.Motion.Rotation+=Physics.Motion.AngularVelocity*CC.Dt;
			NPC.rotation=Physics.Motion.Rotation;
		}
		public override void SendExtraAI(BinaryWriter w) { w.Write((byte)kind);w.Write((byte)limb);w.Write((byte)frame);w.Write(flip);w.Write(NPC.rotation);w.Write(Physics.Motion.AngularVelocity); }
		public override void ReceiveExtraAI(BinaryReader r) { Style((ActorKind)r.ReadByte(),r.ReadByte(),r.ReadByte(),r.ReadBoolean());NPC.rotation=r.ReadSingle();Physics.Motion.AngularVelocity=r.ReadSingle(); }
		public override bool PreDraw(SpriteBatch sb,Vector2 screen,Color color) {
			string prefix=kind==ActorKind.Crab||limb<2?"Leg":"Arm";
			var tex=ModContent.Request<Texture2D>($"TerraCommand/Content/Actors/{kind}/{prefix}{(limb%2==0?"Front":"Back")}{frame}",ReLogic.Content.AssetRequestMode.ImmediateLoad).Value;
			sb.Draw(tex,NPC.Center-screen,null,color,NPC.rotation,Physics.Origin,CC.WorldScale,flip?SpriteEffects.FlipHorizontally:SpriteEffects.None,0);return false;
		}
	}
}
