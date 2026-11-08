using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace TerraCommand.Common.Combat
{
	/// <summary>
	/// Cortex Command style damage: headshots, visible wounds (cosmetic only: no bleeding), gibbing on violent
	/// deaths, and no natural health regeneration (potions, buffs and accessories still heal).
	/// </summary>
	public static class DamageRules
	{
		public const float HeadshotMultiplier = 1.75f;
		public const float HeadZone = 0.25f;          // top fraction of the hitbox
		public const float GibOverkill = 0.5f;        // a single hit of this fraction of max life gibs
		public const int MaxWounds = 12;

		/// <summary>Upright bodies have heads; blobs, worms and flyers mostly don't.</summary>
		public static bool HasHead(NPC npc) => npc.height >= npc.width * 1.25f && npc.aiStyle != NPCAIStyleID.Worm;

		public static bool InHeadZone(Rectangle body, Vector2 hitPoint) => hitPoint.Y < body.Top + body.Height * HeadZone;

		public static Vector2 HitPoint(Rectangle body, Projectile proj) =>
			new(Math.Clamp(proj.Center.X, body.Left, body.Right), Math.Clamp(proj.Center.Y, body.Top, body.Bottom));

		public static int GibCount { get; private set; }

		public static bool IsExplosive(Projectile p) => ProjectileID.Sets.Explosive[p.type] || p.aiStyle == ProjAIStyleID.Explosive;

		/// <summary>Bursts a body into blood and chunks (cosmetic, runs on every client).</summary>
		public static void Gib(Rectangle body, Vector2 push)
		{
			GibCount++;
			if (Main.dedServ)
				return;
			int amount = Math.Clamp(body.Width * body.Height / 25, 25, 90);
			for (int i = 0; i < amount; i++) {
				var d = Dust.NewDustDirect(body.TopLeft(), body.Width, body.Height, DustID.Blood,
					push.X + Main.rand.NextFloat(-5f, 5f), push.Y + Main.rand.NextFloat(-7f, 1f), 0, default, Main.rand.NextFloat(1.1f, 2.1f));
				d.noGravity = false;
			}
			for (int i = 0; i < amount / 6; i++) // heavier chunks
				Dust.NewDust(body.TopLeft(), body.Width, body.Height, DustID.GemRuby, push.X + Main.rand.NextFloat(-4f, 4f), push.Y - Main.rand.NextFloat(2f, 6f), 0, new Color(120, 20, 20), 1.6f);
			SoundEngine.PlaySound(SoundID.NPCDeath1 with { Pitch = -0.3f }, body.Center.ToVector2());
		}

		/// <summary>Adds a wound mark at a point (offset from the body's centre, unflipped).</summary>
		public static void AddWound(List<Vector2> wounds, Rectangle body, Vector2? hitPoint, bool head, int direction)
		{
			Vector2 off;
			if (hitPoint is Vector2 hp) {
				off = hp - body.Center.ToVector2();
			}
			else {
				float y = head ? body.Height * (-0.5f + Main.rand.NextFloat(0.05f, HeadZone)) : body.Height * Main.rand.NextFloat(-0.25f, 0.35f);
				off = new Vector2(Main.rand.NextFloat(-0.35f, 0.35f) * body.Width, y);
			}
			off.X *= direction; // store in "facing right" space so wounds turn with the body
			if (wounds.Count >= MaxWounds)
				wounds.RemoveAt(0);
			wounds.Add(off);
		}

		public static void DrawWounds(SpriteBatch sb, List<Vector2> wounds, Vector2 center, int direction, Color light)
		{
			if (wounds.Count == 0)
				return;
			Texture2D px = TextureAssets.MagicPixel.Value;
			Color dark = new Color(95, 8, 8).MultiplyRGB(light), mid = new Color(150, 20, 20).MultiplyRGB(light);
			foreach (Vector2 w in wounds) {
				Vector2 at = (center + new Vector2(w.X * direction, w.Y)).Floor();
				sb.Draw(px, at, new Rectangle(0, 0, 1, 1), dark, 0f, Vector2.Zero, new Vector2(4f, 4f), SpriteEffects.None, 0f);
				sb.Draw(px, at + new Vector2(1, 1), new Rectangle(0, 0, 1, 1), mid, 0f, Vector2.Zero, new Vector2(2f, 2f), SpriteEffects.None, 0f);
			}
		}
	}

	public class DamageGlobalNPC : GlobalNPC
	{
		public override bool InstancePerEntity => true;

		public readonly List<Vector2> Wounds = new();
		public int BlastedTimer;           // set by Explosions.Blast on every machine
		Vector2? pendingHitPoint;
		bool pendingHead;

		public override void ResetEffects(NPC npc)
		{
			if (BlastedTimer > 0)
				BlastedTimer--;
		}

		public override void ModifyHitByProjectile(NPC npc, Projectile projectile, ref NPC.HitModifiers modifiers)
		{
			Vector2 hp = DamageRules.HitPoint(npc.Hitbox, projectile);
			pendingHitPoint = hp;
			pendingHead = DamageRules.HasHead(npc) && DamageRules.InHeadZone(npc.Hitbox, hp);
			if (DamageRules.IsExplosive(projectile)) // vanilla deals blast damage before the projectile's Kill hooks
				BlastedTimer = 15;
			if (pendingHead) {
				modifiers.SourceDamage *= DamageRules.HeadshotMultiplier;
				LastHeadshot = true;
				if (!Main.dedServ)
					CombatText.NewText(npc.Hitbox, Color.OrangeRed, "Headshot!", dramatic: false, dot: true);
			}
			else
				LastHeadshot = false;
		}

		/// <summary>Whether the most recent projectile hit (on this machine) was a headshot. For tests.</summary>
		public static bool LastHeadshot;

		public override void HitEffect(NPC npc, NPC.HitInfo hit)
		{
			if (npc.life > 0) {
				if (hit.Damage > 0)
					DamageRules.AddWound(Wounds, npc.Hitbox, pendingHitPoint, pendingHead, npc.direction == 0 ? 1 : npc.direction);
			}
			else if (BlastedTimer > 0 || hit.Damage >= npc.lifeMax * DamageRules.GibOverkill) {
				DamageRules.Gib(npc.Hitbox, new Vector2(hit.HitDirection * 3f, -2f));
			}
			pendingHitPoint = null;
			pendingHead = false;
		}

		public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
		{
			if (npc.life >= npc.lifeMax && Wounds.Count > 0)
				Wounds.Clear(); // healed
			DamageRules.DrawWounds(spriteBatch, Wounds, npc.Center - screenPos + new Vector2(0, npc.gfxOffY), npc.direction == 0 ? 1 : npc.direction, drawColor);
		}
	}

	public class DamagePlayer : ModPlayer
	{
		public readonly List<Vector2> Wounds = new();
		public int BlastedTimer;
		Vector2? pendingHitPoint;
		bool pendingHead;

		public override void NaturalLifeRegen(ref float regen)
		{
			if (!TerraCommandConfig.Instance.NaturalLifeRegen)
				regen = 0f;
		}

		public override void ResetEffects()
		{
			if (BlastedTimer > 0)
				BlastedTimer--;
			if (Player.statLife >= Player.statLifeMax2 && Wounds.Count > 0)
				Wounds.Clear();
		}

		public override void ModifyHitByProjectile(Projectile proj, ref Player.HurtModifiers modifiers)
		{
			Vector2 hp = DamageRules.HitPoint(Player.Hitbox, proj);
			pendingHitPoint = hp;
			pendingHead = DamageRules.InHeadZone(Player.Hitbox, hp);
			if (DamageRules.IsExplosive(proj))
				BlastedTimer = 15;
			if (pendingHead) {
				modifiers.SourceDamage *= DamageRules.HeadshotMultiplier;
				if (!Main.dedServ)
					CombatText.NewText(Player.Hitbox, Color.OrangeRed, "Headshot!", dramatic: false, dot: true);
			}
		}

		public override void OnHurt(Player.HurtInfo info)
		{
			Testing.AutoTest.Trace($"{Player.name} hurt {info.Damage} by {(info.DamageSource.SourceProjectileType > 0 ? Lang.GetProjectileName(info.DamageSource.SourceProjectileType).Value : info.DamageSource.SourceCustomReason ?? "?")} (life {Player.statLife}/{Player.statLifeMax2})");
			if (info.Damage > 0)
				DamageRules.AddWound(Wounds, Player.Hitbox, pendingHitPoint, pendingHead, Player.direction);
			pendingHitPoint = null;
			pendingHead = false;
		}

		public bool IsViolentDeath(double damage) => BlastedTimer > 0 || damage >= Player.statLifeMax2 * DamageRules.GibOverkill;

		public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
		{
			if (IsViolentDeath(damage))
				DamageRules.Gib(Player.Hitbox, new Vector2(hitDirection * 3f, -2f));
		}
	}

	/// <summary>Draws a player's wounds over their body.</summary>
	public class WoundLayer : PlayerDrawLayer
	{
		public override Position GetDefaultPosition() => new AfterParent(PlayerDrawLayers.ArmOverItem);

		public override bool GetDefaultVisibility(PlayerDrawSet drawInfo) =>
			drawInfo.shadow == 0f && drawInfo.drawPlayer.GetModPlayer<DamagePlayer>().Wounds.Count > 0;

		protected override void Draw(ref PlayerDrawSet drawInfo)
		{
			Player p = drawInfo.drawPlayer;
			Texture2D px = TextureAssets.MagicPixel.Value;
			Vector2 center = (drawInfo.Position + new Vector2(p.width / 2f, p.height / 2f) - Main.screenPosition).Floor();
			Color light = Lighting.GetColor(p.Center.ToTileCoordinates());
			foreach (Vector2 w in p.GetModPlayer<DamagePlayer>().Wounds) {
				Vector2 at = center + new Vector2(w.X * p.direction, w.Y);
				drawInfo.DrawDataCache.Add(new DrawData(px, at, new Rectangle(0, 0, 1, 1), new Color(95, 8, 8).MultiplyRGB(light), 0f, Vector2.Zero, new Vector2(4f), SpriteEffects.None));
				drawInfo.DrawDataCache.Add(new DrawData(px, at + Vector2.One, new Rectangle(0, 0, 1, 1), new Color(150, 20, 20).MultiplyRGB(light), 0f, Vector2.Zero, new Vector2(2f), SpriteEffects.None));
			}
		}
	}
}
