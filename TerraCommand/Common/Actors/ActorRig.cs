using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Physics;

namespace TerraCommand.Common.Actors
{
	public enum ActorKind : byte { Soldier, Brain }

	/// <summary>
	/// One actor type's part sprites and proportions (px). Parts are original art (tools/gen_art.py), drawn facing right:
	/// limbs hang down from their joint, the torso stands on the hip, the head sits on the neck. Replace the PNGs in
	/// Content/Actors/&lt;Kind&gt;/ to restyle an actor.
	/// </summary>
	public sealed class ActorArt
	{
		public Asset<Texture2D> Head, Torso, Thigh, Shin, Foot, UpperArm, Forearm;
		public float HipHeight = 15f, CrouchHipHeight = 9f;   // hip above the soles
		public float ThighLen = 7f, ShinLen = 7f, AnkleToSole = 2f;
		public float UpperArmLen = 7f, ForearmLen = 7f;
		public Vector2 Shoulder = new(1f, -13f), Neck = new(1f, -15f);
		public Vector2 FootAnkle = new(2f, 1f);               // ankle pixel inside the foot sprite (toe to the right)
		public Vector2 JetpackAt = new(-7f, -9f);
		public bool HasJetpack = true;

		static readonly Dictionary<ActorKind, ActorArt> all = new();

		public static ActorArt Get(ActorKind kind)
		{
			if (all.TryGetValue(kind, out ActorArt a))
				return a;
			string dir = kind == ActorKind.Brain ? "Brain" : "Soldier";
			Asset<Texture2D> R(string n) => ModContent.Request<Texture2D>($"TerraCommand/Content/Actors/{dir}/{n}");
			a = new ActorArt {
				Head = R("Head"), Torso = R("Torso"), Thigh = R("Thigh"), Shin = R("Shin"), Foot = R("Foot"),
				UpperArm = R("UpperArm"), Forearm = R("Forearm"),
			};
			if (kind == ActorKind.Brain) {
				a.Shoulder = new(0f, -12f);
				a.Neck = new(0f, -18f);
				a.FootAnkle = new(3f, 1f);
				a.UpperArmLen = a.ForearmLen = 6f;
				a.HasJetpack = false;
				a.CrouchHipHeight = 11f;
			}
			all[kind] = a;
			return a;
		}

		public static void Unload() => all.Clear();
	}

	/// <summary>Everything needed to pose an actor for one frame.</summary>
	public struct RigPose
	{
		public ActorKind Kind;
		public Vector2 Feet;            // world: bottom-centre of the hitbox
		public int Dir;                 // 1 facing right, -1 left
		public float Rot;               // torso rotation (screen-clockwise)
		public float AimAngle;          // CC aim angle: + up, - down (relative to facing)
		public float Crouch;            // 0..1
		public bool Prone, Unstable, Grounded;
		public float VelX;
		public float WalkDist;          // ground covered (drives the gait)
		public Item Held;
		public bool HeldInUse;          // vanilla is animating a swing: don't draw our copy
		public float Kick;              // the gun jolts back along the aim when fired (px)
		public Color Light;
	}

	/// <summary>A sprite to draw (world position).</summary>
	public readonly record struct RigPiece(Texture2D Tex, Vector2 Pos, Rectangle? Src, Color Color, float Rot, Vector2 Origin, float Scale, SpriteEffects Fx);

	/// <summary>
	/// Poses a Cortex Command style actor from separate parts: two-bone legs whose feet follow the CC limb paths and plant on the
	/// actual ground pixels, two-bone arms that reach the held weapon's grip and fore-grip, a torso that leans/tumbles about the
	/// hip, a head that looks toward the aim, and the jetpack on the back.
	/// </summary>
	public static class RigRenderer
	{
		/// <summary>Where the hip goes: lowered by the crouch, and moved so a lying body is centred on its (short) hitbox.</summary>
		public static Vector2 Hip(in RigPose p, ActorArt art)
		{
			float lying = Lying(p);
			float stand = MathHelper.Lerp(art.HipHeight, art.CrouchHipHeight, p.Crouch);
			return p.Feet + new Vector2(-p.Dir * 6.5f * lying, -MathHelper.Lerp(stand, 6f, lying));
		}

		public static float Lying(in RigPose p) => Math.Clamp((Math.Abs(MathF.Sin(p.Rot)) - 0.3f) / 0.5f, 0f, 1f);

		/// <summary>Aim direction in the world (screen-oriented angle).</summary>
		public static float AimWorld(in RigPose p) => p.Dir < 0 ? MathHelper.Pi + p.AimAngle : -p.AimAngle;

		public static Vector2 NozzleWorld(in RigPose p)
		{
			ActorArt art = ActorArt.Get(p.Kind);
			return Hip(p, art) + new Vector2(art.JetpackAt.X * p.Dir, art.JetpackAt.Y + 9f).RotatedBy(p.Rot);
		}

		/// <summary>The front shoulder (world): aiming is measured from here, so the muzzle line passes through the cursor.</summary>
		public static Vector2 Shoulder(in RigPose p)
		{
			ActorArt art = ActorArt.Get(p.Kind);
			return Hip(p, art) + new Vector2(art.Shoulder.X * p.Dir, art.Shoulder.Y).RotatedBy(p.Rot);
		}

		/// <summary>Where the front hand grips the held item (world).</summary>
		public static Vector2 Grip(in RigPose p)
		{
			ActorArt art = ActorArt.Get(p.Kind);
			Vector2 shoulder = Hip(p, art) + new Vector2(art.Shoulder.X * p.Dir, art.Shoulder.Y).RotatedBy(p.Rot);
			return shoulder + AimWorld(p).ToRotationVector2() * 8f + new Vector2(0f, 1.5f);
		}

		public static void Build(RigPose p, List<RigPiece> outList)
		{
			ActorArt art = ActorArt.Get(p.Kind);
			if (!art.Torso.IsLoaded || !art.Thigh.IsLoaded)
				return;
			int dir = p.Dir;
			float rot = p.Rot;
			Vector2 hip = Hip(p, art);
			SpriteEffects fx = dir > 0 ? SpriteEffects.None : SpriteEffects.FlipHorizontally;
			Color light = p.Light, back = p.Light.MultiplyRGB(new Color(165, 165, 165));
			bool lying = p.Prone || p.Unstable;
			float legFrame = lying ? rot : 0f;   // upright legs keep world orientation; only the torso leans
			bool holding = IsHoldable(p.Held) && !p.Unstable;
			Vector2 W(Vector2 local) => hip + local.RotatedBy(rot);

			// ---------------- legs
			Vector2 FootLocal(int leg)
			{
				if (p.Unstable)
					return new Vector2((leg == 0 ? 2 : -3) * dir, art.HipHeight - 2f);
				bool moving = Math.Abs(p.VelX) > 0.05f && p.Grounded;
				if (moving) {
					LimbPath path = p.Prone ? LimbPaths.Crawl : Math.Abs(p.VelX) > 1.1f ? LimbPaths.Run : LimbPaths.Walk;
					float prog = (p.WalkDist / path.Length + (leg == 0 ? 0f : 0.5f)) % 1f;
					Vector2 at = path.At(prog * path.Length, out _);
					bool backwards = Math.Sign(p.VelX) != dir;   // walking backwards runs the path the other way
					return new Vector2(at.X * (backwards ? -dir : dir), at.Y);
				}
				if (p.Prone)
					return new Vector2((leg == 0 ? 1 : -1) * dir, art.HipHeight);
				if (!p.Grounded)
					return new Vector2((leg == 0 ? 3 : -3) * dir, art.HipHeight - 3f);
				float h = MathHelper.Lerp(art.HipHeight, art.CrouchHipHeight, p.Crouch);
				float spread = MathHelper.Lerp(3.5f, 5f, p.Crouch);
				return new Vector2((leg == 0 ? spread : -spread) * dir, h);
			}

			void Leg(int leg, Color c)
			{
				Vector2 joint = hip + new Vector2((leg == 0 ? 1 : -1) * dir, 0f).RotatedBy(legFrame);
				Vector2 local = FootLocal(leg);
				Vector2 sole = hip + local.RotatedBy(legFrame);
				if (!lying && p.Grounded) {
					// plant the foot on the actual ground (pixel terrain): feet step onto bumps and into dips
					float ground = GroundBelow(new Vector2(sole.X, hip.Y), art.HipHeight + 6f);
					if (sole.Y > ground)
						sole.Y = ground;
				}
				Vector2 ankle = sole - new Vector2(0f, art.AnkleToSole).RotatedBy(legFrame);
				Vector2 knee = Ik(joint, ankle, art.ThighLen, art.ShinLen, (new Vector2(dir, 0f)).RotatedBy(legFrame));
				ankle = knee + (ankle - knee).SafeNormalize(Vector2.UnitY) * art.ShinLen; // where the leg actually reaches
				Bone(art.Thigh.Value, joint, knee, c);
				Bone(art.Shin.Value, knee, ankle, c);
				Texture2D foot = art.Foot.Value;
				Vector2 o = dir > 0 ? art.FootAnkle : new Vector2(foot.Width - art.FootAnkle.X, art.FootAnkle.Y);
				outList.Add(new RigPiece(foot, ankle, null, c, legFrame, o, 1f, fx));
			}

			// ---------------- arms
			Vector2 frontShoulder = W(new Vector2(art.Shoulder.X * dir, art.Shoulder.Y));
			Vector2 aimDir = AimWorld(p).ToRotationVector2();
			Vector2 grip = frontShoulder + aimDir * (8f - p.Kick) + new Vector2(0f, 1.5f);

			void Arm(int arm, Color c)
			{
				Vector2 shoulder = arm == 0 ? frontShoulder : W(new Vector2(-art.Shoulder.X * dir, art.Shoulder.Y));
				Vector2 hand;
				if (holding)
					hand = arm == 0 ? grip : grip + aimDir * (IsGunLike(p.Held) ? 6f : 2f);
				else {
					// arms hang and swing a little with the stride; thrown bodies flail
					float swing = MathF.Sin(p.WalkDist / 9f + arm * MathHelper.Pi) * (p.Grounded ? 3f : 1f);
					hand = p.Unstable ? shoulder + new Vector2(swing * 0.5f, 12f) : shoulder + new Vector2(swing * dir, 12f).RotatedBy(rot);
				}
				Vector2 elbow = Ik(shoulder, hand, art.UpperArmLen, art.ForearmLen, new Vector2(0f, 1f).RotatedBy(holding ? 0f : rot) - new Vector2(dir * 0.3f, 0f));
				hand = elbow + (hand - elbow).SafeNormalize(Vector2.UnitY) * art.ForearmLen;
				Bone(art.UpperArm.Value, shoulder, elbow, c);
				Bone(art.Forearm.Value, elbow, hand, c);
			}

			// ---------------- draw order (facing): back arm, back leg, jetpack, torso, head, front leg, held item, front arm
			Arm(1, back);
			Leg(1, back);
			if (art.HasJetpack && RigPlayer.JetpackTex != null && RigPlayer.JetpackTex.IsLoaded) {
				Texture2D jt = RigPlayer.JetpackTex.Value;
				outList.Add(new RigPiece(jt, W(new Vector2(art.JetpackAt.X * dir, art.JetpackAt.Y)), null, light, rot, jt.Size() / 2f, 1f, fx));
			}
			Texture2D torso = art.Torso.Value;
			outList.Add(new RigPiece(torso, hip, null, light, rot, new Vector2(torso.Width / 2f, torso.Height), 1f, fx));
			Texture2D head = art.Head.Value;
			float headTurn = p.Unstable ? 0f : Math.Clamp(-p.AimAngle * 0.45f * dir - rot * 0.4f, -0.6f, 0.6f);
			outList.Add(new RigPiece(head, W(new Vector2(art.Neck.X * dir, art.Neck.Y)), null, light, rot + headTurn,
				new Vector2(head.Width / 2f, head.Height), 1f, fx));
			Leg(0, light);
			if (holding && !p.HeldInUse)
				Held(grip);
			Arm(0, light);

			void Held(Vector2 at)
			{
				Item item = p.Held;
				Main.instance.LoadItem(item.type);
				Texture2D tex = TextureAssets.Item[item.type].Value;
				Rectangle src = Main.itemAnimations[item.type] != null ? Main.itemAnimations[item.type].GetFrame(tex) : tex.Frame();
				float aim = AimWorld(p);
				Color c = item.GetAlpha(light);
				if (IsGunLike(item)) {
					outList.Add(new RigPiece(tex, at, src, c, aim, new Vector2(src.Width * 0.3f, src.Height * 0.55f), item.scale,
						dir > 0 ? SpriteEffects.None : SpriteEffects.FlipVertically));
				}
				else {
					// swords and tools are drawn diagonally (handle bottom-left): lay the blade along the aim
					float r = dir > 0 ? aim + MathHelper.PiOver4 : aim - MathHelper.Pi - MathHelper.PiOver4 + MathHelper.TwoPi;
					Vector2 o = dir > 0 ? new Vector2(src.Width * 0.15f, src.Height * 0.85f) : new Vector2(src.Width * 0.85f, src.Height * 0.85f);
					outList.Add(new RigPiece(tex, at, src, c, r, o, item.scale, fx));
				}
			}

			void Bone(Texture2D tex, Vector2 from, Vector2 to, Color c)
			{
				float r = (to - from).ToRotation() - MathHelper.PiOver2;   // sprites hang straight down from the joint
				outList.Add(new RigPiece(tex, from, null, c, r, new Vector2(tex.Width / 2f, 1f), 1f, fx));
			}
		}

		/// <summary>Only weapons and tools are carried in the hands; everything else stays in the inventory.</summary>
		public static bool IsHoldable(Item item) =>
			item != null && !item.IsAir && (item.damage > 0 || item.pick > 0 || item.axe > 0 || item.hammer > 0) && !item.consumable;

		public static bool IsGunLike(Item item) =>
			item != null && (item.useStyle == ItemUseStyleID.Shoot || item.useStyle == ItemUseStyleID.HoldUp && item.shoot > 0);

		/// <summary>Two-bone IK from <paramref name="a"/> to <paramref name="c"/>: the middle joint, bent toward <paramref name="bend"/>.</summary>
		static Vector2 Ik(Vector2 a, Vector2 c, float l1, float l2, Vector2 bend)
		{
			Vector2 d = c - a;
			float dist = Math.Clamp(d.Length(), Math.Abs(l1 - l2) + 0.01f, l1 + l2 - 0.01f);
			float baseAng = d.ToRotation();
			float cos = (l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist);
			float off = MathF.Acos(Math.Clamp(cos, -1f, 1f));
			Vector2 j1 = a + (baseAng + off).ToRotationVector2() * l1;
			Vector2 j2 = a + (baseAng - off).ToRotationVector2() * l1;
			return Vector2.Dot(j1 - a, bend) >= Vector2.Dot(j2 - a, bend) ? j1 : j2;
		}

		/// <summary>The first solid pixel below a point (within a range), else the end of the range.</summary>
		static float GroundBelow(Vector2 from, float range)
		{
			for (float y = 0; y < range; y += 1f)
				if (Collision.SolidCollision(new Vector2(from.X, from.Y + y), 1, 1))
					return from.Y + y;
			return from.Y + range;
		}

		/// <summary>Draws pieces straight to a sprite batch (for bodies that aren't players, e.g. idle clones).</summary>
		public static void Draw(SpriteBatch sb, List<RigPiece> pieces)
		{
			foreach (RigPiece r in pieces)
				sb.Draw(r.Tex, (r.Pos - Main.screenPosition).Floor(), r.Src, r.Color, r.Rot, r.Origin, r.Scale, r.Fx, 0f);
		}
	}

	/// <summary>Draws the player's body as a CC actor instead of the Terraria character (armor still counts, but isn't shown).</summary>
	public class RigPlayer : ModPlayer
	{
		public static Asset<Texture2D> JetpackTex;
		public float WalkDist;
		public ActorKind Kind = ActorKind.Soldier;

		public override void Load()
		{
			if (!Main.dedServ)
				JetpackTex = ModContent.Request<Texture2D>("TerraCommand/Content/Actors/Jetpack");
		}

		public override void Unload()
		{
			JetpackTex = null;
			ActorArt.Unload();
		}

		public override void SaveData(Terraria.ModLoader.IO.TagCompound tag) => tag["kind"] = (byte)Kind;

		public override void LoadData(Terraria.ModLoader.IO.TagCompound tag) => Kind = (ActorKind)tag.GetByte("kind");

		public override void PostUpdate()
		{
			Player.fullRotation = 0f;
			if (Player.velocity.Y == 0f)
				WalkDist += Math.Abs(Player.velocity.X);
		}

		public RigPose Pose()
		{
			CCActor a = Player.GetModPlayer<BodyPhysicsPlayer>().Actor;
			return new RigPose {
				Kind = Kind,
				Feet = Player.Bottom + new Vector2(0f, Player.gfxOffY),
				Dir = a.FlipFactor,
				Rot = a.Rot,
				AimAngle = a.AimAngle,
				Crouch = a.Status == ActorStatus.Stable && a.Prone == ProneState.NotProne ? a.CrouchAmount : 0f,
				Prone = a.Prone != ProneState.NotProne && a.Status == ActorStatus.Stable,
				Unstable = a.Status != ActorStatus.Stable,
				Grounded = Player.velocity.Y == 0f,
				VelX = Player.velocity.X,
				WalkDist = WalkDist,
				Held = Player.HeldItem,
				HeldInUse = Player.itemAnimation > 0 && !RigRenderer.IsGunLike(Player.HeldItem),
				Kick = Player.GetModPlayer<Combat.AimPlayer>().GunKick,
				Light = Lighting.GetColor(Player.Center.ToTileCoordinates()),
			};
		}

		public Vector2 NozzleWorld() => RigRenderer.NozzleWorld(Pose());

		public override void HideDrawLayers(PlayerDrawSet drawInfo)
		{
			// the Terraria character isn't drawn at all; vanilla only shows the swing of a melee/tool item in use
			Player p = drawInfo.drawPlayer;
			foreach (PlayerDrawLayer layer in PlayerDrawLayerLoader.Layers) {
				if (layer.Mod != null)
					continue;
				if (layer == PlayerDrawLayers.HeldItem && p.itemAnimation > 0 && !RigRenderer.IsGunLike(p.HeldItem))
					continue;
				layer.Hide();
			}
		}

		public override void ModifyDrawInfo(ref PlayerDrawSet drawInfo)
		{
			if (!ReferenceEquals(drawInfo.drawPlayer, Main.LocalPlayer) && drawInfo.drawPlayer.TryGetModPlayer(out BodyPhysicsPlayer bp))
				bp.Actor.Flipped = drawInfo.drawPlayer.direction < 0; // other players / drawn-only bodies face as vanilla says
		}

		public class BodyLayer : PlayerDrawLayer
		{
			static readonly List<RigPiece> pieces = new();

			public override Position GetDefaultPosition() => new Between(PlayerDrawLayers.BalloonAcc, PlayerDrawLayers.Skin);

			public override bool GetDefaultVisibility(PlayerDrawSet d) => d.shadow == 0f && !d.drawPlayer.dead && !d.headOnlyRender && !d.drawPlayer.mount.Active;

			protected override void Draw(ref PlayerDrawSet d)
			{
				Player p = d.drawPlayer;
				RigPose pose = p.GetModPlayer<RigPlayer>().Pose();
				// follow the draw position (snapshots draw a player away from its entity)
				pose.Feet = d.Position + new Vector2(p.width / 2f, p.height);
				pieces.Clear();
				RigRenderer.Build(pose, pieces);
				foreach (RigPiece r in pieces)
					d.DrawDataCache.Add(new DrawData(r.Tex, (r.Pos - Main.screenPosition).Floor(), r.Src, r.Color, r.Rot, r.Origin, r.Scale, r.Fx));
			}
		}
	}
}
