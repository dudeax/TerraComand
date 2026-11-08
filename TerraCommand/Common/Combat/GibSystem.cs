using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Combat
{
	public enum DeathFx { None, Gib, Decap, Corpse }

	/// <summary>
	/// Cortex Command style remains (cosmetic, client-side): bodies burst into pieces cut from their own sprite,
	/// heads come off on headshot kills, players and bodies leave corpses, and blood droplets splat onto terrain.
	/// NPCs are cut straight from their texture frame; players and clone bodies (drawn from many layers) are first
	/// rendered into a small snapshot texture.
	/// </summary>
	public class GibSystem : ModSystem
	{
		const int MaxChunks = 500, MaxDrops = 1500, MaxSplats = 2500;
		const float ChunkGravity = 0.35f, DropGravity = 0.3f;

		class Snapshot
		{
			public RenderTarget2D Target;
			public int Refs;
		}

		class Chunk
		{
			public Texture2D Tex;
			public Snapshot Snap;          // owner of Tex when it's a snapshot (disposed when the last chunk goes)
			public Rectangle Src;
			public SpriteEffects Fx;
			public float Scale = 1f;
			public Vector2 Pos, Vel;       // Pos = centre
			public float Rot, Spin;
			public int Life, MaxLife, Bleed;
			public bool Corpse, Grounded, Splatted;
		}

		struct Drop { public Vector2 Pos, Vel; public int Life; }

		struct Splat { public Vector2 Pos; public int Life; public byte Shape; public Color Color; }

		/// <summary>A body waiting to be drawn into a snapshot before it can be cut up (players / clone bodies).</summary>
		class Pending
		{
			public Player Body;
			public DeathFx Kind;
			public Vector2 Push;
			public Vector2? BlastCenter;
		}

		static readonly List<Chunk> chunks = new();
		static readonly List<Drop> drops = new();
		static readonly List<Splat> splats = new();
		static readonly List<Pending> pending = new();
		static readonly Dictionary<(int type, int frameY), Color[]> npcPixels = new();

		// test/diagnostic counters
		public static int ChunksSpawned, SplatsMade;
		public static DeathFx LastFx;
		public static int ActiveChunks => chunks.Count;
		public static int ActiveSplats => splats.Count;

		public override void Load()
		{
			if (!Main.dedServ)
				Main.OnPreDraw += RenderSnapshots;
			On_NPC.HitEffect_HitInfo += SuppressVanillaGore;
		}

		public override void Unload()
		{
			Main.OnPreDraw -= RenderSnapshots;
			ClearAll();
		}

		public override void OnWorldUnload() => ClearAll();

		static void ClearAll()
		{
			foreach (Chunk c in chunks)
				Release(c);
			chunks.Clear();
			drops.Clear();
			splats.Clear();
			pending.Clear();
			npcPixels.Clear();
		}

		// ------------------------------------------------------------ vanilla gore suppression

		/// <summary>Set while an NPC's death is being turned into our own remains, so vanilla's gore isn't doubled up.</summary>
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

		// ------------------------------------------------------------ entry points

		public static void NpcDeath(NPC npc, DeathFx kind, Vector2 push, Vector2? blastCenter)
		{
			LastFx = kind;
			if (Main.dedServ || kind == DeathFx.None)
				return;
			suppressGore = true;
			if (npc.ModNPC is CloneUnit unit) {
				pending.Add(new Pending { Body = unit.VisualBody(), Kind = kind, Push = push, BlastCenter = blastCenter });
				return;
			}
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
			// where vanilla draws the frame: centred on the hitbox, bottom-aligned (+4)
			Vector2 topLeft = npc.Bottom + new Vector2(-frame.Width * npc.scale / 2f, -frame.Height * npc.scale + 4f + npc.gfxOffY);
			Cut(tex, null, frame, px, fx, npc.scale, topLeft, kind, push, blastCenter, DamageRules.HasHead(npc));
		}

		public static void PlayerDeath(Player player, DeathFx kind, Vector2 push, Vector2? blastCenter)
		{
			LastFx = kind;
			if (Main.dedServ || kind == DeathFx.None)
				return;
			var body = (Player)player.Clone(); // drawn as it was in its last moment
			body.dead = false;
			body.immune = false;
			body.immuneAlpha = 0;
			body.fullRotation = 0f;
			pending.Add(new Pending { Body = body, Kind = kind, Push = push, BlastCenter = blastCenter });
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

		// ------------------------------------------------------------ snapshots (players / clone bodies)

		const int SnapW = 52, SnapH = 68, SnapPad = 6;

		/// <summary>Renders queued bodies into small textures. Runs before the frame is drawn (no batch is open).</summary>
		static void RenderSnapshots(GameTime _)
		{
			if (pending.Count == 0 || Main.gameMenu)
				return;
			GraphicsDevice gd = Main.instance.GraphicsDevice;
			RenderTargetBinding[] old = gd.GetRenderTargets();
			foreach (Pending p in pending) {
				try {
					var rt = new RenderTarget2D(gd, SnapW, SnapH, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
					gd.SetRenderTarget(rt);
					gd.Clear(Color.Transparent);
					Player b = p.Body;
					// the 40x56 sprite frame is drawn at position + (width/2 - 20, height - 56 + 4); put it at (SnapPad, SnapPad)
					Vector2 frameOffset = new(b.width / 2f - 20f, b.height - 52f);
					Vector2 drawPos = Main.screenPosition - frameOffset + new Vector2(SnapPad);
					Vector2 savedPos = b.position;
					Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone, null, Matrix.Identity);
					Main.PlayerRenderer.DrawPlayer(Main.Camera, b, drawPos, 0f, Vector2.Zero);
					Main.spriteBatch.End();
					b.position = savedPos;
					gd.SetRenderTargets(old);

					var px = new Color[SnapW * SnapH];
					rt.GetData(px);
					var snap = new Snapshot { Target = rt };
					Vector2 topLeft = b.position + frameOffset - new Vector2(SnapPad);
					Cut(rt, snap, new Rectangle(0, 0, SnapW, SnapH), px, SpriteEffects.None, 1f, topLeft, p.Kind, p.Push, p.BlastCenter, true);
					if (snap.Refs == 0)
						rt.Dispose();
				}
				catch (Exception e) {
					gd.SetRenderTargets(old);
					ModContent.GetInstance<TerraCommand>().Logger.Warn("gib snapshot failed: " + e.Message);
				}
			}
			pending.Clear();
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

		// ------------------------------------------------------------ cutting a sprite into remains

		/// <param name="px">pixels of <paramref name="frame"/> (row-major, frame-local)</param>
		/// <param name="topLeft">world position of the frame's top-left corner as drawn</param>
		static void Cut(Texture2D tex, Snapshot snap, Rectangle frame, Color[] px, SpriteEffects fx, float scale, Vector2 topLeft,
			DeathFx kind, Vector2 push, Vector2? blastCenter, bool hasHead)
		{
			Rectangle content = Opaque(px, frame.Width, new Rectangle(0, 0, frame.Width, frame.Height));
			if (content.Width <= 0)
				return;
			Vector2 bodyCenter = topLeft + content.Center.ToVector2() * scale;
			bool flip = fx.HasFlag(SpriteEffects.FlipHorizontally);

			// frame-local rectangle -> chunk at its world position
			Chunk Make(Rectangle local)
			{
				// flipped sprites mirror the source horizontally inside the frame
				float cx = flip ? frame.Width - (local.X + local.Width / 2f) : local.X + local.Width / 2f;
				var c = new Chunk {
					Tex = tex, Snap = snap, Fx = fx, Scale = scale,
					Src = new Rectangle(frame.X + local.X, frame.Y + local.Y, local.Width, local.Height),
					Pos = topLeft + new Vector2(cx, local.Y + local.Height / 2f) * scale,
				};
				if (snap != null)
					snap.Refs++;
				return c;
			}

			if (kind == DeathFx.Gib || kind == DeathFx.Decap && !hasHead) {
				// a jittered grid over the opaque area; ~10px pieces, at most ~40
				int cell = Math.Max(8, (int)Math.Sqrt(content.Width * content.Height / 40f) + 1);
				cell = Math.Max(cell, 8);
				var xs = Splits(content.Left, content.Right, cell);
				var ys = Splits(content.Top, content.Bottom, cell);
				for (int i = 0; i + 1 < xs.Count; i++) {
					for (int j = 0; j + 1 < ys.Count; j++) {
						Rectangle r = Opaque(px, frame.Width, new Rectangle(xs[i], ys[j], xs[i + 1] - xs[i], ys[j + 1] - ys[j]), minPixels: 6);
						if (r.Width <= 0)
							continue;
						Chunk c = Make(r);
						Vector2 away = blastCenter is Vector2 bc ? c.Pos - bc : c.Pos - bodyCenter;
						away = away.SafeNormalize(-Vector2.UnitY);
						float power = blastCenter.HasValue ? Main.rand.NextFloat(4f, 9f) : Main.rand.NextFloat(1.5f, 4.5f);
						c.Vel = push * 0.6f + away * power + new Vector2(0, -Main.rand.NextFloat(1f, 4f));
						c.Spin = Main.rand.NextFloat(-0.35f, 0.35f);
						c.Life = c.MaxLife = 60 * Main.rand.Next(14, 22);
						c.Bleed = Main.rand.Next(20, 70);
						Add(c);
					}
				}
				for (int i = 0; i < 70; i++) {
					Vector2 dir = Main.rand.NextVector2Unit();
					AddDrop(bodyCenter + dir * Main.rand.NextFloat(0, 8f), push * 0.5f + dir * Main.rand.NextFloat(1f, 7f) + new Vector2(0, -2.5f));
				}
				for (int i = 0; i < 25; i++)
					Dust.NewDust(topLeft + content.TopLeft() * scale, (int)(content.Width * scale), (int)(content.Height * scale), DustID.Blood,
						push.X + Main.rand.NextFloat(-4f, 4f), push.Y + Main.rand.NextFloat(-6f, 1f), 0, default, Main.rand.NextFloat(1.1f, 1.8f));
				return;
			}

			if (kind == DeathFx.Decap) {
				int neck = content.Top + (int)(content.Height * DamageRules.HeadZone * 1.1f);
				Rectangle headR = Opaque(px, frame.Width, new Rectangle(content.Left, content.Top, content.Width, neck - content.Top));
				Rectangle bodyR = Opaque(px, frame.Width, new Rectangle(content.Left, neck, content.Width, content.Bottom - neck));
				if (headR.Width > 0) {
					Chunk h = Make(headR);
					h.Vel = push * 1.2f + new Vector2(Main.rand.NextFloat(-1.5f, 1.5f), -Main.rand.NextFloat(4f, 6.5f));
					h.Spin = Main.rand.NextFloat(0.15f, 0.4f) * (push.X >= 0 ? 1 : -1);
					h.Life = h.MaxLife = 60 * 25;
					h.Bleed = 60;
					Add(h);
				}
				if (bodyR.Width > 0) {
					Chunk b = Make(bodyR);
					b.Corpse = true;
					b.Vel = push * 0.4f;
					b.Spin = 0.04f * (push.X >= 0 ? 1 : -1);
					b.Life = b.MaxLife = 60 * 30;
					b.Bleed = 150; // the neck fountains for a while
					Add(b);
				}
				Vector2 neckPos = topLeft + new Vector2(frame.Width / 2f, neck) * scale;
				Spray(neckPos, new Vector2(push.X >= 0 ? 0.4f : -0.4f, -1f), 25, 4f);
				return;
			}

			// corpse: the whole body topples and stays a while
			Chunk corpse = Make(content);
			corpse.Corpse = true;
			corpse.Vel = push * 0.5f;
			corpse.Spin = 0.05f * (push.X >= 0 ? 1 : -1);
			corpse.Life = corpse.MaxLife = 60 * 30;
			Add(corpse);
		}

		/// <summary>Split points from a to b about every <paramref name="cell"/> pixels, jittered so pieces aren't a neat grid.</summary>
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

		/// <summary>Bounding box of the opaque pixels inside <paramref name="area"/> (empty if fewer than minPixels).</summary>
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

		static void Add(Chunk c)
		{
			if (chunks.Count >= MaxChunks) {
				Release(chunks[0]);
				chunks.RemoveAt(0);
			}
			chunks.Add(c);
			ChunksSpawned++;
		}

		static void Release(Chunk c)
		{
			if (c.Snap != null && --c.Snap.Refs <= 0)
				c.Snap.Target?.Dispose();
			c.Snap = null;
		}

		// ------------------------------------------------------------ simulation

		public override void PostUpdateDusts()
		{
			if (Main.dedServ)
				return;
			for (int i = chunks.Count - 1; i >= 0; i--) {
				Chunk c = chunks[i];
				if (--c.Life <= 0) {
					Release(c);
					chunks.RemoveAt(i);
					continue;
				}
				StepChunk(c);
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
			int size = Math.Clamp((int)(Math.Min(c.Src.Width, c.Src.Height) * c.Scale * 0.8f), 4, 14);
			Vector2 tl = c.Pos - new Vector2(size / 2f);
			if (Collision.SolidCollision(tl, size, size)) {
				// spawned inside terrain (or terrain appeared): drift up out of it
				c.Pos.Y -= 1f;
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
				if (c.Vel.Y > 0)
					c.Grounded = true;
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
				// come to rest lying down: long pieces flat on their side, others the nearest way up
				float target;
				bool tall = c.Src.Height > c.Src.Width * 1.2f;
				if (tall || c.Corpse)
					target = MathF.Round((c.Rot - MathHelper.PiOver2) / MathHelper.Pi) * MathHelper.Pi + MathHelper.PiOver2;
				else
					target = MathF.Round(c.Rot / MathHelper.PiOver2) * MathHelper.PiOver2;
				c.Rot = MathHelper.Lerp(c.Rot, target, 0.15f);
				c.Spin = 0f;
			}
			else {
				c.Rot += c.Spin;
				if (c.Corpse && c.Grounded)
					c.Spin += 0.012f * Math.Sign(c.Spin == 0 ? 1 : c.Spin); // topples over
			}

			if (c.Bleed > 0) {
				c.Bleed--;
				if (Main.rand.NextBool(3)) {
					// corpses bleed from the top (the neck, after a headshot); bits from anywhere
					Vector2 from = c.Corpse ? c.Pos + new Vector2(0, -c.Src.Height * c.Scale / 2f).RotatedBy(c.Rot) : c.Pos;
					AddDrop(from, c.Vel * 0.3f + Main.rand.NextVector2Circular(0.8f, 0.8f) + (c.Corpse ? new Vector2(0, -2.5f).RotatedBy(c.Rot) : Vector2.Zero));
				}
			}
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
			if (chunks.Count == 0 && drops.Count == 0 && splats.Count == 0)
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
				// a little irregular blot from the shape bits
				sb.Draw(px, at - new Vector2(1), one, c, 0f, Vector2.Zero, new Vector2(3, 2), SpriteEffects.None, 0f);
				if ((s.Shape & 1) != 0) sb.Draw(px, at + new Vector2(-3, 0), one, c, 0f, Vector2.Zero, new Vector2(2, 2), SpriteEffects.None, 0f);
				if ((s.Shape & 2) != 0) sb.Draw(px, at + new Vector2(2, -1), one, c, 0f, Vector2.Zero, new Vector2(2, 2), SpriteEffects.None, 0f);
				if ((s.Shape & 4) != 0) sb.Draw(px, at + new Vector2(0, 1), one, c, 0f, Vector2.Zero, new Vector2(1, 2), SpriteEffects.None, 0f);
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
				// snapshots already carry the lighting they were drawn with; texture chunks get lit here
				Color light = ch.Snap != null ? Color.White : Lighting.GetColor(ch.Pos.ToTileCoordinates());
				sb.Draw(ch.Tex, ch.Pos - Main.screenPosition, ch.Src, light * fade, ch.Rot, ch.Src.Size() / 2f, ch.Scale, ch.Fx, 0f);
			}
			sb.End();
		}
	}
}
