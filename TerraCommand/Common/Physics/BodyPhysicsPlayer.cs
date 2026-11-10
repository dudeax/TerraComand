using System;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Net;

namespace TerraCommand.Common.Physics
{
	public enum Stance : byte { Stand, Crouch, Prone }

	/// <summary>
	/// The body a player controls is a Cortex Command actor (<see cref="CCActor"/>): CC controls (A/D walk, W jetpack,
	/// Ctrl crouch, S prone, Shift run, mouse aim), CC gravity, leg-driven walking, burst-and-thrust jetpack, a torso that
	/// leans and can be thrown off its feet, and travel-impulse damage. Vanilla movement is switched off: vanilla (with the
	/// pixel collision) only moves the hitbox through the terrain. Wings / rocket boots / extra jumps upgrade the jetpack.
	/// </summary>
	public class BodyPhysicsPlayer : ModPlayer
	{
		public readonly CCActor Actor = new();
		public ActorInput Input;
		bool prevJump, skipImpact = true, contactMoved;
		[ThreadStatic] static Player contactUpdateOwner;
		bool OwnsCollision => UsesContacts || !Simulated && Actor.Contacts.Initialized;
		bool UsesContacts => Simulated && Player.grappling[0] < 0 && !Player.pulley && Player.GetModPlayer<Orbital.PilotPlayer>().Ship == null;
		Vector2 velRequested;
		float impactAccum;
		int impactTimer;
		int width = CC.BodyWidth;
		int height = CC.StandHeight;   // the hitbox height right now (per posture and body type)

		// ---- API used by aiming, explosions, units and tests
		public bool Grounded => Actor.Grounded;
		public bool Jetting => Actor.Jetting;
		public bool Prone => Actor.Prone != ProneState.NotProne && Actor.Status == ActorStatus.Stable;
		public bool Crouching => !Prone && Actor.CrouchAmount > 0.5f && Actor.Status == ActorStatus.Stable;
		public Stance Stance => Prone ? Stance.Prone : Crouching ? Stance.Crouch : Stance.Stand;
		public int KnockedTicks => Actor.Status == ActorStatus.Unstable ? Math.Max(1, CC.StableRecoverTicks - Actor.UnstableTicks) : 0;
		public float FuelMax => Actor.FuelMax;
		public float Fuel => Actor.Fuel;
		bool Simulated => Player.whoAmI == Main.myPlayer && !Player.dead;

		/// <summary>Bodies are narrower than a Terraria character (kept centred). Applied to every player on every machine.</summary>
		public override void PreUpdate()
		{
			if (Player.width != width && !Player.mount.Active) {
				Player.position.X += (Player.width - width) / 2f;
				Player.width = width;
			}
		}

		// ------------------------------------------------------------ gear -> jetpack

		public override void PostUpdateEquips()
		{
			float fuel = CC.JetTimeTicks, force = CC.JetForce;
			if (Player.wingTimeMax > 0) {
				fuel += Player.wingTimeMax * 0.5f;
				force *= 1f + Player.wingTimeMax / 600f;
			}
			if (Player.rocketBoots > 0)
				fuel += 20f + Player.rocketTimeMax * 2f;
			foreach (ExtraJump jump in ExtraJumpLoader.OrderedJumps)
				if (Player.GetJumpState(jump).Enabled)
					fuel += 15f;
			// the body type (soldier, light, heavy, drone, turret, brain): its mass, legs, jetpack and toughness;
			// gear adds to its own jetpack tank
			ActorStats stats = ActorStats.Get(Player.GetModPlayer<Actors.RigPlayer>().Kind);
			Actor.FuelMax = fuel - CC.JetTimeTicks + stats.Fuel;
			Actor.JetForce = force / CC.JetForce * stats.JetForce;
			Actor.Apply(stats, Actors.ActorInventory.Mass(Player));
			Player.statLifeMax2 = (int)(Player.statLifeMax2 * stats.LifeScale);
			Actor.MaxHealth = Player.statLifeMax2;
			Actor.Health = Player.statLife;

			// vanilla mobility off: it's legs and a jetpack now
			Player.wingTimeMax = 0;
			Player.wingTime = 0;
			Player.rocketTimeMax = 0;
			Player.rocketTime = 0;
			Player.blockExtraJumps = true;
			Player.dashType = 0;
			Player.noFallDmg = true;
			Player.buffImmune[BuffID.Suffocation] = true; // rectangular tile overlap is not a body contact
			if (Player.mount.Active)
				Player.mount.Dismount(Player);

			if (Player.whoAmI == Main.myPlayer)
				ReadInput();
			// vanilla must not walk or jump the body itself (Down stays: it drops through platforms)
			Player.controlLeft = Player.controlRight = Player.controlJump = Player.controlUp = false;
		}

		void ReadInput()
		{
			if (Actors.ActorControlState.Blocked) {
				Input = new ActorInput { Aim = Input.Aim };
				Player.GetModPlayer<Orbital.PilotPlayer>().Capture(Input);
				prevJump = false;
				Player.controlDown = Player.controlUseItem = Player.controlUseTile = false;
				return;
			}
			bool jump = Player.controlJump || Player.controlUp;
			Input = new ActorInput {
				Left = Player.controlLeft,
				Right = Player.controlRight,
				Jump = jump,
				JumpStart = jump && !prevJump,
				Reloading = Player.GetModPlayer<Combat.AimPlayer>().Reloading,
				HoldingDevice = !Player.HeldItem.IsAir,
				Prone = Player.controlDown,
				Crouch = TerraCommandKeybinds.Crouch?.Current == true || Testing.AutoTest.Enabled && Testing.AutoTest.HoldCrouch,
				Fast = TerraCommandKeybinds.Run?.Current == true || Testing.AutoTest.Enabled && Testing.AutoTest.HoldRun,
				// aim from the shoulder, so the gun's line passes through the cursor; the pie menu holds the aim
				Aim = UI.ActorControlsUI.PieOpen ? Input.Aim : Main.MouseWorld - Actors.RigRenderer.Shoulder(Player.GetModPlayer<Actors.RigPlayer>().Pose()),
			};
			if (Testing.AutoTest.Enabled && Testing.AutoTest.AimOffset == null)
				Input.Aim = new Vector2(Actor.FlipFactor * 100f, 0f);
			// mouse distance is CC's analog aim magnitude: far from the body = sharp aim
			float reach = Math.Min(Main.screenWidth, Main.screenHeight) * 0.5f;
			Input.SharpAim = Input.Aim.Length() > reach * 0.1f;
			prevJump = jump;
			// flying a ship: the keys are the ship's, the body sits still inside it
			var pilot = Player.GetModPlayer<Orbital.PilotPlayer>();
			if (pilot.Ship != null) {
				pilot.Capture(Input);
				Input = new ActorInput { Aim = Input.Aim };
			}
		}

		// ------------------------------------------------------------ movement

		public override void PostUpdateRunSpeeds()
		{
			// the actor applies gravity and leg forces itself; vanilla's walk/fall rules are neutralised
			Player.gravity = 0f;
			Player.maxFallSpeed = 20f;
			Player.runAcceleration = 0f;
			Player.runSlowdown = 0f;
			Player.maxRunSpeed = Player.accRunSpeed = 20f;
		}

		public override void PreUpdateMovement()
		{
			contactMoved = false;
			Actor.ContactPhysics = UsesContacts;
			bool hooked = Player.grappling[0] >= 0 || Player.pulley;
			if (!Simulated || hooked) {
				velRequested = Player.velocity;
				return;
			}
			Actor.Vel = Player.velocity; // take in outside changes (hits, hooks, blasts)
			Actor.GroundSlope = Actor.Grounded ? Actors.BodyGround.Slope(Player.Bottom) : 0;
			Input.AutoCrouch = ActorContacts.CrouchForClearance(Actor, Player.Bottom, Input, TerrariaActorTerrain.Instance);
			Actor.Control(Input);
			Actor.Physics(Input);
			// balanced on the edge of the hitbox with the feet over nothing: slip off it
			if (!Actor.ContactPhysics && Actor.Grounded && Actor.Status == ActorStatus.Stable && !Actor.Jetting) {
				int slip = Actors.BodyGround.SlipDirection(Player.position, Player.width, Player.height);
				if (slip != 0 && Math.Sign(Actor.Vel.X) != -slip)
					Actor.Vel.X = slip * Math.Max(Math.Abs(Actor.Vel.X), 0.6f);
			}
			Player.velocity = Actor.Vel;
			Player.direction = Actor.FlipFactor;
			velRequested = Player.velocity;
			if (Actor.Jetting)
				JetEffects();
		}

		void JetEffects()
		{
			if (Main.dedServ)
				return;
			Vector2 thrust = Actor.JetDirection(Input);
			Vector2 nozzle = Player.GetModPlayer<Actors.RigPlayer>().NozzleWorld();
			for (int i = 0; i < 2; i++) {
				var d = Dust.NewDustDirect(nozzle - new Vector2(3), 6, 6, DustID.Torch, -thrust.X * 4f, -thrust.Y * 4f, 100, default, 1.4f);
				d.noGravity = true;
			}
			if (Main.rand.NextBool(2))
				Dust.NewDust(nozzle - new Vector2(3), 6, 6, DustID.Smoke, -thrust.X * 2f, -thrust.Y * 2f, 150, default, 1.1f);
			if (Input.JumpStart)
				SoundEngine.PlaySound(SoundID.Item13 with { Volume = 0.7f }, Player.Center);
			else if (Player.miscCounter % 9 == 0)
				SoundEngine.PlaySound(SoundID.Item24 with { Volume = 0.5f }, Player.Center);
		}

		// ------------------------------------------------------------ after collision

		public override void PostUpdate()
		{
			if (Simulated && contactMoved) {
				if (!skipImpact) Impact(Actor.Contacts.TravelImpulse.Length() / Actor.Mass);
				skipImpact = false;
				width = Actor.WantedWidth(); height = Actor.WantedHeight();
				ApplyHeight();
				return;
			}
			if (Simulated) {
				bool grounded = Player.velocity.Y == 0f && velRequested.Y >= 0f;
				// walking down rough ground: a few pixels' drop is a step, not a fall
				if (!grounded && Actor.Grounded && !Actor.Jetting && Actor.Status == ActorStatus.Stable
					&& Player.velocity.Y >= 0f && Player.velocity.Y < 1.5f && Player.grappling[0] < 0) {
					float drop = Actors.BodyGround.DropToGround(Player.position, Player.width, Player.height);
					if (drop >= 0f) {
						Player.position.Y += drop;
						Player.velocity.Y = 0f;
						velRequested.Y = 0f;
						grounded = true;
					}
				}
				// Terraria stops a body over a couple of ticks (first shortening the move to touch the surface, then
				// zeroing it), so speed lost over consecutive ticks is added up and judged as one impact.
				float loss = (velRequested - Player.velocity).Length();
				if (skipImpact || Player.grappling[0] >= 0) {
					impactAccum = 0f;
					impactTimer = 0;
				}
				else if (loss > 0.5f) {
					impactAccum += loss;
					impactTimer = 2;
				}
				else if (impactTimer > 0 && --impactTimer == 0) {
					Impact(impactAccum);
					impactAccum = 0f;
				}
				Actor.AfterMove(Player.velocity, grounded);
				skipImpact = false;
				UpdateHeight();
			}
			else {
				if (!Player.dead) Actor.AdvanceRemoteGait(Player.velocity);
				skipImpact = true;
			}
		}

		void Impact(float speedLost)
		{
			float damage = Actor.ImpactDamage(speedLost);
			Testing.AutoTest.TraceIf(Actor.LastImpulse > 1200f, $"impact {Actor.LastImpulse:0} Ns status {Actor.Status} damage {damage:0.00}");
			if (Actor.LastImpulse > CC.ImpulseDamageThreshold * 0.5f)
				SoundEngine.PlaySound(SoundID.NPCHit1 with { Volume = 0.6f, Pitch = -0.4f }, Player.Center);
			if (damage > 0f) {
				int dmg = Math.Max(1, (int)(damage * Player.statLifeMax2));
				Player.Hurt(PlayerDeathReason.ByCustomReason(NetworkText.FromLiteral($"{Player.name} hit the ground too hard.")),
					dmg, 0, dodgeable: false, knockback: 0f);
			}
		}

		/// <summary>The hitbox follows the posture (standing, crouched, lying); getting taller needs headroom.</summary>
		void UpdateHeight()
		{
			Actors.ActorBounds.Fit(Player.position, Player.width, Player.height, Actor, out width, out height);
			ApplyHeight();
		}

		/// <summary>Sets the hitbox height, keeping the feet where they are.</summary>
		public void ApplyHeight()
		{
			if (Player.mount.Active || Player.portableStoolInfo.IsInUse)
				return;
			if (Player.width != width) {
				Player.position.X += (Player.width - width) / 2f;
				Player.width = width;
			}
			if (Player.height != height) {
				Player.position.Y += Player.height - height;
				Player.height = height;
			}
		}

		// ------------------------------------------------------------ shoves

		/// <summary>Call after teleporting the body (no impact damage from the position/velocity jump).</summary>
		public void SkipImpactCheck() { skipImpact = true; Actor.Contacts.Reset(); }

		public byte SpawnedLimbs; public bool BodyLocked;
		public override void SaveData(TagCompound tag)
        {
            using var stream = new MemoryStream();
            Actor.Limbs.Write(new BinaryWriter(stream));
            tag["limbs"] = stream.ToArray();
            tag["bodyId"] = unchecked((long)Actor.BodyId);
            using var wounds = new MemoryStream();
            Actor.Wounds.Write(new BinaryWriter(wounds));
            tag["wounds"] = wounds.ToArray();
        }

        public override void LoadData(TagCompound tag)
        {
            var wounds = tag.GetByteArray("wounds");
            if (wounds.Length > 0) {
                using var stream = new MemoryStream(wounds);
                Actor.Wounds.CopyFrom(ActorWounds.Read(new BinaryReader(stream)));
            }
            if (tag.ContainsKey("bodyId")) Actor.BodyId = unchecked((ulong)tag.GetLong("bodyId"));
            var limbs = tag.GetByteArray("limbs");
            if (limbs.Length == 5) {
                using var stream = new MemoryStream(limbs);
                Actor.Limbs.CopyFrom(LimbState.Read(new BinaryReader(stream)));
                SpawnedLimbs = Actor.Limbs.Missing;
            }
        }

		public void AdoptBody(Actors.ActorMotion motion, Actors.ActorKind kind)
		{
			motion.Apply(Actor, kind); BodyLocked=true; SpawnedLimbs=Actor.Limbs.Missing;
			Player.velocity = Actor.Vel;
			Player.direction = Actor.FlipFactor;
			Actors.ActorBounds.Fit(Player.position, Player.width, Player.height, Actor, out width, out height);
			ApplyHeight();
			impactAccum = 0;
			impactTimer = 0;
			prevJump = false;
			skipImpact = true;
		}

		public void KnockDown(int dir)
		{
			if (Player.whoAmI == Main.myPlayer)
				Actor.Shove(new Vector2(dir * 0.5f, -0.5f), knockOver: true);
		}

		/// <summary>Shove this body (local player only; other players are moved by their own client).</summary>
		public void ApplyImpulse(Vector2 impulse)
		{
			Testing.AutoTest.Trace($"impulse {impulse} on {Player.name} (me={Player.whoAmI == Main.myPlayer})");
			if (Player.whoAmI != Main.myPlayer)
				return;
			Player.velocity += impulse;
			Actor.Shove(Vector2.Zero, knockOver: impulse.Length() > 2f);
			if (!Actor.ContactPhysics) skipImpact = true; // legacy collision loss included the shove itself
		}

		public override void OnRespawn()
		{
			Actor.Limbs.CopyFrom(new LimbState()); Actor.Wounds.Clear(); SpawnedLimbs=0;BodyLocked=false;Actor.BodyId=BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(),0);
			Actor.Status = ActorStatus.Stable;
			Actor.Prone = ProneState.NotProne;
			Actor.Rot = Actor.AngVel = 0f;
			Actor.Fuel = Actor.FuelMax;
			Actor.BurstCooldown = Actor.SharpAimTicks = 0;
			Actor.SharpAim = 0;
			width = CC.BodyWidth;
			height = Actor.StandHeight;
			skipImpact = true;
		}

		/// <summary>Explosions push through the blast physics (Explosions.Blast) instead of vanilla knockback.</summary>
		public override void ModifyHitByProjectile(Projectile proj, ref Player.HurtModifiers modifiers)
		{
			if (ProjectileID.Sets.Explosive[proj.type] || proj.aiStyle == ProjAIStyleID.Explosive)
				modifiers.Knockback *= 0f;
		}

		// ------------------------------------------------------------ multiplayer: other clients see the posture

		uint syncedWounds;
		ulong syncedLimbs,syncedBody;
		byte syncedStatus, syncedProne, syncedCrouch;
		sbyte syncedRot, syncedAim;
		byte syncedKind, syncedMove;
		bool syncedFlip, syncedContacts, syncedGrounded;

		public override void CopyClientState(ModPlayer targetCopy)
		{
			var t = (BodyPhysicsPlayer)targetCopy;
			t.syncedWounds=Actor.Wounds.Revision; t.syncedBody=Actor.BodyId;t.syncedLimbs=Actor.Limbs.Stamp;
			t.syncedStatus = (byte)Actor.Status;
			t.syncedProne = (byte)Actor.Prone;
			t.syncedCrouch = (byte)(Actor.CrouchAmount * 10);
			t.syncedRot = QuantRot(Actor.Rot);
			t.syncedFlip = Actor.Flipped;
			t.syncedAim = QuantRot(Actor.AimAngle);
			t.syncedMove = (byte)Actor.Move;
			t.syncedContacts = Actor.Contacts.Initialized;
			t.syncedGrounded = Actor.Grounded;
			t.syncedKind = (byte)Player.GetModPlayer<Actors.RigPlayer>().Kind;
		}

		static sbyte QuantRot(float r) => (sbyte)Math.Clamp((int)MathF.Round(MathHelper.WrapAngle(r) * 32f), -127, 127);

		public override void SendClientChanges(ModPlayer clientPlayer)
		{
			var c = (BodyPhysicsPlayer)clientPlayer;
			if (c.syncedWounds!=Actor.Wounds.Revision || c.syncedBody!=Actor.BodyId || c.syncedLimbs!=Actor.Limbs.Stamp || c.syncedStatus != (byte)Actor.Status || c.syncedProne != (byte)Actor.Prone || c.syncedCrouch != (byte)(Actor.CrouchAmount * 10)
				|| Math.Abs(c.syncedRot - QuantRot(Actor.Rot)) > 2 || c.syncedFlip != Actor.Flipped
				|| Math.Abs(c.syncedAim - QuantRot(Actor.AimAngle)) > 2 || c.syncedKind != (byte)Player.GetModPlayer<Actors.RigPlayer>().Kind
				|| c.syncedMove != (byte)Actor.Move || c.syncedContacts != Actor.Contacts.Initialized || c.syncedGrounded != Actor.Grounded
				|| Player.velocity.LengthSquared() > 0.01f && Main.GameUpdateCount % 15 == (ulong)(Player.whoAmI % 15))
				SyncPlayer(-1, Main.myPlayer, false);
		}

		public override void SyncPlayer(int toWho, int fromWho, bool newPlayer)
		{
			if (Main.netMode == NetmodeID.SinglePlayer)
				return;
			ModPacket p = PacketHandler.Create(PacketType.StanceSync);
			p.Write((byte)Player.whoAmI);
			p.Write((byte)Actor.Status);
			p.Write((byte)Actor.Prone);
			p.Write((byte)(Actor.CrouchAmount * 10));
			p.Write(QuantRot(Actor.Rot));
			p.Write(Actor.Flipped);
			p.Write((byte)height);
			p.Write(QuantRot(Actor.AimAngle));
			p.Write((byte)Player.GetModPlayer<Actors.RigPlayer>().Kind);
			Actor.WriteGait(p);
			ContactSnapshot.Capture(Actor.Contacts).Write(p); Actor.Limbs.Write(p);p.Write(Actor.BodyId); Actor.Wounds.Write(p);
			p.Send(toWho, fromWho);
		}

		public static void ReceiveStance(BinaryReader r, int from)
		{
			int who = r.ReadByte();
			var status = (ActorStatus)r.ReadByte();
			var prone = (ProneState)r.ReadByte();
			float crouch = r.ReadByte() / 10f;
			float rot = r.ReadSByte() / 32f;
			bool flip = r.ReadBoolean();
			int h = r.ReadByte();
			float aim = r.ReadSByte() / 32f;
			var kind = (Actors.ActorKind)r.ReadByte();
			GaitState gait = GaitState.Read(r);
			ContactSnapshot contacts = ContactSnapshot.Read(r);
			var limbs=LimbState.Read(r);ulong bodyId=r.ReadUInt64(); var wounds=ActorWounds.Read(r);
			if (Main.netMode == NetmodeID.Server)
				who = from;
			Player p = Main.player[who];
			if (who == Main.myPlayer || !p.active)
				return;
			var bp = p.GetModPlayer<BodyPhysicsPlayer>();
			if(Main.netMode==NetmodeID.Server && bp.BodyLocked && bp.Actor.BodyId!=bodyId)return;
			bp.Actor.BodyId=bodyId;
			bp.Actor.Status = status;
			bp.Actor.Prone = prone;
			bp.Actor.CrouchAmount = crouch;
			bp.Actor.Rot = rot;
			bp.Actor.Flipped = flip;
			bp.Actor.AimAngle = aim;
			bp.Actor.Limbs.CopyFrom(limbs);
			bp.Actor.Wounds.CopyFrom(wounds);
			bp.Actor.Apply(ActorStats.Get(kind), 0);
			gait.Apply(bp.Actor);
			contacts.Apply(bp.Actor.Contacts);
			bp.Actor.Contacts.BindPaths(bp.Actor);
			p.GetModPlayer<Actors.RigPlayer>().Kind = kind;
			bp.height = Math.Clamp(h, Math.Min(CC.LyingHeight,ActorStats.Get(kind).StandHeight), ActorStats.Get(kind).StandHeight);
			bp.width = bp.Actor.WantedWidth();
			bp.ApplyHeight();
			if (Main.netMode == NetmodeID.Server)
				bp.SyncPlayer(-1, who, false);
		}

		// ------------------------------------------------------------ vanilla hitbox hooks

		/// <summary>
		/// Vanilla resets the hitbox to 42 high every tick (ResizeHitbox) and puts Center 21px below the top;
		/// re-apply the posture height and keep Center in the middle of the shorter box.
		/// </summary>
		void MoveWithContacts()
		{
			if (!UsesContacts) { Player.position += Player.velocity; return; }
			Vector2 feet = ActorContacts.Step(Actor, Player.Bottom, Input, TerrariaActorTerrain.Instance);
			Player.Bottom = feet;
			Player.velocity = Actor.Vel;
			Player.fallStart = (int)(Player.position.Y / 16);
			Collision.up = Collision.down = false;
			contactMoved = true;
		}

		static bool SkipVanillaSteps => contactUpdateOwner != null && contactUpdateOwner.TryGetModPlayer(out BodyPhysicsPlayer bp) && bp.OwnsCollision;
		static void ContactStepUp(On_Collision.orig_StepUp orig, ref Vector2 position, ref Vector2 velocity, int width, int height,
			ref float stepSpeed, ref float gfxOffY, int gravDir, bool holdsMatching, int specialChecksMode)
		{
			if (!SkipVanillaSteps) orig(ref position, ref velocity, width, height, ref stepSpeed, ref gfxOffY, gravDir, holdsMatching, specialChecksMode);
		}
		static void ContactStepDown(On_Collision.orig_StepDown orig, ref Vector2 position, ref Vector2 velocity, int width, int height,
			ref float stepSpeed, ref float gfxOffY, int gravDir, bool waterWalk)
		{
			if (!SkipVanillaSteps) orig(ref position, ref velocity, width, height, ref stepSpeed, ref gfxOffY, gravDir, waterWalk);
		}
		public override void Load()
		{
			On_Player.Update += (orig, self, who) => {
				Player previous = contactUpdateOwner; contactUpdateOwner = self;
				try { orig(self, who); } finally { contactUpdateOwner = previous; }
			};
			On_Collision.StepUp += ContactStepUp;
			On_Collision.StepDown += ContactStepDown;
			On_Player.SlopeDownMovement += (orig, self) => {
				if (!self.TryGetModPlayer(out BodyPhysicsPlayer bp) || !bp.OwnsCollision) orig(self);
			};
			On_Player.DryCollision += (orig, self, fallThrough, ignorePlats) => {
				if (self.TryGetModPlayer(out BodyPhysicsPlayer bp) && bp.OwnsCollision) bp.MoveWithContacts();
				else orig(self, fallThrough, ignorePlats);
			};
			On_Player.WaterCollision += (orig, self, fallThrough, ignorePlats) => {
				if (self.TryGetModPlayer(out BodyPhysicsPlayer bp) && bp.OwnsCollision) bp.MoveWithContacts();
				else orig(self, fallThrough, ignorePlats);
			};
			On_Player.HoneyCollision += (orig, self, fallThrough, ignorePlats) => {
				if (self.TryGetModPlayer(out BodyPhysicsPlayer bp) && bp.OwnsCollision) bp.MoveWithContacts();
				else orig(self, fallThrough, ignorePlats);
			};
			On_Player.ShimmerCollision += (orig, self, fallThrough, ignorePlats, shimmering) => {
				if (self.TryGetModPlayer(out BodyPhysicsPlayer bp) && bp.OwnsCollision) bp.MoveWithContacts();
				else orig(self, fallThrough, ignorePlats, shimmering);
			};
			On_Player.SlopingCollision += (orig, self, fallThrough, ignorePlats) => {
				if (!self.TryGetModPlayer(out BodyPhysicsPlayer bp) || !bp.OwnsCollision) orig(self, fallThrough, ignorePlats);
			};
			On_Player.ResizeHitbox += (orig, self) => {
				orig(self);
				if (self.TryGetModPlayer(out BodyPhysicsPlayer bp) && (bp.height != Player.defaultHeight || self.width != bp.width))
					bp.ApplyHeight();
			};
			MonoModHooks.Add(typeof(Player).GetProperty(nameof(Player.HeightOffsetHitboxCenter)).GetGetMethod(),
				(Func<Func<Player, float>, Player, float>)((orig, self) =>
					self.mount.Active || self.portableStoolInfo.IsInUse ? orig(self) : orig(self) + (self.height - Player.defaultHeight) / 2f));
		}
	}

	/// <summary>Mounts don't exist in this world: bodies move on their own legs and jetpacks.</summary>
	public class NoMountsGlobalItem : GlobalItem
	{
		public override bool CanUseItem(Item item, Player player) => item.mountType < 0;
	}
}
