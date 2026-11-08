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
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Orbital;
using TerraCommand.Common.Units;
using TerraCommand.Content.Items.Devices;

namespace TerraCommand.Content.NPCs
{
	/// <summary>CC's AI modes: hold (sentry), follow, patrol, go to a point, gold dig, brain hunt.</summary>
	public enum UnitOrder : byte { Sentry, Follow, Patrol, GoTo, GoldDig, BrainHunt }

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
		/// <summary>Go-To: the point to reach. Patrol: the middle of the beat.</summary>
		public Vector2 Waypoint;
		public readonly CCActor Actor = new();
		int patrolDir = 1, digSearch, shaftSide = 1;
		Point digTile = new(-1, -1);
		public static int OreSentUp;   // tests

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
			NPC.friendly = team != EnemyTeam;   // the AI faction's bodies are hostile: the player's shots hit them
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

			// move: towards whatever the orders say (or nowhere: sentry)
			Vector2? goal = Goal(out float arrive);
			int moveDir = 0;
			float dx = 0f, dy = 0f;
			if (goal is Vector2 g && Actor.SpeedScale > 0f) {
				dx = g.X - NPC.Center.X;
				dy = g.Y - NPC.Bottom.Y;
				if (Math.Abs(dx) > arrive)
					moveDir = Math.Sign(dx);
			}
			input.Left = moveDir < 0;
			input.Right = moveDir > 0;
			input.Fast = moveDir != 0 && Math.Abs(dx) > 16 * 20;

			// blocked (walking into something without getting anywhere), or the goal is well above: jetpack
			if (moveDir != 0 && Actor.Grounded && Math.Abs(NPC.velocity.X) < 0.2f)
				stuckTicks++;
			else if (Actor.Grounded)
				stuckTicks = 0;
			if (Order == UnitOrder.Patrol && stuckTicks > 90) {
				patrolDir = -patrolDir;   // a patrol turns back at walls it can't get over
				stuckTicks = 0;
			}
			bool climb = goal != null && dy < -16 * 4 && (moveDir != 0 || Math.Abs(dx) <= arrive + 16);
			if (stable && (stuckTicks > 20 || climb && Actor.Grounded) && Actor.Fuel > CC.JetMinStartTicks) {
				jetTicks = 40;
				stuckTicks = Math.Min(stuckTicks, 20) - 20;
			}
			if (jetTicks > 0) {
				jetTicks--;
				input.Jump = true;
			}
			input.JumpStart = input.Jump && !prevJump;
			prevJump = input.Jump;

			// aim: at the target, at the ore being dug, or along the way it's going
			input.Aim = target != null ? target.Center - shoulder : new Vector2((moveDir != 0 ? moveDir : Actor.FlipFactor) * 100f, 0f);
			input.SharpAim = target != null && moveDir == 0;

			if (target != null && stable)
				TryAttack(target, pose);
			else if (Order == UnitOrder.GoldDig && stable)
				GoldDig(ref input, shoulder, Math.Abs(dx) <= arrive + 16 || stuckTicks > 10);
			if (reloadTicks > 0 && --reloadTicks == 0) {
				Item gun = Loadout.Held;
				if (Devices.IsFirearm(gun))
					gun.GetGlobalItem<MagazineGlobalItem>().Rounds = Devices.Stats(gun).Magazine;
			}
			if (fireCooldown > 0)
				fireCooldown--;
			return input;
		}

		/// <summary>Where the orders send the body (null: stay put), and how close counts as there.</summary>
		Vector2? Goal(out float arrive)
		{
			arrive = 16 * 2;
			switch (Order) {
				case UnitOrder.Follow: {
					Player leader = NearestTeamPlayer();
					if (leader == null)
						return null;
					arrive = 16 * 5;
					// hopelessly far behind: catch up
					if (Vector2.Distance(leader.Center, NPC.Center) > 16 * 120) {
						NPC.Bottom = leader.Bottom;
						NPC.velocity = Vector2.Zero;
						skipImpact = true;
						NPC.netUpdate = true;
					}
					return new Vector2(leader.Center.X, leader.Bottom.Y);
				}
				case UnitOrder.Patrol: {
					if (Waypoint == Vector2.Zero)
						Waypoint = NPC.Bottom;
					float x = Waypoint.X + patrolDir * 16 * 12;
					if (Math.Abs(x - NPC.Center.X) < arrive + 2)
						patrolDir = -patrolDir;
					return new Vector2(x, NPC.Bottom.Y);
				}
				case UnitOrder.GoTo:
					if (Math.Abs(Waypoint.X - NPC.Center.X) <= arrive && Math.Abs(Waypoint.Y - NPC.Bottom.Y) < 16 * 3) {
						Order = UnitOrder.Sentry;   // arrived: hold here
						NPC.netUpdate = true;
						Common.Testing.AutoTest.Trace($"unit #{NPC.whoAmI} reached its waypoint");
						return null;
					}
					return Waypoint;
				case UnitOrder.GoldDig: {
					CollectOre();
					if (digTile.X < 0 || !IsOre(digTile.X, digTile.Y)) {
						digTile = new Point(-1, -1);
						if (--digSearch <= 0) {
							digSearch = 60;
							digTile = FindOre(30);
						}
					}
					if (digTile.X < 0)
						return null;
					arrive = 16 * 2;
					return new Vector2(digTile.X * 16 + 8, digTile.Y * 16 + 16);
				}
				case UnitOrder.BrainHunt:
					return HuntTarget();
			}
			return null;
		}

		// ------------------------------------------------------------ gold dig

		static bool IsOre(int i, int j)
		{
			if (!WorldGen.InWorld(i, j, 2))
				return false;
			Tile t = Main.tile[i, j];
			return t.HasTile && (TileID.Sets.Ore[t.TileType] || Main.tileSpelunker[t.TileType] && Main.tileSolid[t.TileType]);
		}

		Point FindOre(int radius)
		{
			int ci = (int)(NPC.Center.X / 16), cj = (int)(NPC.Center.Y / 16);
			Point best = new(-1, -1);
			int bestD = int.MaxValue;
			for (int i = ci - radius; i <= ci + radius; i++)
				for (int j = cj - radius; j <= cj + radius; j++) {
					int d = (i - ci) * (i - ci) + (j - cj) * (j - cj);
					if (d < bestD && d <= radius * radius && IsOre(i, j)) {
						bestD = d;
						best = new Point(i, j);
					}
				}
			return best;
		}

		static bool IsDigger(Item it) => it.ModItem is CCDigger || it.pick > 0;

		/// <summary>Dig towards the ore with whatever digger the body carries (it takes it in hand).</summary>
		void GoldDig(ref ActorInput input, Vector2 shoulder, bool there)
		{
			if (digTile.X < 0 || !there)
				return;
			if (!IsDigger(Loadout.Held)) {
				int k = Array.FindIndex(Loadout.Inventory, IsDigger);
				if (k < 0)
					return;
				Loadout.Selected = k;
				NPC.netUpdate = true;
			}
			// at what's left of the ore (a partly dug tile's middle may already be gone)
			Vector2 ore = PixelTerrain.NearestSolidPixel(digTile.X, digTile.Y, shoulder);
			Item dig = Loadout.Held;
			var digger = dig.ModItem as CCDigger;
			float reach = digger != null ? 10f + digger.Spec.FireVelocity * 10f : 16 * 4;
			if (Vector2.Distance(ore, shoulder) > reach - 8f) {
				// out of reach: sink a body-wide shaft under its feet towards the ore, and drop into it (CC diggers)
				ore = NPC.Bottom + new Vector2(Math.Clamp(ore.X - NPC.Center.X, -3f, 3f) + shaftSide * 5f, 6f);
			}
			input.Aim = ore - shoulder;
			input.SharpAim = true;
			if (fireCooldown > 0)
				return;
			// the first solid terrain along the aim, within reach, takes a bite (dropping what it was made of)
			Vector2 dir = input.Aim.SafeNormalize(Vector2.UnitX);
			int power = digger != null ? digger.Strength : dig.pick;
			for (float r = 4f; r <= reach; r += 2f) {
				Vector2 at = shoulder + dir * r;
				if (!PixelTerrain.IsPixelSolid(at.X, at.Y))
					continue;
				PixelTerrain.RequestCarve(at, digger != null ? 4f + digger.Spec.ParticleCount : 5f, power, CarveFlags.DropItems);
				break;
			}
			if (!Main.dedServ)
				for (int k = 0; k < 2; k++) {
					var d = Dust.NewDustDirect(shoulder + dir * Main.rand.NextFloat(8f, reach), 2, 2, DustID.Electric, 0, 0, 150, default, 0.5f);
					d.noGravity = true;
					d.velocity = dir;
				}
			if (dig.UseSound is Terraria.Audio.SoundStyle snd && Main.rand.NextBool(3))
				SoundEngine.PlaySound(snd, NPC.Center);
			fireCooldown = Math.Max(4, dig.useTime * 2);
			shaftSide = -shaftSide;   // the shaft goes down left and right of centre in turn, wide enough to drop into
		}

		/// <summary>CC: dug gold goes to the team's funds. Here dug ore and gems go up to the orbital stockpile.</summary>
		void CollectOre()
		{
			if (Main.GameUpdateCount % 15 != (ulong)(NPC.whoAmI % 15))
				return;
			foreach (Item it in Main.item) {
				if (!it.active || it.createTile < TileID.Dirt || Vector2.Distance(it.Center, NPC.Center) > 16 * 10)
					continue;
				if (!TileID.Sets.Ore[it.createTile] && !Main.tileSpelunker[it.createTile])
					continue;
				OrbitalStockpile.Add(it);
				OreSentUp += it.stack;
				Common.Testing.AutoTest.Trace($"gold dig: {it.stack} {it.Name} sent to orbit");
				it.TurnToAir();
				it.active = false;
				if (Main.netMode == NetmodeID.Server)
					NetMessage.SendData(MessageID.SyncItem, number: Array.IndexOf(Main.item, it));
			}
		}

		// ------------------------------------------------------------ brain hunt

		/// <summary>The nearest enemy brain (another team's brain body or player), else the nearest hostile thing.</summary>
		Vector2? HuntTarget()
		{
			Vector2? best = null;
			float bestD = float.MaxValue;
			void Consider(Vector2 at)
			{
				float d = Vector2.DistanceSquared(at, NPC.Center);
				if (d < bestD) {
					bestD = d;
					best = at;
				}
			}
			foreach (NPC n in Main.ActiveNPCs)
				if (n.ModNPC is CloneUnit u && u.Team != Team && u.Loadout.Kind == ActorKind.Brain)
					Consider(new Vector2(n.Center.X, n.Bottom.Y));
			foreach (Player p in Main.ActivePlayers)
				if (!p.dead && p.team != Team && p.GetModPlayer<RigPlayer>().Kind == ActorKind.Brain && (p.hostile || Team == EnemyTeam))
					Consider(new Vector2(p.Center.X, p.Bottom.Y));
			if (best != null)
				return best;
			foreach (NPC n in Main.ActiveNPCs)
				if (IsEnemy(n) && Vector2.Distance(n.Center, NPC.Center) < 16 * 200)
					Consider(new Vector2(n.Center.X, n.Bottom.Y));
			return best;
		}

		/// <summary>The AI faction's team (CC's enemy player).</summary>
		public const int EnemyTeam = 6;

		/// <summary>Something this body should shoot: a hostile creature, or another team's body.</summary>
		bool IsEnemy(NPC n)
		{
			if (n.ModNPC is CloneUnit u)
				return u.Team != Team && n.life > 0;
			if (Team == EnemyTeam)
				return false;   // the AI faction leaves the wildlife to the player
			return !n.friendly && n.lifeMax > 5 && n.CanBeChasedBy(NPC);
		}

		void FindTarget()
		{
			if (Main.GameUpdateCount % 10 != (ulong)(NPC.whoAmI % 10))
				return;
			targetIndex = -1;
			float best = 16 * 35;
			foreach (NPC n in Main.ActiveNPCs) {
				if (n.whoAmI == NPC.whoAmI || !IsEnemy(n))
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
			writer.WriteVector2(Waypoint);
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
			NPC.friendly = Team != EnemyTeam;
			Order = (UnitOrder)reader.ReadByte();
			Waypoint = reader.ReadVector2();
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
			string tag = Order switch {
				UnitOrder.Follow => "F", UnitOrder.Patrol => "P", UnitOrder.GoTo => "G", UnitOrder.GoldDig => "$", UnitOrder.BrainHunt => "H", _ => "S",
			};
			bool selected = UnitPlayer.Selected.Contains(NPC.whoAmI);
			Color c = Team == Main.LocalPlayer.team ? (hover ? Color.Yellow : selected ? Color.Cyan : Color.LightGreen) : Color.IndianRed;
			if (selected && Team == Main.LocalPlayer.team)
				tag = "[" + tag + "]";
			Vector2 pos = NPC.Top - screenPos - new Vector2(4, 24);
			Utils.DrawBorderString(spriteBatch, tag, pos, c, 0.8f);
			return false;
		}
	}
}
