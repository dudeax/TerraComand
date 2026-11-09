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

		public static bool IsExplosive(Projectile p) => ProjectileID.Sets.Explosive[p.type] || p.aiStyle == ProjAIStyleID.Explosive;

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
		public Vector2? BlastCenter;
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
			if(npc.ModNPC is Content.NPCs.CloneUnit unit && unit.Actor.Limbs.At(unit.Actor,npc.Bottom,hp).HasValue)pendingHead=false;
			if (DamageRules.IsExplosive(projectile)) {
				BlastedTimer = 15;            // vanilla deals blast damage before the projectile's Kill hooks
				modifiers.Knockback *= 0f;    // explosions push through blast physics instead (Explosions.Blast)
			}
			if (pendingHead) {
				modifiers.SourceDamage *= DamageRules.HeadshotMultiplier;
				LastHeadshot = true;
				if (!Main.dedServ)
					CombatText.NewText(npc.Hitbox, Color.OrangeRed, "Headshot!", dramatic: false, dot: true);
			}
			else
				LastHeadshot = false;
		}

		public override void OnHitByProjectile(NPC npc,Projectile projectile,NPC.HitInfo hit,int damageDone) {
			if(npc.life>0 && npc.ModNPC is Content.NPCs.CloneUnit unit && unit.Actor.Limbs.At(unit.Actor,npc.Bottom,DamageRules.HitPoint(npc.Hitbox,projectile)) is int limb)
				Physics.CraftPartHits.ActorHit(unit,projectile,limb);
		}

		/// <summary>Whether the most recent projectile hit (on this machine) was a headshot. For tests.</summary>
		public static bool LastHeadshot;

		public override void HitEffect(NPC npc, NPC.HitInfo hit)
		{
			Vector2 push = new(hit.HitDirection * 3f, -2f);
			if (npc.life > 0) {
				if (hit.Damage > 0) {
					DamageRules.AddWound(Wounds, npc.Hitbox, pendingHitPoint, pendingHead, npc.direction == 0 ? 1 : npc.direction);
					GibSystem.Spray(pendingHitPoint ?? npc.Center, new Vector2(hit.HitDirection, -0.3f), 5);
				}
			}
			else {
				bool blasted = BlastedTimer > 0, overkill = hit.Damage >= npc.lifeMax * DamageRules.GibOverkill;
				Vector2? bc = blasted ? BlastCenter : null;
				if (npc.ModNPC is Content.NPCs.CloneUnit unit)
					GibSystem.ActorDeath(unit.Pose(Color.White), GibSystem.Decide(blasted, overkill, pendingHead, true), push, bc);
				else {
					DeathFx fx = GibSystem.Decide(blasted, overkill, pendingHead, DamageRules.HasHead(npc));
					GibSystem.NpcDeath(npc, fx == DeathFx.Corpse ? DeathFx.None : fx, push, bc); // ordinary deaths: vanilla gore
				}
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
		public Vector2? BlastCenter;
		int headshotTicks;               // the last hit was a headshot, recently
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
			if (headshotTicks > 0)
				headshotTicks--;
			if (Player.statLife >= Player.statLifeMax2 && Wounds.Count > 0)
				Wounds.Clear();
		}

		public override void ModifyHitByProjectile(Projectile proj, ref Player.HurtModifiers modifiers)
		{
			Vector2 hp = DamageRules.HitPoint(Player.Hitbox, proj);
			pendingHitPoint = hp;
			pendingHead = DamageRules.InHeadZone(Player.Hitbox, hp);
			var anatomy=Player.GetModPlayer<Physics.BodyPhysicsPlayer>().Actor;
			if(anatomy.Limbs.At(anatomy,Player.Bottom,hp).HasValue)pendingHead=false;
			if (DamageRules.IsExplosive(proj))
				BlastedTimer = 15;
			else {
				// CC: a round pushes a body by its momentum, a little, along its flight - not vanilla's fixed knockback
				// (which the actor physics would carry for tens of tiles)
				modifiers.Knockback *= 0f;
				Player.GetModPlayer<Physics.BodyPhysicsPlayer>().ApplyImpulse(proj.velocity.SafeNormalize(Vector2.Zero) * Math.Min(proj.knockBack * 0.25f, 1.5f));
			}
			if (pendingHead) {
				modifiers.SourceDamage *= DamageRules.HeadshotMultiplier;
				if (!Main.dedServ)
					CombatText.NewText(Player.Hitbox, Color.OrangeRed, "Headshot!", dramatic: false, dot: true);
			}
		}

		public override void OnHurt(Player.HurtInfo info)
		{
			Testing.AutoTest.Trace($"{Player.name} hurt {info.Damage} by {(info.DamageSource.SourceProjectileType > 0 ? Lang.GetProjectileName(info.DamageSource.SourceProjectileType).Value : info.DamageSource.SourceCustomReason ?? "?")} (life {Player.statLife}/{Player.statLifeMax2})");
			if (info.Damage > 0) {
				DamageRules.AddWound(Wounds, Player.Hitbox, pendingHitPoint, pendingHead, Player.direction);
				GibSystem.Spray(pendingHitPoint ?? Player.Center, new Vector2(info.HitDirection, -0.3f), 5);
			}
			if(Player.whoAmI==Main.myPlayer && !Player.dead && pendingHitPoint is Vector2 point) {
				var actor=Player.GetModPlayer<Physics.BodyPhysicsPlayer>().Actor;
				if(actor.Limbs.At(actor,Player.Bottom,point) is int limb && actor.Limbs.Wound(limb)) Physics.LimbSystem.Detach(Player,limb);
			}
			headshotTicks = pendingHead ? 10 : 0;
			pendingHitPoint = null;
			pendingHead = false;
		}

		public bool IsViolentDeath(double damage) => BlastedTimer > 0 || damage >= Player.statLifeMax2 * DamageRules.GibOverkill;

		/// <summary>How this body's death looks: blasted or overkilled bodies gib, headshots decapitate, else a corpse.</summary>
		public DeathFx DeathKind(double damage) =>
			GibSystem.Decide(BlastedTimer > 0, damage >= Player.statLifeMax2 * DamageRules.GibOverkill, headshotTicks > 0, true);

		/// <summary>Leaves this body's remains where it died (locally; <paramref name="broadcast"/> for a control transfer).</summary>
		public void LeaveRemains(double damage, int hitDirection, bool broadcast)
		{
			DeathFx kind = DeathKind(damage);
			Actors.RigPose pose = Player.GetModPlayer<Actors.RigPlayer>().Pose();
			Vector2 push = new Vector2(hitDirection * 3f, -2f);
			Vector2? bc = BlastedTimer > 0 ? BlastCenter : null;
			GibSystem.ActorDeath(pose, kind, push, bc);
			if (broadcast)
				GibSystem.Broadcast(pose, kind, push, bc);
		}

		public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource) =>
			LeaveRemains(damage, hitDirection, broadcast: false);   // every client runs a real death
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
