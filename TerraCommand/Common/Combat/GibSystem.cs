using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Net;

namespace TerraCommand.Common.Combat
{
	public enum DeathFx : byte { None, Gib, Decap, Corpse }

	/// <summary>
	/// Cortex Command style remains (cosmetic, every client):
	/// - a CC body that dies violently bursts into its actual rig parts (helmet, torso, limbs, boots, jetpack), which tumble,
	///   bounce and bleed;
	/// - a headshot kill knocks the head off and the body collapses; other deaths leave a corpse that topples and lies a while;
	/// - Terraria enemies burst into pieces cut from their own sprite (vanilla gore is suppressed when that happens);
	/// - blood droplets splat onto terrain and fall off when that ground is blown away.
	/// </summary>
	public class GibSystem : ModSystem
	{
		const int MaxChunks = 500, MaxDrops = 1500, MaxSplats = 2500, MaxCorpses = 40;
		const float ChunkGravity = 0.3f, DropGravity = 0.3f;

		class Chunk
		{
			public Texture2D Tex;
			public Rectangle Src;
			public SpriteEffects Fx;
			public float Scale = 1f;
			public Vector2 Pos, Vel;       // Pos = centre
			public float Rot, Spin;
			public int Life, MaxLife, Bleed;
			public bool Lit = true, Grounded, Splatted;
		}

		class Corpse
		{
			public RigPose Pose;
			public Vector2 Vel;
			public float Rot, Spin, Target;
			public int Life = 60 * 40;
		}

		struct Drop { public Vector2 Pos, Vel; public int Life; }

		struct Splat { public Vector2 Pos; public int Life; public byte Shape; public Color Color; }

		static readonly List<Chunk> chunks = new();
		static readonly List<Corpse> corpses = new();
		static readonly List<Drop> drops = new();
		static readonly List<Splat> splats = new();
		static readonly Dictionary<(int type, int frameY), Color[]> npcPixels = new();
		static readonly List<RigPiece> pieces = new();

		// test/diagnostic counters
		public static int ChunksSpawned, SplatsMade, Gibs;
		public static DeathFx LastFx;
		public static int ActiveChunks => chunks.Count;
		public static int ActiveSplats => splats.Count;
		public static int ActiveCorpses => corpses.Count;

		public override void Load() => On_NPC.HitEffect_HitInfo += SuppressVanillaGore;

		public override void Unload() => ClearAll();

		public override void OnWorldUnload() => ClearAll();

		static void ClearAll()
		{
			chunks.Clear();
			corpses.Clear();
			drops.Clear();
			splats.Clear();
			npcPixels.Clear();
		}

		/// <summary>
		/// How a death looks: a blast gibs; a headshot kill takes the head off (CC: the head breaks off first); a massive
		/// hit gibs; anything else leaves a corpse.
		/// </summary>
		public static DeathFx Decide(bool blasted, bool overkill, bool headshot, bool hasHead) =>
			blasted ? DeathFx.Gib : headshot && hasHead ? DeathFx.Decap : overkill ? DeathFx.Gib : DeathFx.Corpse;

		// ------------------------------------------------------------ vanilla gore suppression

		static bool suppressGore;
		static readonly bool[] goreWasActive = new bool[Main.maxGore + 1];

		static void SuppressVanillaGore(On_NPC.orig_HitEffect_HitInfo orig, NPC self, NPC.HitInfo hit)
		{
			if (Main.dedServ || self.life > 0) {
				orig(self, hit);
				return;
			}
			for (int i = 0; i < Main.gore.Length && i < goreWasActive.Length; i++)
				goreWasActive[i] = Main.gore[i].active;
			suppressGore = false;
			orig(self, hit); // vanilla gore, then the GlobalNPC hooks (which may claim the death)
			if (suppressGore) {
				for (int i = 0; i < Main.gore.Length && i < goreWasActive.Length; i++)
					if (Main.gore[i].active && !goreWasActive[i])
						Main.gore[i].active = false;
			}
			suppressGore = false;
		}

		// ------------------------------------------------------------ CC bodies: parts and corpses

		/// <summary>A CC body died (player or idle body). Runs locally; <see cref="Broadcast"/> shares it in multiplayer.</summary>
		public static void ActorDeath(RigPose pose, DeathFx kind, Vector2 push, Vector2? blastCenter)
		{
			LastFx = kind;
			if (kind == DeathFx.Gib)
				Gibs++;
			Testing.AutoTest.Trace($"death fx {kind}");
			if (Main.dedServ || kind == DeathFx.None)
				return;
			pose.Held = null;           // the weapon drops as a real item
			pose.Unstable = true;
			pose.Light = Lighting.GetColor(pose.Feet.ToTileCoordinates() - new Point(0, 1));
			Vector2 bodyCenter = pose.Feet - new Vector2(0f, 20f);

			if (kind == DeathFx.Gib) {
				pieces.Clear();
				RigRenderer.Build(pose, pieces);
				foreach (RigPiece r in pieces) {
					Chunk c = FromPiece(r);
					Vector2 away = (blastCenter is Vector2 bc ? c.Pos - bc : c.Pos - bodyCenter).SafeNormalize(-Vector2.UnitY);
					float power = blastCenter.HasValue ? Main.rand.NextFloat(3f, 8f) : Main.rand.NextFloat(1.5f, 4f);
					c.Vel = push * 0.5f + away * power + new Vector2(0f, -Main.rand.NextFloat(1f, 3f));
					c.Spin = Main.rand.NextFloat(-0.4f, 0.4f);
					c.Life = c.MaxLife = 60 * Main.rand.Next(16, 26);
					c.Bleed = Main.rand.Next(20, 70);
					AddChunk(c);
				}
				Blood(bodyCenter, push, 70, 7f);
				SoundEngine.PlaySound(SoundID.NPCDeath1 with { Pitch = -0.3f }, bodyCenter);
				return;
			}

			var corpse = new Corpse { Pose = pose, Vel = push * 0.4f, Rot = pose.Rot };
			int fall = Math.Abs(push.X) > 0.5f ? Math.Sign(push.X) : pose.Dir;   // topples the way it was hit, else forward
			corpse.Target = fall * MathHelper.PiOver2;
			if (kind == DeathFx.Decap) {
				corpse.Pose.Headless = true;
				// the head comes off and flies, spraying from the neck
				pieces.Clear();
				RigRenderer.Build(pose, pieces, onlyHead: true);
				foreach (RigPiece r in pieces) {
					Chunk h = FromPiece(r);
					h.Vel = push * 1.1f + new Vector2(Main.rand.NextFloat(-1.5f, 1.5f), -Main.rand.NextFloat(3.5f, 5.5f));
					h.Spin = Main.rand.NextFloat(0.15f, 0.4f) * (push.X >= 0 ? 1 : -1);
					h.Life = h.MaxLife = 60 * 25;
					h.Bleed = 60;
					AddChunk(h);
				}
				Blood(pose.Feet - new Vector2(0f, 30f), new Vector2(push.X * 0.3f, -2.5f), 30, 4f);
			}
			if (corpses.Count >= MaxCorpses)
				corpses.RemoveAt(0);
			corpses.Add(corpse);
		}

		static Chunk FromPiece(RigPiece r)
		{
			Rectangle src = r.Src ?? r.Tex.Bounds;
			// the sprite's centre in the world: from its origin point, through the rotation (origins are in drawn space)
			Vector2 center = r.Pos + ((src.Size() / 2f - r.Origin) * r.Scale).RotatedBy(r.Rot);
			return new Chunk { Tex = r.Tex, Src = src, Fx = r.Fx, Scale = r.Scale, Pos = center, Rot = r.Rot };
		}

		static void Blood(Vector2 at, Vector2 push, int n, float speed)
		{
			for (int i = 0; i < n; i++) {
				Vector2 dir = Main.rand.NextVector2Unit();
				AddDrop(at + dir * Main.rand.NextFloat(0, 8f), push * 0.5f + dir * Main.rand.NextFloat(1f, speed) + new Vector2(0, -2f));
			}
		}

		/// <summary>Multiplayer: show a body's death on the other clients (a control transfer isn't a vanilla death).</summary>
		public static void Broadcast(RigPose pose, DeathFx kind, Vector2 push, Vector2? blastCenter, int ignoreClient = -1)
		{
			if (Main.netMode == NetmodeID.SinglePlayer)
				return;
			ModPacket p = PacketHandler.Create(PacketType.DeathFx);
			p.Write((byte)kind);
			p.Write((byte)pose.Kind);
			p.WriteVector2(pose.Feet);
			p.Write((sbyte)pose.Dir);
			p.Write(pose.Rot);
			p.WriteVector2(push);
			p.Write(blastCenter.HasValue);
			p.WriteVector2(blastCenter ?? Vector2.Zero);
			p.Send(-1, ignoreClient);
		}

		public static void ReceiveDeathFx(BinaryReader r, int from)
		{
			var kind = (DeathFx)r.ReadByte();
			var pose = new RigPose { Kind = (ActorKind)r.ReadByte(), Feet = r.ReadVector2(), Dir = r.ReadSByte(), Rot = r.ReadSingle() };
			Vector2 push = r.ReadVector2();
			bool hasBlast = r.ReadBoolean();
			Vector2 bc = r.ReadVector2();
			if (Main.netMode == NetmodeID.Server) {
				Broadcast(pose, kind, push, hasBlast ? bc : null, ignoreClient: from);
				return;
			}
			ActorDeath(pose, kind, push, hasBlast ? bc : null);
		}

		// ------------------------------------------------------------ Terraria enemies: cut from their sprite

		public static void NpcDeath(NPC npc, DeathFx kind, Vector2 push, Vector2? blastCenter)
		{
			LastFx = kind;
			if (kind == DeathFx.Gib)
				Gibs++;
			if (Main.dedServ || kind is DeathFx.None or DeathFx.Corpse)
				return;
			suppressGore = true;
			Main.instance.LoadNPC(npc.type);
			Texture2D tex = TextureAssets.Npc[npc.type].Value;
			Rectangle frame = npc.frame;
			if (frame.Width <= 0 || frame.Height <= 0)
				frame = new Rectangle(0, 0, tex.Width, tex.Height / Math.Max(1, Main.npcFrameCount[npc.type]));
			frame = Rectangle.Intersect(frame, tex.Bounds);
			if (frame.Width <= 0 || frame.Height <= 0)
				return;
			Color[] px = NpcPixels(npc.type, tex, frame);
			var fx = npc.spriteDirection == 1 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
			Vector2 topLeft = npc.Bottom + new Vector2(-frame.Width * npc.scale / 2f, -frame.Height * npc.scale + 4f + npc.gfxOffY);
			Cut(tex, frame, px, fx, npc.scale, topLeft, kind, push, blastCenter, DamageRules.HasHead(npc));
		}

		static Color[] NpcPixels(int type, Texture2D tex, Rectangle frame)
		{
			if (npcPixels.TryGetValue((type, frame.Y), out Color[] px) && px.Length == frame.Width * frame.Height)
				return px;
			px = new Color[frame.Width * frame.Height];
			try {
				tex.GetData(0, frame, px, 0, px.Length);
			}
			catch {
				Array.Fill(px, Color.White); // unreadable: treat every cell as solid
			}
			if (npcPixels.Count > 200)
				npcPixels.Clear();
			npcPixels[(type, frame.Y)] = px;
			return px;
		}

		/// <param name="px">pixels of <paramref name="frame"/> (row-major, frame-local)</param>
		/// <param name="topLeft">world position of the frame's top-left corner as drawn</param>
		static void Cut(Texture2D tex, Rectangle frame, Color[] px, SpriteEffects fx, float scale, Vector2 topLeft,
			DeathFx kind, Vector2 push, Vector2? blastCenter, bool hasHead)
		{
			Rectangle content = Opaque(px, frame.Width, new Rectangle(0, 0, frame.Width, frame.Height));
			if (content.Width <= 0)
				return;
			Vector2 bodyCenter = topLeft + content.Center.ToVector2() * scale;
			bool flip = fx.HasFlag(SpriteEffects.FlipHorizontally);

			Chunk Make(Rectangle local)
			{
				float cx = flip ? frame.Width - (local.X + local.Width / 2f) : local.X + local.Width / 2f;
				return new Chunk {
					Tex = tex, Fx = fx, Scale = scale,
					Src = new Rectangle(frame.X + local.X, frame.Y + local.Y, local.Width, local.Height),
					Pos = topLeft + new Vector2(cx, local.Y + local.Height / 2f) * scale,
				};
			}

			if (kind == DeathFx.Decap && hasHead) {
				int neck = content.Top + (int)(content.Height * DamageRules.HeadZone * 1.1f);
				Rectangle headR = Opaque(px, frame.Width, new Rectangle(content.Left, content.Top, content.Width, neck - content.Top));
				Rectangle bodyR = Opaque(px, frame.Width, new Rectangle(content.Left, neck, content.Width, content.Bottom - neck));
				if (headR.Width > 0) {
					Chunk h = Make(headR);
					h.Vel = push * 1.2f + new Vector2(Main.rand.NextFloat(-1.5f, 1.5f), -Main.rand.NextFloat(4f, 6.5f));
					h.Spin = Main.rand.NextFloat(0.15f, 0.4f) * (push.X >= 0 ? 1 : -1);
					h.Life = h.MaxLife = 60 * 25;
					h.Bleed = 60;
					AddChunk(h);
				}
				if (bodyR.Width > 0) {
					Chunk b = Make(bodyR);
					b.Vel = push * 0.4f;
					b.Spin = 0.05f * (push.X >= 0 ? 1 : -1);
					b.Life = b.MaxLife = 60 * 30;
					b.Bleed = 150;
					AddChunk(b);
				}
				Blood(topLeft + new Vector2(frame.Width / 2f, neck) * scale, new Vector2(push.X * 0.3f, -2.5f), 25, 4f);
				return;
			}

			// a jittered grid over the opaque area; ~10px pieces, at most ~40
			int cell = Math.Max(8, (int)Math.Sqrt(content.Width * content.Height / 40f) + 1);
			var xs = Splits(content.Left, content.Right, cell);
			var ys = Splits(content.Top, content.Bottom, cell);
			for (int i = 0; i + 1 < xs.Count; i++) {
				for (int j = 0; j + 1 < ys.Count; j++) {
					Rectangle r = Opaque(px, frame.Width, new Rectangle(xs[i], ys[j], xs[i + 1] - xs[i], ys[j + 1] - ys[j]), minPixels: 6);
					if (r.Width <= 0)
						continue;
					Chunk c = Make(r);
					Vector2 away = (blastCenter is Vector2 bc ? c.Pos - bc : c.Pos - bodyCenter).SafeNormalize(-Vector2.UnitY);
					float power = blastCenter.HasValue ? Main.rand.NextFloat(4f, 9f) : Main.rand.NextFloat(1.5f, 4.5f);
					c.Vel = push * 0.6f + away * power + new Vector2(0, -Main.rand.NextFloat(1f, 4f));
					c.Spin = Main.rand.NextFloat(-0.35f, 0.35f);
					c.Life = c.MaxLife = 60 * Main.rand.Next(14, 22);
					c.Bleed = Main.rand.Next(20, 70);
					AddChunk(c);
				}
			}
			Blood(bodyCenter, push, 70, 7f);
			SoundEngine.PlaySound(SoundID.NPCDeath1 with { Pitch = -0.3f }, bodyCenter);
		}

		static List<int> Splits(int a, int b, int cell)
		{
			var list = new List<int> { a };
			int n = Math.Max(1, (int)Math.Round((b - a) / (float)cell));
			for (int i = 1; i < n; i++) {
				int at = a + (b - a) * i / n + Main.rand.Next(-cell / 4, cell / 4 + 1);
				if (at > list[^1] + 2 && at < b - 2)
					list.Add(at);
			}
			list.Add(b);
			return list;
		}

		static Rectangle Opaque(Color[] px, int stride, Rectangle area, int minPixels = 1)
		{
			int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
			int rows = px.Length / stride;
			for (int y = Math.Max(0, area.Top); y < Math.Min(rows, area.Bottom); y++) {
				for (int x = Math.Max(0, area.Left); x < Math.Min(stride, area.Right); x++) {
					if (px[y * stride + x].A < 40)
						continue;
					count++;
					if (x < x0) x0 = x;
					if (x > x1) x1 = x;
					if (y < y0) y0 = y;
					if (y > y1) y1 = y;
				}
			}
			return count < minPixels ? Rectangle.Empty : new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
		}

		static void AddChunk(Chunk c)
		{
			if (chunks.Count >= MaxChunks)
				chunks.RemoveAt(0);
			chunks.Add(c);
			ChunksSpawned++;
		}

		/// <summary>A few drops of blood flying from a wound.</summary>
		public static void Spray(Vector2 at, Vector2 dir, int count, float speed = 3f)
		{
			if (Main.dedServ)
				return;
			for (int i = 0; i < count; i++)
				AddDrop(at, dir * speed * Main.rand.NextFloat(0.4f, 1.3f) + Main.rand.NextVector2Circular(1.5f, 1.5f) + new Vector2(0, -1f));
		}

		static void AddDrop(Vector2 at, Vector2 vel)
		{
			if (drops.Count >= MaxDrops)
				drops.RemoveAt(0);
			drops.Add(new Drop { Pos = at, Vel = vel, Life = 150 });
		}

		// ------------------------------------------------------------ simulation

		public override void PostUpdateDusts()
		{
			if (Main.dedServ)
				return;
			for (int i = chunks.Count - 1; i >= 0; i--) {
				Chunk c = chunks[i];
				if (--c.Life <= 0) {
					chunks.RemoveAt(i);
					continue;
				}
				StepChunk(c);
			}
			for (int i = corpses.Count - 1; i >= 0; i--) {
				if (--corpses[i].Life <= 0)
					corpses.RemoveAt(i);
				else
					StepCorpse(corpses[i]);
			}
			for (int i = drops.Count - 1; i >= 0; i--) {
				Drop d = drops[i];
				d.Vel.Y = Math.Min(d.Vel.Y + DropGravity, 12f);
				d.Vel.X *= 0.99f;
				Vector2 next = d.Pos + d.Vel;
				if (Collision.SolidCollision(next - Vector2.One, 2, 2)) {
					AddSplat(d.Pos);
					drops.RemoveAt(i);
					continue;
				}
				d.Pos = next;
				if (--d.Life <= 0 || Collision.WetCollision(d.Pos, 1, 1)) {
					drops.RemoveAt(i);
					continue;
				}
				drops[i] = d;
			}
			if (Main.GameUpdateCount % 30 == 0) {
				// splats on terrain that has since been blown away fall off; old ones fade
				for (int i = splats.Count - 1; i >= 0; i--) {
					Splat s = splats[i];
					s.Life -= 30;
					if (s.Life <= 0 || !Collision.SolidCollision(s.Pos - new Vector2(3), 6, 6))
						splats.RemoveAt(i);
					else
						splats[i] = s;
				}
			}
		}

		static void StepChunk(Chunk c)
		{
			c.Vel.Y = Math.Min(c.Vel.Y + ChunkGravity, 14f);
			int size = Math.Clamp((int)(Math.Min(c.Src.Width, c.Src.Height) * c.Scale * 0.8f), 3, 14);
			Vector2 tl = c.Pos - new Vector2(size / 2f);
			if (Collision.SolidCollision(tl, size, size)) {
				c.Pos.Y -= 1f;   // spawned inside terrain: drift up out of it
				c.Vel *= 0.5f;
				return;
			}
			Vector2 v = Collision.TileCollision(tl, c.Vel, size, size, true, true);
			bool hitX = v.X != c.Vel.X, hitY = v.Y != c.Vel.Y;
			float speed = c.Vel.Length();
			c.Pos += v;
			if (hitX) {
				c.Vel.X = -c.Vel.X * 0.3f;
				c.Spin *= -0.5f;
			}
			c.Grounded = false;
			if (hitY) {
				c.Grounded = c.Vel.Y > 0;
				c.Vel.Y = Math.Abs(c.Vel.Y) > 3f ? -c.Vel.Y * 0.3f : 0f;
				c.Vel.X *= c.Grounded ? 0.8f : 0.95f;
				c.Spin *= 0.6f;
			}
			if ((hitX || hitY) && speed > 3f && !c.Splatted) {
				c.Splatted = true;
				Spray(c.Pos, -c.Vel.SafeNormalize(Vector2.Zero) * 0.3f, 4, 1.5f);
			}
			if (c.Grounded && Math.Abs(c.Vel.X) < 0.3f) {
				c.Vel.X = 0f;
				// come to rest: long pieces lie on their side, others the nearest way up
				bool tall = c.Src.Height > c.Src.Width * 1.2f;
				float target = tall ? MathF.Round((c.Rot - MathHelper.PiOver2) / MathHelper.Pi) * MathHelper.Pi + MathHelper.PiOver2
					: MathF.Round(c.Rot / MathHelper.PiOver2) * MathHelper.PiOver2;
				c.Rot = MathHelper.Lerp(c.Rot, target, 0.15f);
				c.Spin = 0f;
			}
			else
				c.Rot += c.Spin;
			if (c.Bleed > 0) {
				c.Bleed--;
				if (Main.rand.NextBool(3))
					AddDrop(c.Pos, c.Vel * 0.3f + Main.rand.NextVector2Circular(0.8f, 0.8f));
			}
		}

		/// <summary>A dead body falls with CC gravity and topples over (limp), then lies until it fades.</summary>
		static void StepCorpse(Corpse k)
		{
			k.Vel.Y = Math.Min(k.Vel.Y + Actors.CC.Gravity, 14f);
			const int w = 20, h = 20;
			Vector2 tl = k.Pose.Feet - new Vector2(w / 2f, h);
			Vector2 v = Collision.TileCollision(tl, k.Vel, w, h, true, true);
			bool grounded = v.Y != k.Vel.Y && k.Vel.Y > 0;
			k.Pose.Feet += v;
			k.Vel = v;
			if (grounded) {
				k.Vel.X *= 0.85f;
				k.Vel.Y = 0f;
			}
			k.Pose.Grounded = grounded;
			// topple toward lying flat; a little bounce when it gets there
			float diff = k.Target - k.Rot;
			k.Spin += diff * 0.02f;
			k.Spin *= 0.9f;
			k.Rot += k.Spin;
			k.Pose.Rot = k.Rot;
			k.Pose.VelX = 0f;
		}

		static void AddSplat(Vector2 at)
		{
			if (splats.Count >= MaxSplats)
				splats.RemoveAt(0);
			splats.Add(new Splat {
				Pos = at, Life = 60 * 90, Shape = (byte)Main.rand.Next(256),
				Color = Color.Lerp(new Color(90, 6, 6), new Color(150, 16, 16), Main.rand.NextFloat()),
			});
			SplatsMade++;
		}

		// ------------------------------------------------------------ drawing

		public override void PostDrawTiles()
		{
			if (chunks.Count == 0 && drops.Count == 0 && splats.Count == 0 && corpses.Count == 0)
				return;
			SpriteBatch sb = Main.spriteBatch;
			sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None,
				Main.Rasterizer, null, Main.GameViewMatrix.TransformationMatrix);
			Texture2D px = TextureAssets.MagicPixel.Value;
			Rectangle one = new(0, 0, 1, 1);
			Rectangle view = new((int)Main.screenPosition.X - 64, (int)Main.screenPosition.Y - 64, Main.screenWidth + 128, Main.screenHeight + 128);

			foreach (Splat s in splats) {
				if (!view.Contains(s.Pos.ToPoint()))
					continue;
				float fade = Math.Min(1f, s.Life / 300f);
				Color c = s.Color.MultiplyRGB(Lighting.GetColor(s.Pos.ToTileCoordinates())) * fade;
				Vector2 at = (s.Pos - Main.screenPosition).Floor();
				sb.Draw(px, at - new Vector2(1), one, c, 0f, Vector2.Zero, new Vector2(3, 2), SpriteEffects.None, 0f);
				if ((s.Shape & 1) != 0) sb.Draw(px, at + new Vector2(-3, 0), one, c, 0f, Vector2.Zero, new Vector2(2, 2), SpriteEffects.None, 0f);
				if ((s.Shape & 2) != 0) sb.Draw(px, at + new Vector2(2, -1), one, c, 0f, Vector2.Zero, new Vector2(2, 2), SpriteEffects.None, 0f);
				if ((s.Shape & 4) != 0) sb.Draw(px, at + new Vector2(0, 1), one, c, 0f, Vector2.Zero, new Vector2(1, 2), SpriteEffects.None, 0f);
			}
			foreach (Corpse k in corpses) {
				if (!view.Contains(k.Pose.Feet.ToPoint()))
					continue;
				float fade = Math.Min(1f, k.Life / 120f);
				k.Pose.Light = Lighting.GetColor((k.Pose.Feet - new Vector2(0, 8)).ToTileCoordinates()) * fade;
				pieces.Clear();
				RigRenderer.Build(k.Pose, pieces);
				RigRenderer.Draw(sb, pieces);
			}
			foreach (Drop d in drops) {
				if (!view.Contains(d.Pos.ToPoint()))
					continue;
				Color c = new Color(140, 12, 12).MultiplyRGB(Lighting.GetColor(d.Pos.ToTileCoordinates()));
				sb.Draw(px, (d.Pos - Main.screenPosition).Floor(), one, c, 0f, Vector2.Zero, new Vector2(2, 2), SpriteEffects.None, 0f);
			}
			foreach (Chunk ch in chunks) {
				if (!view.Contains(ch.Pos.ToPoint()) || ch.Tex == null || ch.Tex.IsDisposed)
					continue;
				float fade = Math.Min(1f, ch.Life / 120f);
				Color light = Lighting.GetColor(ch.Pos.ToTileCoordinates());
				sb.Draw(ch.Tex, ch.Pos - Main.screenPosition, ch.Src, light * fade, ch.Rot, ch.Src.Size() / 2f, ch.Scale, ch.Fx, 0f);
			}
			sb.End();
		}
	}
}
