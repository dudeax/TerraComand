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
	/// <summary>
	/// Draws a player's body as a Cortex Command actor rig, re-posing the pieces vanilla draws (so skin, armor, vanity and dyes
	/// all still show): the torso leans/tumbles around the hip, the head turns toward the aim, both arms hold the aim, the held
	/// item is always shown in the hands pointing at the aim, and the legs step with the CC gait (Terraria's walk frames advanced
	/// by distance actually covered, so feet don't slide), squash when crouched, and lie along the body when prone or thrown.
	/// </summary>
	public class RigPlayer : ModPlayer
	{
		// Joint positions in the vanilla 40x56 player frame (facing right): the frame's row 52 sits on the hitbox's bottom.
		const float HipAboveFeet = 10f;      // frame y 42
		const float NeckAboveFeet = 23f;     // frame y 29
		const float ShoulderAboveFeet = 23f; // composite shoulder: Center + (-4*dir, -2) on a 42px body
		const float LegLength = 11f;
		const float StepPx = 3.6f;           // ground covered per walk frame (14 frames ~ one CC stride cycle)

		float walkDist;
		public float LegsSquash = 1f;        // 1 standing, < 1 crouched

		BodyPhysicsPlayer Body => Player.GetModPlayer<BodyPhysicsPlayer>();

		/// <summary>World-space (screen-oriented) angle of the aim.</summary>
		public float AimWorldAngle {
			get {
				CCActor a = Body.Actor;
				return a.Flipped ? MathHelper.Pi + a.AimAngle : -a.AimAngle;
			}
		}

		bool UsingItem => Player.itemAnimation > 0;

		// ------------------------------------------------------------ pose (after vanilla's PlayerFrame)

		public override void PostUpdate()
		{
			CCActor a = Body.Actor;
			Player.fullRotation = 0f;
			Player.bodyFrame.Y = 0;

			// legs: walk frames advance with the ground actually covered
			bool stable = a.Status == ActorStatus.Stable;
			bool onGround = Player.velocity.Y == 0f;
			if (onGround && Math.Abs(Player.velocity.X) > 0.05f)
				walkDist += Math.Abs(Player.velocity.X);
			int frame = 0;
			if (!stable || !onGround)
				frame = 5;                                                   // legs loose in the air / tumbling
			else if (Math.Abs(Player.velocity.X) > 0.05f)
				frame = 6 + (int)(walkDist / StepPx) % 14;                   // walk / run / crawl cycle
			Player.legFrame.Y = Player.legFrame.Height * frame;

			LegsSquash = stable && a.Prone == ProneState.NotProne ? MathHelper.Lerp(1f, 0.55f, a.CrouchAmount) : 1f;

			// arms hold the aim (relative to the leaning torso), unless vanilla is animating an item use
			if (!UsingItem && !Player.dead) {
				// thrown bodies go limp: the arms hang (and swing a little) instead of aiming
				float world = stable ? AimWorldAngle : MathHelper.PiOver2 + MathF.Sin(Main.GameUpdateCount * 0.3f) * 0.4f;
				float r = world - a.Rot - MathHelper.PiOver2;
				Player.SetCompositeArmFront(true, Player.CompositeArmStretchAmount.Full, r);
				Player.SetCompositeArmBack(true, Player.CompositeArmStretchAmount.ThreeQuarters, r);
			}
		}

		// ------------------------------------------------------------ joints in screen space

		/// <summary>Bottom-centre of the hitbox, where vanilla puts the feet (screen space).</summary>
		static Vector2 Feet(ref PlayerDrawSet d) => d.Position - Main.screenPosition + new Vector2(d.drawPlayer.width / 2f, d.drawPlayer.height);

		/// <summary>Where the hip goes: lowered by bent legs, and moved so a lying body is centred on its (short) hitbox.</summary>
		Vector2 Hip(Vector2 feet, CCActor a)
		{
			float lying = Math.Clamp((Math.Abs(MathF.Sin(a.Rot)) - 0.3f) / 0.5f, 0f, 1f);
			float standHip = HipAboveFeet - LegLength * (1f - LegsSquash);
			float x = MathHelper.Lerp(0f, -a.FlipFactor * 9f, lying);
			float y = MathHelper.Lerp(standHip, 9f, lying);
			return feet + new Vector2(x, -y);
		}

		/// <summary>The hand in the un-leaned body (vanilla's GetFrontHandPosition, but from the bottom-anchored sprite).</summary>
		Vector2 FrontHand(Vector2 feet, CCActor a, float armRot)
		{
			int dir = a.FlipFactor;
			Vector2 shoulder = feet + new Vector2(-4f * dir, -ShoulderAboveFeet);
			Vector2 v = (armRot + MathHelper.PiOver2).ToRotationVector2() * 10f;
			v += new Vector2(0f, 3f * dir).RotatedBy(armRot + MathHelper.PiOver2);
			return shoulder + v;
		}

		// ------------------------------------------------------------ re-posing vanilla's draw data

		enum Part { Upper, Head, Legs, Skip }

		readonly HashSet<Texture2D> headTex = new(), legTex = new();

		void CollectTextures(Player p, int skin)
		{
			headTex.Clear();
			legTex.Clear();
			foreach (int i in new[] { 0, 1, 2, 15 })
				Add(headTex, TextureAssets.Players[skin, i]);
			foreach (int i in new[] { 10, 11, 12 })
				Add(legTex, TextureAssets.Players[skin, i]);
			Add(headTex, Get(TextureAssets.PlayerHair, p.hair));
			Add(headTex, Get(TextureAssets.PlayerHairAlt, p.hair));
			Add(headTex, Get(TextureAssets.ArmorHead, p.head));
			Add(headTex, Get(TextureAssets.AccFace, p.face));
			Add(headTex, Get(TextureAssets.AccFace, p.faceHead));
			Add(headTex, Get(TextureAssets.AccFace, p.faceFlower));
			Add(headTex, Get(TextureAssets.AccBeard, p.beard));
			Add(legTex, Get(TextureAssets.ArmorLeg, p.legs));
			Add(legTex, Get(TextureAssets.AccShoes, p.shoe));
		}

		static ReLogic.Content.Asset<Texture2D> Get(ReLogic.Content.Asset<Texture2D>[] arr, int i) => i > 0 && i < arr.Length ? arr[i] : null;

		static void Add(HashSet<Texture2D> set, ReLogic.Content.Asset<Texture2D> asset)
		{
			if (asset != null && asset.IsLoaded)
				set.Add(asset.Value);
		}

		public override void TransformDrawData(ref PlayerDrawSet drawInfo)
		{
			Player p = drawInfo.drawPlayer;
			if (drawInfo.shadow != 0f || p.mount.Active || p.dead || drawInfo.headOnlyRender)
				return;
			if (!p.TryGetModPlayer(out BodyPhysicsPlayer body))
				return;
			CCActor a = body.Actor;
			if (!ReferenceEquals(p, Main.LocalPlayer))
				a.Flipped = p.direction < 0; // other players and drawn-only bodies (idle clones) face as vanilla says
			CollectTextures(p, drawInfo.skinVar);

			Vector2 feet = Feet(ref drawInfo);
			Vector2 hip0 = feet - new Vector2(0f, HipAboveFeet);
			Vector2 neck0 = feet - new Vector2(0f, NeckAboveFeet);
			Vector2 hip = Hip(feet, a);
			float lean = a.Rot;
			bool lyingOrThrown = a.Status != ActorStatus.Stable || a.Prone != ProneState.NotProne;
			float legRot = lyingOrThrown ? a.Rot : 0f;
			// CC heads look toward the aim, part way, against the lean
			float headTurn = Math.Clamp(-a.AimAngle * 0.45f * a.FlipFactor - lean * 0.4f, -0.6f, 0.6f);
			if (a.Status != ActorStatus.Stable)
				headTurn = 0f;

			for (int i = 0; i < drawInfo.DrawDataCache.Count; i++) {
				DrawData d = drawInfo.DrawDataCache[i];
				switch (Classify(d)) {
					case Part.Legs:
						// pivot the legs sprite on the hip (frame row 52 - HipAboveFeet; the feet are on row 52), squash it for a
						// crouch, and lay it along the body when lying or thrown
						d.origin = new Vector2(d.origin.X, 52f - HipAboveFeet);
						d.position = hip;
						d.rotation += legRot;
						d.scale = new Vector2(d.scale.X, d.scale.Y * LegsSquash);
						break;
					case Part.Head:
						d.position = neck0 + (d.position - neck0).RotatedBy(headTurn);
						d.rotation += headTurn;
						goto case Part.Upper;
					case Part.Upper:
						d.position = hip + (d.position - hip0).RotatedBy(lean);
						d.rotation += lean;
						break;
				}
				drawInfo.DrawDataCache[i] = d;
			}
		}

		Part Classify(in DrawData d)
		{
			if (d.texture == null)
				return Part.Skip;
			if (legTex.Contains(d.texture))
				return Part.Legs;
			if (headTex.Contains(d.texture))
				return Part.Head;
			return Part.Upper;
		}

		/// <summary>The jetpack nozzle in the world (the pack sits on the back, leaning with the torso).</summary>
		public Vector2 NozzleWorld()
		{
			CCActor a = Body.Actor;
			Vector2 hip = Hip(Player.Bottom, a);
			return hip + new Vector2(-a.FlipFactor * 9f, -8f).RotatedBy(a.Rot);
		}

		public class JetpackLayer : PlayerDrawLayer
		{
			static Asset<Texture2D> tex;

			public override void Load()
			{
				if (!Main.dedServ)
					tex = ModContent.Request<Texture2D>("TerraCommand/Content/Actors/Jetpack");
			}

			public override Position GetDefaultPosition() => new Between(PlayerDrawLayers.BalloonAcc, PlayerDrawLayers.Skin);

			public override bool GetDefaultVisibility(PlayerDrawSet d) => d.shadow == 0f && !d.drawPlayer.dead && !d.headOnlyRender && !d.drawPlayer.mount.Active;

			protected override void Draw(ref PlayerDrawSet d)
			{
				Player p = d.drawPlayer;
				int dir = p.GetModPlayer<BodyPhysicsPlayer>().Actor.FlipFactor;
				Vector2 at = Feet(ref d) + new Vector2(-dir * 9f, -27f);
				Color light = Lighting.GetColor(p.Center.ToTileCoordinates());
				d.DrawDataCache.Add(new DrawData(tex.Value, at.Floor(), null, light, 0f, tex.Size() / 2f, 1f,
					dir > 0 ? SpriteEffects.None : SpriteEffects.FlipHorizontally));
			}
		}

		// ------------------------------------------------------------ the held item, always in hand

		public class HeldItemLayer : PlayerDrawLayer
		{
			public override Position GetDefaultPosition() => new Between(PlayerDrawLayers.Shield, PlayerDrawLayers.ArmOverItem);

			public override bool GetDefaultVisibility(PlayerDrawSet d)
			{
				Player p = d.drawPlayer;
				return d.shadow == 0f && !p.dead && p.itemAnimation == 0 && !p.HeldItem.IsAir && !p.mount.Active && !d.headOnlyRender
					&& p.HeldItem.useStyle != ItemUseStyleID.None && !p.HeldItem.consumable;
			}

			protected override void Draw(ref PlayerDrawSet d)
			{
				Player p = d.drawPlayer;
				var rig = p.GetModPlayer<RigPlayer>();
				CCActor a = p.GetModPlayer<BodyPhysicsPlayer>().Actor;
				Item item = p.HeldItem;
				Main.instance.LoadItem(item.type);
				Texture2D tex = TextureAssets.Item[item.type].Value;
				Rectangle src = Main.itemAnimations[item.type] != null ? Main.itemAnimations[item.type].GetFrame(tex) : tex.Frame();

				Vector2 feet = Feet(ref d);
				float armRot = rig.AimWorldAngle - a.Rot - MathHelper.PiOver2;
				Vector2 hand = rig.FrontHand(feet, a, armRot);
				float aim = rig.AimWorldAngle - a.Rot; // in the un-leaned body; TransformDrawData adds the lean
				int dir = a.FlipFactor;
				Color light = Lighting.GetColor(p.Center.ToTileCoordinates());
				Color c = item.GetAlpha(light);

				float rot;
				Vector2 origin;
				SpriteEffects fx;
				if (item.useStyle == ItemUseStyleID.Shoot || item.useStyle == ItemUseStyleID.HoldUp && item.shoot > 0) {
					// guns, bows, staves: point along the aim, grip a quarter of the way along
					rot = aim;
					fx = dir > 0 ? SpriteEffects.None : SpriteEffects.FlipVertically;
					origin = new Vector2(src.Width * 0.3f, src.Height * 0.5f);
				}
				else {
					// swords, pickaxes and tools are drawn diagonally (handle bottom-left): lay the blade along the aim
					fx = dir > 0 ? SpriteEffects.None : SpriteEffects.FlipHorizontally;
					rot = dir > 0 ? aim + MathHelper.PiOver4 : aim - MathHelper.Pi - MathHelper.PiOver4 + MathHelper.TwoPi;
					origin = dir > 0 ? new Vector2(src.Width * 0.15f, src.Height * 0.85f) : new Vector2(src.Width * 0.85f, src.Height * 0.85f);
				}
				d.DrawDataCache.Add(new DrawData(tex, hand.Floor(), src, c, rot, origin, item.scale, fx));
			}
		}
	}
}
