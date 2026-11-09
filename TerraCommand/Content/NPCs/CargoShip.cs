using ReLogic.Content;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Orbital;
using TerraCommand.Common.Units;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Physics;

namespace TerraCommand.Content.NPCs
{
	public enum ShipState : byte { Descending, Unloading, Waiting, Ascending, Piloted }

	/// <summary>
	/// The craft types (CC has rockets, dropships and unpowered drop crates). Rocket: drops straight down on its engine and
	/// goes back up. Dropship: wide and slow, comes in at an angle, can be piloted. Drop crate: falls unpowered, bursts open on
	/// impact and is left behind - a one-way delivery.
	/// </summary>


	/// <summary>
	/// A cargo rocket between the ground and orbit, as in Cortex Command: ordered from the orbital menu, it flies down to its
	/// landing zone, lands and unloads (ordered Clone Bodies step out as soldiers). While it waits, anything thrown into it and
	/// any idle team body that walks into it is loaded; sent back up, its cargo reaches the orbital stockpile when it leaves the
	/// atmosphere. Enemies can shoot it down: it explodes and the cargo is lost. Server-authoritative; cargo lives on the
	/// authority in <see cref="ShipSystem"/>.
	/// </summary>
	public class CargoShip : ModNPC
	{
		public ShipState State { get => (ShipState)NPC.ai[0]; set => NPC.ai[0] = (float)value; }
		ref float Timer => ref NPC.ai[1];
		public int Team { get => (int)NPC.ai[2]; set => NPC.ai[2] = value; }
		public float LandY { get => NPC.ai[3]; set => NPC.ai[3] = value; }

		public bool Thrusting { get; private set; }
		public ShipStyle Style;
		/// <summary>Where it's landing (dropships steer to it).</summary>
		public float TargetX;
		/// <summary>The player flying it (-1: none).</summary>
		public int Pilot = -1;
		/// <summary>The pilot's controls: 1 left, 2 right, 4 thrust, 8 down.</summary>
		public byte PilotInput;
		public bool Grounded { get; private set; }
		public ShipPhysics Physics { get; private set; } = new(ShipStyle.Rocket);
		public float OrbitY;

		/// <summary>The cargo bay (things thrown or walking into it are loaded).</summary>
		public Rectangle Bay {
			get {
				Physics.Motion.Position = NPC.Bottom;
				Vector2 centre = Physics.World(Style == ShipStyle.Dropship ? new Vector2(0,20) : new Vector2(0,10));
				int width = Style == ShipStyle.Dropship ? 48 : 20;
				return new Rectangle((int)centre.X-width/2,(int)centre.Y-25,width,50);
			}
		}
		public bool CommandedDoorOpen => Style != ShipStyle.Crate
			&& (State == ShipState.Unloading || State == ShipState.Waiting || State == ShipState.Piloted && (Grounded || Style == ShipStyle.Dropship && Math.Abs(LandY - NPC.Bottom.Y - 8) < 8 && Math.Abs(NPC.velocity.Y) < 0.7f));
		public bool DoorOpen => CommandedDoorOpen || Style==ShipStyle.Dropship && (!Physics.Attachments.Has(CraftPart.LeftHatch) || !Physics.Attachments.Has(CraftPart.RightHatch));
		public bool CanPilot => Style == ShipStyle.Dropship || Style == ShipStyle.Rocket;

		/// <summary>Takes on a craft type's size (feet kept where they are) and toughness.</summary>
		public void SetStyle(ShipStyle style)
		{
			Style = style;
			Vector2 bottom = NPC.Bottom;
			Physics = new ShipPhysics(style);
			(NPC.width, NPC.height) = (Physics.Width, Physics.Height);
			NPC.Bottom = bottom;
			if (Main.netMode != NetmodeID.MultiplayerClient && NPC.life == NPC.lifeMax) {
				NPC.lifeMax = style switch { ShipStyle.Dropship => 1200, ShipStyle.Crate => 300, _ => 800 };
				NPC.life = NPC.lifeMax;
			}
		}

		public override void SendExtraAI(System.IO.BinaryWriter w)
		{
			w.Write((byte)Style);
			w.Write(TargetX);
			w.Write((short)Pilot);
			w.Write(PilotInput);
			w.Write(Grounded);
			w.Write(OrbitY); Physics.Write(w);
		}

		public override void ReceiveExtraAI(System.IO.BinaryReader r)
		{
			var style = (ShipStyle)r.ReadByte();
			if (style != Style)
				SetStyle(style);
			TargetX = r.ReadSingle();
			Pilot = r.ReadInt16();
			PilotInput = r.ReadByte();
			Grounded = r.ReadBoolean();
			OrbitY = r.ReadSingle(); Physics.Read(r); NPC.rotation = Physics.Motion.Rotation;
		}

		public override void SetStaticDefaults()
		{
			NPCID.Sets.NPCBestiaryDrawModifiers drawMods = new() { Hide = true };
			NPCID.Sets.NPCBestiaryDrawOffset.Add(Type, drawMods);
			NPCID.Sets.CantTakeLunchMoney[Type] = true;
		}

		public override void SetDefaults()
		{
			Physics = new ShipPhysics(ShipStyle.Rocket);
			NPC.width = Physics.Width;
			NPC.height = Physics.Height;
			NPC.lifeMax = 800;
			NPC.defense = 20;
			NPC.damage = 0;
			NPC.knockBackResist = 0f;
			NPC.aiStyle = -1;
			NPC.friendly = true;          // enemies (and hostile fire) can shoot it down; your own weapons can't
			NPC.noGravity = true;
			NPC.noTileCollide = true;     // it flies its own path and lands itself
			NPC.netAlways = true;
			NPC.HitSound = SoundID.NPCHit4;
			NPC.DeathSound = SoundID.Item14;
			NPC.npcSlots = 0f;
		}

		public override bool CheckActive() => false;   // never despawns on its own
		public override bool? CanBeHitByItem(Player player, Item item) => false;
		/// <summary>Your ships are shot down by hostile fire; the AI faction's by yours.</summary>
		public override bool? CanBeHitByProjectile(Projectile projectile) {
			if(Team==CloneUnit.EnemyTeam ? !projectile.friendly : !projectile.hostile) return false;
			Physics.Motion.Position=NPC.Bottom;
			Rectangle overlap=Rectangle.Intersect(projectile.Hitbox,NPC.Hitbox);
			if(overlap.Width<=0 || overlap.Height<=0) return null; // custom beam/line collision remains on its existing damage path
			for(int x=overlap.Left;x<overlap.Right;x++) for(int y=overlap.Top;y<overlap.Bottom;y++) {
				Vector2 point=new(x+.5f,y+.5f);
				if(Physics.Contains(point) || Physics.PartAt(point).HasValue) return null;
			}
			return false;
		}
		public override bool CanChat() => false;

		// ------------------------------------------------------------ flight

		public override void AI()
		{
			NPC.timeLeft = 600; NPC.friendly = Team != CloneUnit.EnemyTeam;
			Physics.Motion.Position = NPC.Bottom; Physics.Motion.Velocity = NPC.velocity;
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				Physics.Motion.Rotation += Physics.Motion.AngularVelocity * CC.Dt;
				NPC.rotation = Physics.Motion.Rotation;
				Thrusting = Physics.Thrusting;
				if (Thrusting) Exhaust();
				return;
			}
			if (OrbitY <= 0) OrbitY = Math.Min(NPC.position.Y, GroundBelow(NPC.Bottom) - 900);
			if (State == ShipState.Piloted && (Pilot < 0 || !Main.player[Pilot].active || Main.player[Pilot].dead)) Unpilot();
			if (Timer++ % 15 == 0 || LandY <= 0) LandY = GroundBelow(NPC.Bottom);
			CraftInput input = ControlFlight();
			Physics.BayOpen = CommandedDoorOpen;
			Physics.Step(input, ShipSystem.CargoMass(this), TerrariaActorTerrain.Instance);
			SpawnDetachedParts();
			Grounded = Physics.Motion.Grounded;
			NPC.rotation = Physics.Motion.Rotation;
			NPC.velocity = Physics.Motion.Velocity;
			Vector2 solved = Physics.Motion.Position;
			// Terraria adds velocity after AI. The solver has already integrated this tick.
			NPC.Bottom = solved - NPC.velocity;
			Thrusting = Physics.Thrusting;
			if (Physics.ImpactDamage > 0)
				NPC.SimpleStrikeNPC(Math.Max(1, (int)(Physics.ImpactDamage * NPC.lifeMax)), 0, noPlayerInteraction: true);
			if (!NPC.active) return;
			bool arrived = Style == ShipStyle.Dropship
				? Math.Abs(LandY - solved.Y - 8) < 7 && Math.Abs(TargetX - solved.X) < 16 && NPC.velocity.Length() < 0.7f
				: Grounded && NPC.velocity.Length() < 1.5f;
			if (State == ShipState.Descending && arrived) {
				State = ShipState.Unloading; Timer = 0; NPC.netUpdate = true;
				SoundEngine.PlaySound(SoundID.Item53 with { Pitch = -0.5f }, NPC.Center);
			}
			else if (State == ShipState.Unloading) {
				if (Timer == 40) ShipSystem.Unload(this);
				if (Timer >= 60) { State = ShipState.Waiting; Timer = 0; NPC.netUpdate = true; }
			}
			else if (State == ShipState.Waiting) {
				if (Style == ShipStyle.Crate) {
					if (Timer > 600) {
						NPC.active = false;
						if (Main.netMode == NetmodeID.Server) NetMessage.SendData(MessageID.SyncNPC,number:NPC.whoAmI);
					}
				}
				else {
					if (Timer > 30) ShipSystem.LoadFromBay(this);
					if (Team == CloneUnit.EnemyTeam && Timer > 120) Launch();
				}
			}
			else if (State == ShipState.Piloted && (Grounded || Style == ShipStyle.Dropship && Math.Abs(LandY - NPC.Bottom.Y - 8) < 8 && Math.Abs(NPC.velocity.Y) < 0.7f) && Timer % 30 == 0) ShipSystem.LoadFromBay(this);
			else if (State == ShipState.Ascending && solved.Y - NPC.height < OrbitY) ShipSystem.ReachedOrbit(this);
			if (Main.GameUpdateCount % 6 == 0) NPC.netUpdate = true;
			if (Thrusting) Exhaust();
		}

		CraftInput ControlFlight()
		{
			if (State == ShipState.Piloted) {
				float steer = ((PilotInput & 2) != 0 ? 1 : 0) - ((PilotInput & 1) != 0 ? 1 : 0);
				bool up = (PilotInput & 4) != 0, down = (PilotInput & 8) != 0;
				return new(steer, (up ? 1 : 0) - (down ? 1 : 0), up, down);
			}
			if(Style==ShipStyle.Rocket && (State==ShipState.Waiting || State==ShipState.Unloading)) return default;
			float wantedX = Math.Clamp((TargetX - NPC.Bottom.X) * 0.025f, -3, 3);
			float steerAuto = Math.Clamp((wantedX - NPC.velocity.X) * 0.7f, -1, 1);
			if (Style == ShipStyle.Rocket) steerAuto=Physics.AutomaticRocketSteer(wantedX);
			if (State == ShipState.Ascending) return new(steerAuto,1,Style!=ShipStyle.Rocket || Math.Abs(NPC.rotation)<1,false);
			float gap = LandY - NPC.Bottom.Y - (Style == ShipStyle.Dropship ? 8 : 0);
			if (Style == ShipStyle.Dropship) {
				// Brake the approach before the fragile hatch joints touch down.

				// Source velocity controller accepts upward target speed in metres/sec.
				return new(steerAuto, Physics.AutomaticDropshipVertical(gap), true, false);
			}
			float mass = Physics.EmptyMass + ShipSystem.CargoMass(this);
			float brake = Math.Max(0.01f, Physics.Profile.MainForce / mass / 180 - CC.Gravity);
			bool fire = NPC.velocity.Y > MathF.Sqrt(2 * brake * Math.Max(0, gap - 12));
			if (gap < 24 && NPC.velocity.Y < 2.5f) fire = false;
			// Rocket side jets are attitude controls as well as lateral thrusters.
			return new(steerAuto, 0, fire && Math.Abs(NPC.rotation)<1, false);
		}

		/// <summary>Authority: a player takes the controls.</summary>
		public bool Board(Player p)
		{
			if (!CanPilot || Pilot >= 0 || !DoorOpen && State != ShipState.Descending || p.team != Team)
				return false;
			Pilot = p.whoAmI;
			PilotInput = 0;
			State = ShipState.Piloted;
			Grounded = Physics.Motion.Grounded;
			Timer = 0;
			NPC.netUpdate = true;
			Common.Testing.AutoTest.Trace($"ship #{NPC.whoAmI} piloted by {p.name}");
			return true;
		}

		/// <summary>Authority: the pilot leaves; a ship in the air lands itself where it is.</summary>
		public void Unpilot()
		{
			if (State != ShipState.Piloted)
				return;
			Pilot = -1;
			PilotInput = 0;
			TargetX = NPC.Center.X;
			if (Grounded) {
				State = ShipState.Waiting;
			}
			else {
				State = ShipState.Descending;
				LandY = 0f;
			}
			Timer = 0;
			NPC.netUpdate = true;
			Common.Testing.AutoTest.Trace($"ship #{NPC.whoAmI} pilot left");
		}

		/// <summary>The first solid pixel row under the ship's footprint, searching down from <paramref name="from"/>.</summary>
		float GroundBelow(Vector2 from)
		{
			int x0 = (int)NPC.position.X, w = NPC.width;
			for (float y = from.Y; y < Main.maxTilesY * 16 - 200; y += 4f)
				if (FootprintSolid(x0, w, y))
					for (float fine = y - 4f; fine <= y + 4f; fine += 1f)
						if (FootprintSolid(x0, w, fine))
							return fine;
			return Main.maxTilesY * 16 - 200;
		}

		static bool FootprintSolid(int x, int width, float y) {
			for (int dx = 0; dx < width; dx += 2)
				if (TerrariaActorTerrain.Instance.Sample(new Vector2(x + dx,y), out var surface) && !surface.OneWay) return true;
			return false;
		}

		void Exhaust()
		{
			if (Main.dedServ || Style == ShipStyle.Crate)
				return;
			Vector2[] nozzles = Style == ShipStyle.Dropship
				? new[] { Physics.World(new Vector2(-49,25)), Physics.World(new Vector2(49,25)) }
				: new[] { Physics.World(new Vector2(0,33)) };
			for(int nozzleIndex=0;nozzleIndex<nozzles.Length;nozzleIndex++) {
				if(Style==ShipStyle.Dropship && !Physics.Attachments.Has(nozzleIndex==0?CraftPart.LeftEngine:CraftPart.RightEngine)) continue;
				Vector2 nozzle=nozzles[nozzleIndex];
				for (int i = 0; i < 3; i++) {
					var d = Dust.NewDustDirect(nozzle, Style == ShipStyle.Dropship ? 8 : 12, 6, DustID.Torch, Main.rand.NextFloat(-1f, 1f), 5f, 100, default, 2f);
					d.noGravity = true;
				}
				Dust.NewDust(nozzle, 12, 6, DustID.Smoke, Main.rand.NextFloat(-1.5f, 1.5f), 3f, 150, default, 1.6f);
			}
			if (Main.GameUpdateCount % 12 == 0)
				SoundEngine.PlaySound(SoundID.Item34 with { Volume = 0.6f, Pitch = -0.6f }, NPC.Center);
		}

		/// <summary>Send the ship back up (server / singleplayer).</summary>
		public void Launch()
		{
			if (Style == ShipStyle.Crate)
				return;   // a crate is one-way
			if (State == ShipState.Piloted)
				Unpilot();
			if (State != ShipState.Waiting && State != ShipState.Unloading)
				return;
			ShipSystem.LoadFromBay(this);
			State = ShipState.Ascending;
			Timer = 0;
			NPC.netUpdate = true;
			SoundEngine.PlaySound(SoundID.Item62 with { Pitch = -0.4f }, NPC.Center);
		}

		// ------------------------------------------------------------ destruction

		public bool DetachPart(CraftPart part) {
			if(Main.netMode==NetmodeID.MultiplayerClient) return false;
			Physics.Motion.Position=NPC.Bottom;
			bool removed=Physics.Detach(part); SpawnDetachedParts(); NPC.velocity=Physics.Motion.Velocity; NPC.netUpdate|=removed; return removed;
		}
		void SpawnDetachedParts() {
			if(Main.netMode==NetmodeID.MultiplayerClient) return;
			while(Physics.Detached.TryDequeue(out var part)) {
                Common.Testing.AutoTest.Trace($"released {part.Kind} at {Physics.Motion.Position} vel {NPC.velocity} angle {NPC.rotation} open {Physics.HatchOpen}");
				int index=NPC.NewNPC(NPC.GetSource_FromAI(),(int)part.Position.X,(int)part.Position.Y,ModContent.NPCType<CraftWreck>());
				if(index<Main.maxNPCs && Main.npc[index].ModNPC is CraftWreck wreck) wreck.Initialize(part);
				NPC.netUpdate=true;
			}
		}
		public void AttachmentImpulse(Vector2 point,Vector2 impulse) {
			if(Physics.PartAt(point) is CraftPart part && impulse.Length()*60/CC.PPM>CraftAttachments.Strength(part)) DetachPart(part);
		}
		public override void OnHitByProjectile(Projectile projectile,NPC.HitInfo hit,int damageDone) {
			Physics.Motion.Position=NPC.Bottom;
			Rectangle overlap=Rectangle.Intersect(projectile.Hitbox,NPC.Hitbox);
			for(int x=overlap.Left;x<overlap.Right;x++) for(int y=overlap.Top;y<overlap.Bottom;y++) {
				Vector2 point=new(x,y);
				if(Physics.PartAt(point).HasValue) { CraftPartHits.Hit(this,projectile,point); return; }
			}
		}

		public override void HitEffect(NPC.HitInfo hit)
		{
			if (Main.dedServ)
				return;
			for (int i = 0; i < 3; i++)
				Dust.NewDust(NPC.position, NPC.width, NPC.height, DustID.Iron, hit.HitDirection * 2f, -1f);
		}

		public override void OnKill()
		{
			// it blows up, cargo and all
			ShipSystem.Destroyed(this);
			if(Main.netMode!=NetmodeID.MultiplayerClient) {
				Physics.Motion.Position=NPC.Bottom; Physics.Motion.Velocity=NPC.velocity;
				CraftWreckSpawner.Disassemble(Physics);
			}
			PixelTerrain.RequestCarve(NPC.Center, 40f, TerrainRules.ExplosivePower, CarveFlags.Explosion);
			Common.Physics.Explosions.MarkBlasted(NPC.Center, 96f);
			Common.Physics.Explosions.Blast(NPC.Center, 96f, Common.Physics.Tuning.BlastImpulse, 120);
			if (!Main.dedServ) {
				for (int i = 0; i < 50; i++)
					Dust.NewDust(NPC.position, NPC.width, NPC.height, DustID.Smoke, Main.rand.NextFloat(-6, 6), Main.rand.NextFloat(-6, 2), 100, default, 2.2f);
				for (int i = 0; i < 30; i++)
					Dust.NewDust(NPC.position, NPC.width, NPC.height, DustID.Torch, Main.rand.NextFloat(-8, 8), Main.rand.NextFloat(-8, 2), 100, default, 2.5f);
			}
		}

		// ------------------------------------------------------------ drawing

		static Asset<Texture2D> dropshipTex, crateTex;
		static Asset<Texture2D> DropshipTex => dropshipTex ??= ModContent.Request<Texture2D>("TerraCommand/Content/NPCs/Dropship");
		static Asset<Texture2D> CrateTex => crateTex ??= ModContent.Request<Texture2D>("TerraCommand/Content/NPCs/DropCrate");

		public override bool PreDraw(SpriteBatch sb, Vector2 screenPos, Color drawColor)
		{
			Texture2D tex = Style switch {
				ShipStyle.Dropship => DropshipTex.Value, ShipStyle.Crate => CrateTex.Value, _ => TextureAssets.Npc[Type].Value,
			};
			Physics.Motion.Position = NPC.Bottom;
			if (Style == ShipStyle.Dropship) {
				var body = ModContent.Request<Texture2D>("TerraCommand/Content/NPCs/DropshipBody",AssetRequestMode.ImmediateLoad).Value;
				foreach(var part in new[]{CraftPart.LeftHatch,CraftPart.RightHatch}) if(Physics.Attachments.Has(part)) {
					var door=ModContent.Request<Texture2D>("TerraCommand/Content/NPCs/"+(part==CraftPart.LeftHatch?"DropshipHatchLeft":"DropshipHatchRight"),AssetRequestMode.ImmediateLoad).Value;
					sb.Draw(door,Physics.World(Physics.PartJoint(part))-screenPos,null,drawColor,Physics.PartAngle(part),ShipPhysics.PartOrigin(part),1,SpriteEffects.None,0);
				}
				sb.Draw(body,Physics.DrawPosition-screenPos,null,drawColor,NPC.rotation,new Vector2(55,32),1,SpriteEffects.None,0);
				for(int side=-1;side<=1;side+=2) {
					if(!Physics.Attachments.Has(side<0?CraftPart.LeftEngine:CraftPart.RightEngine)) continue;
					var engine=ModContent.Request<Texture2D>("TerraCommand/Content/NPCs/DropshipEngine"+(side<0 ? "Left" : "Right"),AssetRequestMode.ImmediateLoad).Value;
					sb.Draw(engine,Physics.World(new Vector2(side*49,-3))-screenPos,null,drawColor,Physics.EngineAngle,new Vector2(side<0 ? 29 : 6,21),1,SpriteEffects.None,0);
				}
			}
			else sb.Draw(tex, Physics.DrawPosition - screenPos, null, drawColor, NPC.rotation, Physics.TextureOrigin, 1f, SpriteEffects.None, 0f);
			if (Style == ShipStyle.Rocket) {
				int frame = Math.Clamp((int)MathF.Round(Physics.Gear * 4), 0, 4);
				var leg = ModContent.Request<Texture2D>($"TerraCommand/Content/NPCs/RocketLeg{frame}", AssetRequestMode.ImmediateLoad).Value;
				for (int side = -1; side <= 1; side += 2)
					if(Physics.Attachments.Has(side<0?CraftPart.LeftGear:CraftPart.RightGear)) sb.Draw(leg, Physics.World(new Vector2(side * 12,29)) - screenPos, null, drawColor, NPC.rotation,
						new Vector2(side < 0 ? leg.Width - 4 : 4,10), 1f, side < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0);
			}
			// cargo count above a landed ship (local team only)
			if (DoorOpen && Team == Main.LocalPlayer.team) {
				int n = ShipSystem.CargoCountClient(NPC.whoAmI);
				string s = n > 0 ? $"Cargo: {n}" : "Cargo bay empty";
				Vector2 size = FontAssets.MouseText.Value.MeasureString(s) * 0.7f;
				Utils.DrawBorderString(sb, s, NPC.Top - screenPos - new Vector2(size.X / 2f, 22f), Color.LightBlue, 0.7f);
			}
			return false;
		}
	}
}
