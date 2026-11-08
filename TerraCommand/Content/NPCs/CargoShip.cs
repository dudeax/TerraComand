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
	public enum ShipState : byte { Descending, Unloading, Waiting, Ascending }

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
		/// <summary>The cargo bay: the lower body (things thrown or walking into it are loaded).</summary>
		public Rectangle Bay => new((int)NPC.position.X - 4, (int)NPC.position.Y + 34, NPC.width + 8, NPC.height - 34);
		public bool DoorOpen => State == ShipState.Unloading || State == ShipState.Waiting;

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
		public override bool? CanBeHitByProjectile(Projectile projectile) => projectile.hostile ? null : false;
		public override bool CanChat() => false;

		// ------------------------------------------------------------ flight

		public override void AI()
		{
			NPC.timeLeft = 600;
			Thrusting = false;
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
					if (Main.netMode != NetmodeID.MultiplayerClient && Timer > 30)
						ShipSystem.LoadFromBay(this);
					break;
				case ShipState.Ascending:
					Thrusting = true;
					NPC.velocity.Y = Math.Max(NPC.velocity.Y - Climb, -MaxSpeed * 1.5f);
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
			// fall fast, then brake so it touches down gently
			float want = Math.Min(MaxSpeed, MathF.Sqrt(2f * Brake * Math.Max(0f, dist)) + 0.6f);
			if (NPC.velocity.Y < want)
				NPC.velocity.Y = Math.Min(want, NPC.velocity.Y + 0.4f);
			else {
				NPC.velocity.Y = Math.Max(want, NPC.velocity.Y - Brake * 1.5f);
				Thrusting = true;
			}
			NPC.velocity.X = 0f;
			if (dist <= NPC.velocity.Y + 0.5f) {
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
			if (Main.dedServ)
				return;
			Vector2 nozzle = NPC.Bottom - new Vector2(6, 4);
			for (int i = 0; i < 3; i++) {
				var d = Dust.NewDustDirect(nozzle, 12, 6, DustID.Torch, Main.rand.NextFloat(-1f, 1f), 5f, 100, default, 2f);
				d.noGravity = true;
			}
			Dust.NewDust(nozzle, 12, 6, DustID.Smoke, Main.rand.NextFloat(-1.5f, 1.5f), 3f, 150, default, 1.6f);
			if (Main.GameUpdateCount % 12 == 0)
				SoundEngine.PlaySound(SoundID.Item34 with { Volume = 0.6f, Pitch = -0.6f }, NPC.Center);
		}

		/// <summary>Send the ship back up (server / singleplayer).</summary>
		public void Launch()
		{
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

		public override bool PreDraw(SpriteBatch sb, Vector2 screenPos, Color drawColor)
		{
			Texture2D tex = TextureAssets.Npc[Type].Value;
			Vector2 at = (NPC.position - screenPos).Floor();
			sb.Draw(tex, at, drawColor);
			if (DoorOpen) {
				// the cargo door slides open: a dark bay
				var door = new Rectangle((int)at.X + 8, (int)at.Y + 38, 12, 18);
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
