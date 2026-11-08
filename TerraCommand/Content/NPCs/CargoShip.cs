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

namespace TerraCommand.Content.NPCs
{
	public enum ShipState : byte { Descending, Unloading, Waiting, Ascending, Piloted }

	/// <summary>
	/// The craft types (CC has rockets, dropships and unpowered drop crates). Rocket: drops straight down on its engine and
	/// goes back up. Dropship: wide and slow, comes in at an angle, can be piloted. Drop crate: falls unpowered, bursts open on
	/// impact and is left behind - a one-way delivery.
	/// </summary>
	public enum ShipStyle : byte { Rocket, Dropship, Crate }

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

		public const float MaxSpeed = 12f, Brake = 0.16f, Climb = 0.22f;
		public bool Thrusting { get; private set; }
		public ShipStyle Style;
		/// <summary>Where it's landing (dropships steer to it).</summary>
		public float TargetX;
		/// <summary>The player flying it (-1: none).</summary>
		public int Pilot = -1;
		/// <summary>The pilot's controls: 1 left, 2 right, 4 thrust, 8 down.</summary>
		public byte PilotInput;
		public bool Grounded { get; private set; }

		/// <summary>The cargo bay (things thrown or walking into it are loaded).</summary>
		public Rectangle Bay => Style switch {
			ShipStyle.Dropship => new((int)NPC.position.X + 12, (int)NPC.position.Y + 6, NPC.width - 24, NPC.height),
			ShipStyle.Crate => NPC.Hitbox,
			_ => new((int)NPC.position.X - 4, (int)NPC.position.Y + 34, NPC.width + 8, NPC.height - 34),
		};
		public bool DoorOpen => Style != ShipStyle.Crate
			&& (State == ShipState.Unloading || State == ShipState.Waiting || State == ShipState.Piloted && Grounded);
		public bool CanPilot => Style == ShipStyle.Dropship || Style == ShipStyle.Rocket;

		/// <summary>Takes on a craft type's size (feet kept where they are) and toughness.</summary>
		public void SetStyle(ShipStyle style)
		{
			Style = style;
			Vector2 bottom = NPC.Bottom;
			(NPC.width, NPC.height) = style switch { ShipStyle.Dropship => (60, 32), ShipStyle.Crate => (24, 24), _ => (28, 68) };
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
		}

		public override void ReceiveExtraAI(System.IO.BinaryReader r)
		{
			var style = (ShipStyle)r.ReadByte();
			if (style != Style || NPC.width == 28 && style != ShipStyle.Rocket)
				SetStyle(style);
			TargetX = r.ReadSingle();
			Pilot = r.ReadInt16();
			PilotInput = r.ReadByte();
			Grounded = r.ReadBoolean();
		}

		public override void SetStaticDefaults()
		{
			NPCID.Sets.NPCBestiaryDrawModifiers drawMods = new() { Hide = true };
			NPCID.Sets.NPCBestiaryDrawOffset.Add(Type, drawMods);
			NPCID.Sets.CantTakeLunchMoney[Type] = true;
		}

		public override void SetDefaults()
		{
			NPC.width = 28;
			NPC.height = 68;
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
		public override bool? CanBeHitByProjectile(Projectile projectile) =>
			Team == CloneUnit.EnemyTeam ? (projectile.friendly ? null : false) : projectile.hostile ? null : false;
		public override bool CanChat() => false;

		// ------------------------------------------------------------ flight

		public override void AI()
		{
			NPC.timeLeft = 600;
			Thrusting = false;
			NPC.friendly = Team != CloneUnit.EnemyTeam;
			switch (State) {
				case ShipState.Descending:
					Descend();
					break;
				case ShipState.Unloading:
					NPC.velocity = Vector2.Zero;
					if (++Timer == 40 && Main.netMode != NetmodeID.MultiplayerClient)
						ShipSystem.Unload(this);
					if (Timer >= 60) {
						State = ShipState.Waiting;
						Timer = 0;
						NPC.netUpdate = true;
					}
					break;
				case ShipState.Waiting:
					NPC.velocity = Vector2.Zero;
					Timer++;
					if (Style == ShipStyle.Crate) {
						// an opened crate is just a wreck: it's left behind a while, then gone
						if (Timer > 600 && Main.netMode != NetmodeID.MultiplayerClient) {
							NPC.active = false;
							if (Main.netMode == NetmodeID.Server)
								NetMessage.SendData(MessageID.SyncNPC, number: NPC.whoAmI);
						}
						break;
					}
					if (Main.netMode != NetmodeID.MultiplayerClient && Timer > 30)
						ShipSystem.LoadFromBay(this);
					if (Team == CloneUnit.EnemyTeam && Timer > 120 && Main.netMode != NetmodeID.MultiplayerClient)
						Launch();   // the enemy's dropships go straight back up for more
					break;
				case ShipState.Piloted:
					Fly();
					break;
				case ShipState.Ascending:
					Thrusting = true;
					NPC.velocity.Y = Math.Max(NPC.velocity.Y - (Style == ShipStyle.Dropship ? Climb * 0.7f : Climb), -MaxSpeed * 1.5f);
					NPC.velocity.X = 0f;
					if (NPC.position.Y < 16 * 40 && Main.netMode != NetmodeID.MultiplayerClient)
						ShipSystem.ReachedOrbit(this);
					break;
			}
			if (Thrusting)
				Exhaust();
		}

		void Descend()
		{
			// Find where it will land: the first ground under its footprint (re-checked as it comes down: terrain changes).
			if (Main.netMode != NetmodeID.MultiplayerClient && (LandY <= 0f || Timer++ % 15 == 0)) {
				float ground = GroundBelow(NPC.Bottom);
				if (Math.Abs(ground - LandY) > 1f) {
					LandY = ground;
					NPC.netUpdate = true;
				}
			}
			float dist = LandY - NPC.Bottom.Y;
			if (Style == ShipStyle.Crate) {
				// unpowered: it just falls
				NPC.velocity.Y = Math.Min(NPC.velocity.Y + 0.3f, 16f);
				NPC.velocity.X = 0f;
			}
			else {
				// fall fast, then brake so it touches down gently (a dropship comes in slower, steering to the landing zone)
				float top = Style == ShipStyle.Dropship ? 7f : MaxSpeed;
				float dx = TargetX - NPC.Center.X;
				bool overLz = Style != ShipStyle.Dropship || Math.Abs(dx) < 12f;
				float want = Math.Min(top, MathF.Sqrt(2f * Brake * Math.Max(0f, dist - (overLz ? 0f : 64f))) + (overLz ? 0.6f : 0f));
				if (NPC.velocity.Y < want)
					NPC.velocity.Y = Math.Min(want, NPC.velocity.Y + 0.4f);
				else {
					NPC.velocity.Y = Math.Max(want, NPC.velocity.Y - Brake * 1.5f);
					Thrusting = true;
				}
				NPC.velocity.X = Style == ShipStyle.Dropship ? Math.Clamp(dx * 0.03f, -4f, 4f) : 0f;
				if (Style == ShipStyle.Dropship && Math.Abs(NPC.velocity.X) > 0.5f)
					Thrusting = true;
				if (!overLz)
					return;
			}
			if (dist <= NPC.velocity.Y + 0.5f) {
				if (Style == ShipStyle.Crate) {
					// it hits hard and bursts open
					SoundEngine.PlaySound(SoundID.Item14 with { Volume = 0.6f }, NPC.Center);
					Common.Physics.Explosions.Blast(NPC.Bottom, 40f, Common.Physics.Tuning.BlastImpulse * 0.3f);
				}
				NPC.Bottom = new Vector2(NPC.Bottom.X, LandY);
				NPC.velocity = Vector2.Zero;
				State = ShipState.Unloading;
				Timer = 0;
				NPC.netUpdate = true;
				SoundEngine.PlaySound(SoundID.Item53 with { Pitch = -0.5f }, NPC.Center);
				for (int i = 0; i < 25 && !Main.dedServ; i++)
					Dust.NewDust(NPC.BottomLeft - new Vector2(16, 6), NPC.width + 32, 8, DustID.Smoke, Main.rand.NextFloat(-4, 4), -1f, 120, default, 1.6f);
			}
		}

		/// <summary>Flown by a player: thrust (jump) against gravity, steer left and right, land on anything flat.</summary>
		void Fly()
		{
			Player p = Pilot >= 0 && Pilot < Main.maxPlayers ? Main.player[Pilot] : null;
			if (Main.netMode != NetmodeID.MultiplayerClient && (p == null || !p.active || p.dead)) {
				Unpilot();
				return;
			}
			bool left = (PilotInput & 1) != 0, right = (PilotInput & 2) != 0, up = (PilotInput & 4) != 0, down = (PilotInput & 8) != 0;
			float accel = Style == ShipStyle.Dropship ? 0.18f : 0.08f, top = Style == ShipStyle.Dropship ? 6f : 3f;
			if (left != right)
				NPC.velocity.X = Math.Clamp(NPC.velocity.X + (right ? accel : -accel), -top, top);
			else
				NPC.velocity.X *= Grounded ? 0.8f : 0.97f;
			NPC.velocity.Y += 0.15f + (down ? 0.1f : 0f);
			if (up) {
				NPC.velocity.Y -= Style == ShipStyle.Dropship ? 0.32f : 0.38f;
				Thrusting = true;
			}
			NPC.velocity.Y = Math.Clamp(NPC.velocity.Y, -8f, 10f);
			if (NPC.position.Y < 16 * 45 && NPC.velocity.Y < 0f)
				NPC.velocity.Y = 0f;   // the pilot can't take it to orbit: launch it for that
			float fall = NPC.velocity.Y;
			NPC.velocity = Collision.TileCollision(NPC.position, NPC.velocity, NPC.width, NPC.height);
			bool grounded = fall > 0f && NPC.velocity.Y == 0f;
			if (grounded && fall > 7f && Main.netMode != NetmodeID.MultiplayerClient)
				NPC.SimpleStrikeNPC((int)((fall - 7f) * 60f), 0, noPlayerInteraction: true);   // a hard landing hurts it
			if (grounded != Grounded) {
				Grounded = grounded;
				NPC.netUpdate = true;
			}
			if (Main.netMode != NetmodeID.MultiplayerClient) {
				if (Grounded && ++Timer % 30 == 0)
					ShipSystem.LoadFromBay(this);
				if (Main.GameUpdateCount % 6 == 0)
					NPC.netUpdate = true;
			}
		}

		/// <summary>Authority: a player takes the controls.</summary>
		public bool Board(Player p)
		{
			if (!CanPilot || Pilot >= 0 || !DoorOpen && State != ShipState.Descending || p.team != Team)
				return false;
			Pilot = p.whoAmI;
			PilotInput = 0;
			State = ShipState.Piloted;
			Grounded = NPC.velocity.Y == 0f;
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
				NPC.velocity = Vector2.Zero;
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
				if (Collision.SolidCollision(new Vector2(x0, y), w, 4))
					for (float fine = y - 4f; fine <= y + 4f; fine += 1f)
						if (Collision.SolidCollision(new Vector2(x0, fine), w, 1))
							return fine;
			return Main.maxTilesY * 16 - 200;
		}

		void Exhaust()
		{
			if (Main.dedServ || Style == ShipStyle.Crate)
				return;
			Vector2[] nozzles = Style == ShipStyle.Dropship
				? new[] { NPC.BottomLeft + new Vector2(0, -4), NPC.BottomRight + new Vector2(-8, -4) }
				: new[] { NPC.Bottom - new Vector2(6, 4) };
			foreach (Vector2 nozzle in nozzles) {
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
			Vector2 at = (NPC.position - screenPos).Floor();
			if (Style == ShipStyle.Crate && State != ShipState.Descending) {
				// burst open: the panels lie splayed on the ground
				var src = new Rectangle(0, 4, 24, 20);
				sb.Draw(tex, at + new Vector2(-10, 8), src, drawColor, -0.6f, Vector2.Zero, new Vector2(0.5f, 1f), SpriteEffects.None, 0f);
				sb.Draw(tex, at + new Vector2(22, 4), src, drawColor, 0.6f, Vector2.Zero, new Vector2(0.5f, 1f), SpriteEffects.None, 0f);
				sb.Draw(tex, at + new Vector2(0, 20), new Rectangle(0, 20, 24, 4), drawColor);
				return false;
			}
			sb.Draw(tex, at, drawColor);
			if (DoorOpen) {
				// the cargo door slides open: a dark bay
				var door = Style == ShipStyle.Dropship ? new Rectangle((int)at.X + 18, (int)at.Y + 10, 14, 12) : new Rectangle((int)at.X + 8, (int)at.Y + 38, 12, 18);
				sb.Draw(TextureAssets.MagicPixel.Value, door, new Color(18, 18, 24).MultiplyRGB(drawColor) * 1f);
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
