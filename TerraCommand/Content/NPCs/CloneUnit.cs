using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Combat;
using TerraCommand.Common.Units;

namespace TerraCommand.Content.NPCs
{
	public enum UnitOrder : byte { Sentry, Follow }

	/// <summary>
	/// An idle body: the same Cortex Command actor as a controlled one (<see cref="CCActor"/>: legs, jetpack, balance,
	/// ragdoll, impact damage), driven by a simple CC-style AI through the same controls a player uses. Players can swap
	/// control into it at any time. It follows or holds position, aims at enemies in sight and fires its held weapon
	/// (magazines, reloads and spread as for players), and jets over what blocks its way. Server-authoritative; clients get
	/// its posture for drawing. On the server/singleplayer <see cref="Loadout"/> is the full loadout; on clients only visuals.
	/// </summary>
	public class CloneUnit : ModNPC
	{
		public UnitLoadout Loadout = new();
		public int Team;
		public UnitOrder Order = UnitOrder.Sentry;
		public readonly CCActor Actor = new();

		int targetIndex = -1;
		int fireCooldown, reloadTicks, stuckTicks, jetTicks;
		bool prevJump, skipImpact = true;
		Vector2 velRequested;
		float impactAccum;
		int impactTimer;
		float walkDist;

		public int KnockedTicks => Actor.Status == ActorStatus.Unstable ? Math.Max(1, CC.StableRecoverTicks - Actor.UnstableTicks) : 0;

		public override void SetStaticDefaults()
		{
			Main.npcFrameCount[Type] = 1;
			NPCID.Sets.NPCBestiaryDrawModifiers drawMods = new() { Hide = true };
			NPCID.Sets.NPCBestiaryDrawOffset.Add(Type, drawMods);
			NPCID.Sets.ImmuneToRegularBuffs[Type] = false;
		}

		public override void SetDefaults()
		{
			NPC.width = CC.BodyWidth;
			NPC.height = 42;
			NPC.lifeMax = 100;
			NPC.damage = 0;
			NPC.defense = 0;
			NPC.friendly = true;
			NPC.aiStyle = -1;
			NPC.knockBackResist = 0f;      // pushed by blast physics (Explosions) and its own recoil, not vanilla knockback
			NPC.noGravity = true;          // the actor applies CC gravity itself
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

		public void KnockDown(int dir)
		{
			Actor.Shove(new Vector2(dir * 0.5f, -0.5f), knockOver: true);
			skipImpact = true;
			NPC.netUpdate = true;
		}

		/// <summary>Thrown by a blast (server/singleplayer).</summary>
		public void Blasted(Vector2 impulse)
		{
			NPC.velocity += impulse;
			Actor.Shove(Vector2.Zero, knockOver: impulse.Length() > 2f);
			skipImpact = true;
			NPC.netUpdate = true;
		}

		/// <summary>Moved by a teleport (orders, catch-up): not an impact.</summary>
		public void SkipImpactCheck() => skipImpact = true;

		// ------------------------------------------------------------ AI (server / singleplayer)

		public override void AI()
		{
			Loadout.Life = NPC.life;
			if (NPC.velocity.Y == 0f)
				walkDist += Math.Abs(NPC.velocity.X);
			NPC.direction = NPC.spriteDirection = Actor.FlipFactor;
			if (Main.netMode == NetmodeID.MultiplayerClient)
				return; // posture arrives from the server (ReceiveExtraAI); vanilla moves the body by the synced velocity

			// what the terrain did to last tick's move
			bool grounded = NPC.velocity.Y == 0f && velRequested.Y >= 0f;
			// walking down rough ground: a few pixels' drop is a step, not a fall
			if (!grounded && Actor.Grounded && !Actor.Jetting && Actor.Status == ActorStatus.Stable && NPC.velocity.Y >= 0f && NPC.velocity.Y < 1.5f) {
				float drop = BodyGround.DropToGround(NPC.position, NPC.width, NPC.height);
				if (drop >= 0f) {
					NPC.position.Y += drop;
					NPC.velocity.Y = 0f;
					velRequested.Y = 0f;
					grounded = true;
				}
			}
			TrackImpacts();
			Actor.AfterMove(NPC.velocity, grounded);
			UpdateBodyStats();

			ActorInput input = Think();
			Actor.Vel = NPC.velocity;
			Actor.Control(input);
			Actor.Physics(input);
			if (Actor.Grounded && Actor.Status == ActorStatus.Stable && !Actor.Jetting) {
				int slip = BodyGround.SlipDirection(NPC.position, NPC.width, NPC.height);
				if (slip != 0 && Math.Sign(Actor.Vel.X) != -slip)
					Actor.Vel.X = slip * Math.Max(Math.Abs(Actor.Vel.X), 0.6f);
			}
			if (input.Left || input.Right)
				Collision.StepUp(ref NPC.position, ref Actor.Vel, NPC.width, NPC.height, ref NPC.stepSpeed, ref NPC.gfxOffY);
			NPC.velocity = Actor.Vel;
			velRequested = NPC.velocity;
			if (Actor.Jetting && !Main.dedServ)
				JetEffects();

			SyncIfChanged();
		}

		void UpdateBodyStats()
		{
			ActorStats stats = ActorStats.Get(Loadout.Kind);
			Actor.MaxHealth = NPC.lifeMax;
			if (Main.GameUpdateCount % 60 == (ulong)(NPC.whoAmI % 60) || Actor.StandHeight != stats.StandHeight) {
				float m = 0f;
				foreach (Item it in Loadout.Inventory)
					m += ActorInventory.ItemMass(it);
				for (int i = 0; i < 10; i++)
					m += ActorInventory.ItemMass(Loadout.Armor[i]);
				Actor.FuelMax = stats.Fuel;
				Actor.JetForce = stats.JetForce;
				Actor.Apply(stats, m);
				// the hitbox fits the body type (drones and turrets are short), feet kept in place
				if (NPC.height != stats.StandHeight) {
					NPC.position.Y += NPC.height - stats.StandHeight;
					NPC.height = stats.StandHeight;
				}
			}
		}

		/// <summary>Terraria stops a body over a couple of ticks; speed lost over consecutive ticks is one impact (CC damage).</summary>
		void TrackImpacts()
		{
			float loss = (velRequested - NPC.velocity).Length();
			if (skipImpact) {
				impactAccum = 0f;
				impactTimer = 0;
				skipImpact = false;
			}
			else if (loss > 0.5f) {
				impactAccum += loss;
				impactTimer = 2;
			}
			else if (impactTimer > 0 && --impactTimer == 0) {
				float damage = Actor.ImpactDamage(impactAccum);
				if (damage > 0f)
					NPC.SimpleStrikeNPC(Math.Max(1, (int)(damage * NPC.lifeMax)), 0, noPlayerInteraction: true);
				impactAccum = 0f;
			}
		}

		/// <summary>The unit's "controller": a simple CC-style AI that presses the same buttons a player would.</summary>
		ActorInput Think()
		{
			var input = new ActorInput();
			FindTarget();
			NPC target = targetIndex >= 0 && Main.npc[targetIndex].active ? Main.npc[targetIndex] : null;
			RigPose pose = Pose(Color.White);
			Vector2 shoulder = RigRenderer.Shoulder(pose);
			bool stable = Actor.Status == ActorStatus.Stable;

			// move: follow the nearest team player, or hold position
			int moveDir = 0;
			float leaderDy = 0f, leaderDx = 0f;
			if (Order == UnitOrder.Follow) {
				Player leader = NearestTeamPlayer();
				if (leader != null) {
					leaderDx = leader.Center.X - NPC.Center.X;
					leaderDy = leader.Bottom.Y - NPC.Bottom.Y;
					if (Math.Abs(leaderDx) > 16 * 5)
						moveDir = Math.Sign(leaderDx);
					// hopelessly far behind: catch up
					if (Vector2.Distance(leader.Center, NPC.Center) > 16 * 120) {
						NPC.Bottom = leader.Bottom;
						NPC.velocity = Vector2.Zero;
						skipImpact = true;
						NPC.netUpdate = true;
					}
				}
			}
			input.Left = moveDir < 0;
			input.Right = moveDir > 0;
			input.Fast = moveDir != 0 && Math.Abs(leaderDx) > 16 * 20;

			// blocked (walking into something without getting anywhere), or the leader is well above: jetpack
			if (moveDir != 0 && Actor.Grounded && Math.Abs(NPC.velocity.X) < 0.2f)
				stuckTicks++;
			else if (Actor.Grounded)
				stuckTicks = 0;
			if (stable && (stuckTicks > 20 || moveDir != 0 && leaderDy < -16 * 5 && Actor.Grounded) && Actor.Fuel > CC.JetMinStartTicks) {
				jetTicks = 40;
				stuckTicks = 0;
			}
			if (jetTicks > 0) {
				jetTicks--;
				input.Jump = true;
			}
			input.JumpStart = input.Jump && !prevJump;
			prevJump = input.Jump;

			// aim: at the target, or along the way it's going
			input.Aim = target != null ? target.Center - shoulder : new Vector2((moveDir != 0 ? moveDir : Actor.FlipFactor) * 100f, 0f);
			input.SharpAim = target != null && moveDir == 0;

			if (target != null && stable)
				TryAttack(target, pose);
			if (reloadTicks > 0 && --reloadTicks == 0) {
				Item gun = Loadout.Held;
				if (Devices.IsFirearm(gun))
					gun.GetGlobalItem<MagazineGlobalItem>().Rounds = Devices.Stats(gun).Magazine;
			}
			if (fireCooldown > 0)
				fireCooldown--;
			return input;
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

		/// <summary>Fire the held weapon CC-style: from the muzzle along the aim, with magazine, reload and shake.</summary>
		void TryAttack(NPC target, RigPose pose)
		{
			Item it = Loadout.Held;
			if (it.IsAir || it.damage <= 0 || it.pick > 0 || it.axe > 0 || it.hammer > 0 || fireCooldown > 0 || reloadTicks > 0)
				return;
			// only once the aim has swung onto the target
			Vector2 shoulder = RigRenderer.Shoulder(pose);
			float wantAngle = (target.Center - shoulder).ToRotation();
			float aimAngle = RigRenderer.AimWorld(pose);
			if (Math.Abs(MathHelper.WrapAngle(wantAngle - aimAngle)) > 0.15f)
				return;

			if (it.CountsAsClass(DamageClass.Melee) && it.shoot <= ProjectileID.None) {
				if (Vector2.Distance(target.Center, NPC.Center) < 16 * 3.5f) {
					target.SimpleStrikeNPC(it.damage, Actor.FlipFactor, knockBack: it.knockBack);
					fireCooldown = Math.Max(it.useAnimation, 10);
				}
				return;
			}

			int proj = it.shoot;
			float speed = it.shootSpeed;
			CCFirearm cc = it.ModItem as CCFirearm;
			if (cc == null && it.useAmmo == AmmoID.Bullet) { proj = ProjectileID.Bullet; speed += 5f; }
			else if (it.useAmmo == AmmoID.Arrow) { proj = ProjectileID.WoodenArrowFriendly; speed += 3f; }
			else if (it.useAmmo == AmmoID.Rocket) { proj = ProjectileID.RocketI; speed += 2f; }
			if (cc == null && (proj <= ProjectileID.None || ContentSamples.ProjectilesByType[proj].aiStyle is not (1 or 2 or 16)))
				return; // Terraria weapons with special behaviour need a player to use them

			// CC shake: sharp aim narrows it, firing from the air doubles it
			float shakeDeg = 3f;
			if (Devices.IsFirearm(it)) {
				FirearmStats s = Devices.Stats(it);
				shakeDeg = s.ShakeDeg - (s.ShakeDeg - s.SharpShakeDeg) * Actor.SharpAim;
				var mag = it.GetGlobalItem<MagazineGlobalItem>();
				if (mag.Loaded(it) <= 0) {
					reloadTicks = s.ReloadTicks;
					return;
				}
				mag.Rounds--;
			}
			if (!Actor.Grounded)
				shakeDeg *= 2f;
			Vector2 dir = aimAngle.ToRotationVector2();
			float dev = Devices.Gaussian() * MathHelper.ToRadians(shakeDeg / 2f);
			Vector2 muzzle = RigRenderer.GunAt(pose) + dir * 16f;
			if (cc != null)
				CCFirearm.Fire(NPC.GetSource_FromAI(), muzzle, dir.RotatedBy(dev) * speed, proj, it.damage, it.knockBack, Main.myPlayer, cc.Spec);
			else
				Projectile.NewProjectile(NPC.GetSource_FromAI(), muzzle, dir.RotatedBy(dev) * Math.Max(speed, 6f), proj,
					it.damage, it.knockBack, Main.myPlayer);
			NPC.velocity -= dir * it.knockBack * 0.06f * CC.Mass / Actor.Mass;   // recoil
			fireCooldown = Math.Max(it.useTime, 6);
			SoundEngine.PlaySound(it.UseSound ?? SoundID.Item11, NPC.Center);
		}

		void JetEffects()
		{
			Vector2 nozzle = RigRenderer.NozzleWorld(Pose(Color.White));
			var d = Dust.NewDustDirect(nozzle - new Vector2(3), 6, 6, DustID.Torch, 0f, 4f, 100, default, 1.4f);
			d.noGravity = true;
		}

		// ------------------------------------------------------------ net: posture for drawing on clients

		sbyte sentRot, sentAim;
		ActorStatus sentStatus;
		bool sentFlip;

		void SyncIfChanged()
		{
			if (Main.netMode != NetmodeID.Server)
				return;
			sbyte rot = Quant(Actor.Rot), aim = Quant(Actor.AimAngle);
			if (Math.Abs(rot - sentRot) > 3 || Math.Abs(aim - sentAim) > 3 || Actor.Status != sentStatus || Actor.Flipped != sentFlip
				|| Main.GameUpdateCount % 30 == (ulong)(NPC.whoAmI % 30) && Math.Abs(NPC.velocity.X) > 0.1f)
				NPC.netUpdate = true;
		}

		static sbyte Quant(float r) => (sbyte)Math.Clamp((int)MathF.Round(MathHelper.WrapAngle(r) * 32f), -127, 127);

		public override void SendExtraAI(BinaryWriter writer)
		{
			writer.Write((byte)Team);
			writer.Write((byte)Order);
			writer.Write(NPC.lifeMax);
			writer.Write((byte)Actor.Status);
			writer.Write((byte)Actor.Prone);
			writer.Write(Actor.Flipped);
			writer.Write(sentRot = Quant(Actor.Rot));
			writer.Write(sentAim = Quant(Actor.AimAngle));
			writer.Write((byte)(Actor.CrouchAmount * 10));
			sentStatus = Actor.Status;
			sentFlip = Actor.Flipped;
			Loadout.Write(writer, visualsOnly: true);
		}

		public override void ReceiveExtraAI(BinaryReader reader)
		{
			Team = reader.ReadByte();
			Order = (UnitOrder)reader.ReadByte();
			NPC.lifeMax = reader.ReadInt32();
			Actor.Status = (ActorStatus)reader.ReadByte();
			Actor.Prone = (ProneState)reader.ReadByte();
			Actor.Flipped = reader.ReadBoolean();
			Actor.Rot = reader.ReadSByte() / 32f;
			Actor.AimAngle = reader.ReadSByte() / 32f;
			Actor.CrouchAmount = reader.ReadByte() / 10f;
			Loadout = UnitLoadout.Read(reader);
		}

		// ------------------------------------------------------------ death

		public override void OnKill()
		{
			Loadout.DropAll(NPC.GetSource_Death(), NPC.Hitbox);
			if (Loadout.Kind == ActorKind.Brain)
				BrainSystem.BrainDestroyed(Team); // the idle brain was destroyed: game over for its team
		}

		// ------------------------------------------------------------ drawing

		public RigPose Pose(Color light) => new() {
			Kind = Loadout.Kind,
			Feet = NPC.Bottom + new Vector2(0f, NPC.gfxOffY),
			Dir = Actor.FlipFactor,
			Rot = Actor.Rot,
			AimAngle = Actor.AimAngle,
			Crouch = Actor.Status == ActorStatus.Stable && Actor.Prone == ProneState.NotProne ? Actor.CrouchAmount : 0f,
			Prone = Actor.Prone != ProneState.NotProne && Actor.Status == ActorStatus.Stable,
			Unstable = Actor.Status != ActorStatus.Stable,
			Grounded = NPC.velocity.Y == 0f,
			Sink = NPC.velocity.Y == 0f && Actor.Status == ActorStatus.Stable ? BodyGround.FeetSink(NPC.position, NPC.width, NPC.height) : 0f,
			VelX = NPC.velocity.X,
			WalkDist = walkDist,
			Held = Loadout.Held,
			Light = light,
		};

		static readonly List<RigPiece> rigPieces = new();

		public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
		{
			rigPieces.Clear();
			RigRenderer.Build(Pose(drawColor), rigPieces);
			RigRenderer.Draw(spriteBatch, rigPieces);

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
