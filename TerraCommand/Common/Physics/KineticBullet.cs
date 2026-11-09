using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.DataStructures;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Combat;
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Net;
namespace TerraCommand.Common.Physics {
	/// <summary>Owner retains NPC damage attribution; the server owns ballistic movement and terrain work.</summary>
	public class KineticBullet : GlobalProjectile,IBulletMatter {
		public override bool InstancePerEntity=>true;
		static uint nextToken;
        [ThreadStatic] static (int Type,Vector2 Velocity)? spawning;
        [ThreadStatic] static FirearmSpec spawningFirearm;
        public static int SpawnRound(IEntitySource source,Vector2 muzzle,Vector2 velocity,int type,int damage,float knockback,int owner,FirearmSpec spec) {
            var previous=spawningFirearm;spawningFirearm=spec;
            try {return Projectile.NewProjectile(source,muzzle,velocity,type,damage,knockback,owner);}
            finally {spawningFirearm=previous;}
        }
        public override void Load()=>On_Projectile.NewProjectile_IEntitySource_float_float_float_float_int_int_float_int_float_float_float+=Spawn;
        public override void Unload()=>On_Projectile.NewProjectile_IEntitySource_float_float_float_float_int_int_float_int_float_float_float-=Spawn;
        static int Spawn(On_Projectile.orig_NewProjectile_IEntitySource_float_float_float_float_int_int_float_int_float_float_float orig,IEntitySource source,float x,float y,float vx,float vy,int type,int damage,float knockback,int owner,float ai0,float ai1,float ai2) {
            var previous=spawning;spawning=(type,new(vx,vy));
            try { return orig(source,x,y,vx,vy,type,damage,knockback,owner,ai0,ai1,ai2); }
            finally {spawning=previous;}
        }
		public uint Token;
		public byte Profile;
		bool initialized,waiting,normalized,hasRunAI;
		Vector2 authoritativePosition,authoritativeVelocity;
		float initialSpeed;
		int initialDamage;
		readonly HashSet<(int,int)> predicted=new();
		public static bool Eligible(Projectile p)=>p.type==ProjectileID.Bullet || p.type==ProjectileID.SilverBullet || p.type==ProjectileID.BulletHighVelocity || p.type==ProjectileID.CrystalBullet || p.type==ProjectileID.CursedBullet || p.type==ProjectileID.IchorBullet || p.type==ProjectileID.ChlorophyteBullet;
		public override void OnSpawn(Projectile p,IEntitySource source) {
			if(!Eligible(p))return;
            // NewProjectile caps aiStyle 1 before OnSpawn; recover the requested muzzle velocity.
            if(TerraCommandConfig.Instance.ProjectilesChipTerrain && spawning is { } requested && requested.Type==p.type)p.velocity=requested.Velocity;
			if(Main.netMode!=NetmodeID.Server || p.owner==255)Token=++nextToken;
			Profile=p.type==ProjectileID.BulletHighVelocity?(byte)1:(byte)0;
			if(spawningFirearm!=null)Configure(p,spawningFirearm);
            else if(source is EntitySource_ItemUse use && use.Item.ModItem is CCFirearm cc) {
				Configure(p,cc.Spec);
			}
			if(TerraCommandConfig.Instance.ProjectilesChipTerrain && p.damage>0)Capture(p);
		}
        void Capture(Projectile p) {initialized=true;initialSpeed=Math.Max(.001f,p.velocity.Length());initialDamage=Math.Max(1,p.damage);authoritativePosition=p.Center;authoritativeVelocity=p.velocity;}
        public override bool ShouldUpdatePosition(Projectile p)=>!initialized || !Eligible(p) || !TerraCommandConfig.Instance.ProjectilesChipTerrain;
		public static void Configure(Projectile p,FirearmSpec spec) {
			if(!Eligible(p))return;var state=p.GetGlobalProjectile<KineticBullet>();
			if(spec.RoundProfile<3)state.Profile=spec.RoundProfile;
			if(!state.normalized) { p.velocity/=p.extraUpdates+1;state.normalized=true;if(state.initialized)state.Capture(p); } p.netUpdate=true;
		}

		public override bool PreAI(Projectile p) {
			if(!Eligible(p)||!TerraCommandConfig.Instance.ProjectilesChipTerrain) {
				if(initialized) { p.tileCollide=true;p.hide=false;if(waiting){p.velocity=authoritativeVelocity;p.damage=Math.Max(1,initialDamage);}waiting=false;initialized=false; }
				return true;
			}
			if(!initialized && p.damage<=0)return true;
			p.tileCollide=false;
			if(!initialized)Capture(p);
            hasRunAI=true;
			p.Center=authoritativePosition;p.velocity=authoritativeVelocity;
			return !waiting;
		}
		public override void PostAI(Projectile p) {
			if(!initialized||!Eligible(p)||!TerraCommandConfig.Instance.ProjectilesChipTerrain)return;
			if(waiting && Main.netMode==NetmodeID.MultiplayerClient) {p.velocity=Vector2.Zero;p.damage=0;p.Center=authoritativePosition;return;}
			// Native AI can steer special rounds, but must not cap CC muzzle velocity.
            Vector2 velocity=authoritativeVelocity;
            if(p.type==ProjectileID.ChlorophyteBullet && p.velocity.LengthSquared()>.000001f)velocity=Vector2.Normalize(p.velocity)*velocity.Length();
            velocity.Y+=CC.Gravity/((p.extraUpdates+1)*(p.extraUpdates+1));
            var result=BulletTravel.Step(authoritativePosition,velocity,RoundProfile.Get(Profile),p.extraUpdates+1,this);
			if(Testing.AutoTest.Enabled && result.Contacts>0)Testing.AutoTest.Trace($"bullet {p.identity} speed {velocity.Length()} initial {initialSpeed} updates {p.extraUpdates+1} profile {Profile}: contacts {result.Contacts} stopped {result.Stopped} outgoing {result.Velocity.Length()}");
			authoritativePosition=result.Position;authoritativeVelocity=result.Velocity;
			if(result.Stopped) {
				p.Center=result.Position;
				if(Main.netMode==NetmodeID.MultiplayerClient) { waiting=true;p.velocity=Vector2.Zero;p.damage=0;p.hide=true; }
				else p.Kill();
				return;
			}
			p.velocity=result.Velocity;p.Center=result.Position;
			p.damage=Math.Max(1,(int)(initialDamage*Math.Min(1,result.Velocity.LengthSquared()/(initialSpeed*initialSpeed))));
			if(Main.netMode==NetmodeID.Server && (result.Contacts>0 || Main.GameUpdateCount%3==0))Broadcast(p,true);
		}
		public bool Sample(Vector2 point,out float resistance) {
			resistance=0;
			var key=((int)MathF.Floor(point.X/2),(int)MathF.Floor(point.Y/2));
			if(Main.netMode==NetmodeID.MultiplayerClient && predicted.Contains(key))return false;
			if(!TerrariaActorTerrain.Instance.Sample(point,out var surface)||surface.OneWay)return false;
			int x=(int)MathF.Floor(point.X/16),y=(int)MathF.Floor(point.Y/16);
			resistance=float.PositiveInfinity;
			if(!TerrainRules.IsCarvable(x,y)||!WorldGen.CanKillTile(x,y))return true;
			int type=Main.tile[x,y].TileType;
			if(!TerrainRules.ExplosionCanAffect(type)||TerrainRules.MinPick(type)>TerrainRules.ExplosivePower)return true;
			resistance=Main.netMode==NetmodeID.MultiplayerClient?TerrainRules.Material(type).Integrity*4:BallisticFractures.Resistance(point);
			return true;
		}
		public void Spend(Vector2 point,float work) {
			if(Main.netMode!=NetmodeID.MultiplayerClient)BallisticFractures.Spend(point,work);
			else if(Sample(point,out float resistance)&&work>=resistance-.0001f)predicted.Add(((int)MathF.Floor(point.X/2),(int)MathF.Floor(point.Y/2)));
		}
		void Broadcast(Projectile p,bool alive) {
			var packet=PacketHandler.Create(PacketType.BulletState);packet.Write((byte)p.owner);packet.Write(p.identity);packet.Write(Token);packet.Write(Profile);
			packet.Write(authoritativePosition.X);packet.Write(authoritativePosition.Y);packet.Write(authoritativeVelocity.X);packet.Write(authoritativeVelocity.Y);packet.Write(p.damage);packet.Write(alive);packet.Send();
		}
		public override void OnKill(Projectile p,int timeLeft) { if(initialized && Main.netMode==NetmodeID.Server)Broadcast(p,false); }
		public override void SendExtraAI(Projectile p,BitWriter bits,BinaryWriter w) {w.Write(Token);w.Write(Profile);}
		public override void ReceiveExtraAI(Projectile p,BitReader bits,BinaryReader r) { uint token=r.ReadUInt32();byte profile=r.ReadByte();if(!hasRunAI){Token=token;Profile=profile<3?profile:(byte)0;} }
		public static void Receive(BinaryReader r) {
			int owner=r.ReadByte(),identity=r.ReadInt32();uint token=r.ReadUInt32();byte profile=r.ReadByte();Vector2 position=new(r.ReadSingle(),r.ReadSingle()),velocity=new(r.ReadSingle(),r.ReadSingle());int damage=r.ReadInt32();bool alive=r.ReadBoolean();
			if(Main.netMode!=NetmodeID.MultiplayerClient)return;
			var p=Main.projectile.FirstOrDefault(p=>p.active&&p.owner==owner&&p.identity==identity);
			if(p==null)return;var state=p.GetGlobalProjectile<KineticBullet>();if(state.Token!=token)return;
			state.Profile=profile<3?profile:(byte)0;state.authoritativePosition=position;state.authoritativeVelocity=velocity;state.waiting=false;p.hide=false;p.Center=position;p.velocity=velocity;p.damage=damage;
			if(!alive)p.Kill();
		}
	}
}
