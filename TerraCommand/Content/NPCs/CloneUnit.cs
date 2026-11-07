using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Units;

namespace TerraCommand.Content.NPCs
{
	public enum UnitOrder : byte { Sentry, Follow }

	/// <summary>
	/// An idle body. Players can swap control into it at any time; while idle it follows simple orders and
	/// fights with whatever weapon it is holding. On the server/singleplayer <see cref="Loadout"/> is the
	/// full loadout; on multiplayer clients it only carries visuals.
	/// </summary>
	public class CloneUnit : ModNPC
	{
		public UnitLoadout Loadout = new();
		public int Team;
		public UnitOrder Order = UnitOrder.Sentry;
		int fireCooldown;
		int targetIndex = -1;
		Player dummy;

		public override void SetStaticDefaults()
		{
			Main.npcFrameCount[Type] = 1;
			NPCID.Sets.NPCBestiaryDrawModifiers drawMods = new() { Hide = true };
			NPCID.Sets.NPCBestiaryDrawOffset.Add(Type, drawMods);
			NPCID.Sets.ImmuneToRegularBuffs[Type] = false;
		}

		public override void SetDefaults()
		{
			NPC.width = 18;
			NPC.height = 40;
			NPC.lifeMax = 100;
			NPC.damage = 0;
			NPC.defense = 0;
			NPC.friendly = true;
			NPC.aiStyle = -1;
			NPC.knockBackResist = 0.5f;
			NPC.HitSound = SoundID.PlayerHit;
			NPC.DeathSound = SoundID.PlayerKilled;
			NPC.npcSlots = 0f;
			NPC.netAlways = true;
		}

		public override bool CheckActive() => false;
		public override bool CanChat() => false;
		public override bool NeedSaving() => false; // persisted by UnitSystem instead

		public void InitFrom(UnitLoadout loadout, int team, UnitOrder order, int lifeMax)
		{
			Loadout = loadout;
			Team = team;
			Order = order;
			NPC.lifeMax = Math.Max(lifeMax, 20);
			NPC.life = Math.Clamp(loadout.Life, 1, NPC.lifeMax);
			NPC.defense = loadout.Defense;
			NPC.netUpdate = true;
		}

		// ------------------------------------------------------------ AI

		public override void AI()
		{
			Loadout.Life = NPC.life;
			if (fireCooldown > 0)
				fireCooldown--;

			if (Main.netMode == NetmodeID.MultiplayerClient)
				return;

			FindTarget();
			NPC target = targetIndex >= 0 ? Main.npc[targetIndex] : null;
			if (target != null)
				NPC.direction = NPC.spriteDirection = target.Center.X > NPC.Center.X ? 1 : -1;

			bool onGround = NPC.velocity.Y == 0f;
			float wantVX = 0f;
			if (Order == UnitOrder.Follow) {
				Player leader = NearestTeamPlayer();
				if (leader != null) {
					float dx = leader.Center.X - NPC.Center.X;
					if (Math.Abs(dx) > 16 * 5)
						wantVX = Math.Sign(dx) * 3f;
					if (target == null && wantVX != 0)
						NPC.direction = NPC.spriteDirection = Math.Sign(wantVX);
					// Teleport catch-up if hopelessly far behind.
					if (Vector2.Distance(leader.Center, NPC.Center) > 16 * 120) {
						NPC.Bottom = leader.Bottom;
						NPC.netUpdate = true;
					}
				}
			}
			NPC.velocity.X = MathHelper.Lerp(NPC.velocity.X, wantVX, 0.15f);
			if (wantVX != 0f) {
				Collision.StepUp(ref NPC.position, ref NPC.velocity, NPC.width, NPC.height, ref NPC.stepSpeed, ref NPC.gfxOffY);
				if (onGround && NPC.collideX) {
					NPC.velocity.Y = -7.5f;
					NPC.netUpdate = true;
				}
			}

			if (target != null && fireCooldown <= 0)
				TryAttack(target);

			if (Main.GameUpdateCount % 30 == 0 && Math.Abs(NPC.velocity.X) > 0.1f)
				NPC.netUpdate = true;
		}

		void FindTarget()
		{
			if (Main.GameUpdateCount % 10 != (ulong)(NPC.whoAmI % 10))
				return;
			targetIndex = -1;
			float best = 16 * 35;
			foreach (NPC n in Main.ActiveNPCs) {
				if (n.friendly || n.lifeMax <= 5 || !n.CanBeChasedBy(NPC))
					continue;
				float d = Vector2.Distance(n.Center, NPC.Center);
				if (d < best && Collision.CanHitLine(NPC.Center, 1, 1, n.Center, 1, 1)) {
					best = d;
					targetIndex = n.whoAmI;
				}
			}
		}

		Player NearestTeamPlayer()
		{
			Player best = null;
			float bestD = float.MaxValue;
			foreach (Player p in Main.ActivePlayers) {
				if (p.dead || p.team != Team)
					continue;
				float d = Vector2.DistanceSquared(p.Center, NPC.Center);
				if (d < bestD) {
					bestD = d;
					best = p;
				}
			}
			return best;
		}

		void TryAttack(NPC target)
		{
			Item it = Loadout.Held;
			if (it.IsAir || it.damage <= 0 || it.pick > 0 || it.axe > 0 || it.hammer > 0)
				return;
			int cooldown = Math.Max(it.useAnimation, 10) * 2; // idle bodies are clumsier than players
			Vector2 dir = Vector2.Normalize(target.Center - NPC.Center);

			int proj = it.shoot;
			float speed = it.shootSpeed;
			if (it.useAmmo == AmmoID.Bullet) { proj = ProjectileID.Bullet; speed += 5f; }
			else if (it.useAmmo == AmmoID.Arrow) { proj = ProjectileID.WoodenArrowFriendly; speed += 3f; }
			else if (it.useAmmo == AmmoID.Rocket) { proj = ProjectileID.RocketI; speed += 2f; }

			bool simpleProjectile = proj > ProjectileID.None && ContentSamples.ProjectilesByType[proj].aiStyle is 1 or 2 or 16;
			if (simpleProjectile) {
				Projectile.NewProjectile(NPC.GetSource_FromAI(), NPC.Center + dir * 10f, dir * Math.Max(speed, 6f), proj,
					it.damage, it.knockBack, Main.myPlayer);
				fireCooldown = cooldown;
			}
			else if (it.CountsAsClass(DamageClass.Melee) && Vector2.Distance(target.Center, NPC.Center) < 16 * 3.5f) {
				target.SimpleStrikeNPC(it.damage, NPC.direction, knockBack: it.knockBack);
				fireCooldown = cooldown;
			}
		}

		public override void OnKill()
		{
			Loadout.DropAll(NPC.GetSource_Death(), NPC.Hitbox);
		}

		// ------------------------------------------------------------ net

		public override void SendExtraAI(BinaryWriter writer)
		{
			writer.Write((byte)Team);
			writer.Write((byte)Order);
			writer.Write(NPC.lifeMax);
			Loadout.Write(writer, visualsOnly: true);
		}

		public override void ReceiveExtraAI(BinaryReader reader)
		{
			Team = reader.ReadByte();
			Order = (UnitOrder)reader.ReadByte();
			NPC.lifeMax = reader.ReadInt32();
			Loadout = UnitLoadout.Read(reader);
		}

		// ------------------------------------------------------------ drawing

		public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
		{
			dummy ??= new Player();
			Loadout.ApplyVisuals(dummy);
			dummy.direction = NPC.direction == 0 ? 1 : NPC.direction;
			dummy.velocity = NPC.velocity;
			dummy.position = NPC.Bottom - new Vector2(dummy.width / 2f, dummy.height) + new Vector2(0, NPC.gfxOffY);
			dummy.gfxOffY = 0;
			dummy.ResetEffects();
			dummy.ResetVisibleAccessories();
			dummy.UpdateMiscCounter();
			dummy.UpdateDyes();
			dummy.PlayerFrame();

			spriteBatch.End();
			Main.PlayerRenderer.DrawPlayers(Main.Camera, new[] { dummy });
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, Main.DefaultSamplerState, DepthStencilState.None,
				Main.Rasterizer, null, Main.GameViewMatrix.TransformationMatrix);

			// Order marker above the head; highlighted when the mouse hovers this body.
			bool hover = NPC.Hitbox.Contains(Main.MouseWorld.ToPoint());
			string tag = Order == UnitOrder.Follow ? "F" : "S";
			Color c = Team == Main.LocalPlayer.team ? (hover ? Color.Yellow : Color.LightGreen) : Color.Gray;
			Vector2 pos = NPC.Top - screenPos - new Vector2(4, 24);
			Utils.DrawBorderString(spriteBatch, tag, pos, c, 0.8f);
			return false;
		}
	}
}
