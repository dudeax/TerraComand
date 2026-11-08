using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Physics
{
	/// <summary>Blast impulses: explosions physically throw bodies, units and enemies.</summary>
	public static class Explosions
	{
		/// <summary>Vanilla blast radius in tiles (the same values vanilla passes to ExplodeTiles).</summary>
		public static int BlastRadiusTiles(Projectile p)
		{
			switch (p.type) {
				case ProjectileID.Dynamite:
				case ProjectileID.StickyDynamite:
				case ProjectileID.BouncyDynamite:
					return 7;
				case ProjectileID.Bomb:
				case ProjectileID.StickyBomb:
				case ProjectileID.BouncyBomb:
				case ProjectileID.BombFish:
				case ProjectileID.ScarabBomb:
				case ProjectileID.DirtBomb:
				case ProjectileID.DirtStickyBomb:
					return 4;
			}
			return Math.Max(3, Math.Max(p.width, p.height) / 32);
		}

		/// <summary>Runs on every machine when an explosive dies; each applies what it has authority over.</summary>
		/// <param name="damage">explosion damage: CC bodies (idle units) take it with distance falloff; vanilla doesn't let a player's
		/// explosives hurt "friendly" NPCs, but in CC a blast hurts everyone in it</param>
		public static void Blast(Vector2 center, float radius, float strength, int damage = 0)
		{
			// The local player's own body (each client moves its own player).
			if (!Main.dedServ) {
				Player lp = Main.LocalPlayer;
				if (Vector2.Distance(lp.Center, center) < radius)
					Testing.AutoTest.Trace($"blast catches local player: active={lp.active} dead={lp.dead} life={lp.statLife}");
				if (lp.active && !lp.dead)
					lp.GetModPlayer<BodyPhysicsPlayer>().ApplyImpulse(Impulse(center, lp.Center, radius, strength) * Actors.CC.BlastScale);
			}
			// NPCs and idle bodies: the server / singleplayer.
			if (Main.netMode == NetmodeID.MultiplayerClient)
				return;
			foreach (NPC n in Main.ActiveNPCs) {
				if (n.boss || n.knockBackResist <= 0f && n.ModNPC is not CloneUnit)
					continue;
				Vector2 imp = Impulse(center, n.Center, radius, strength);
				if (imp == Vector2.Zero)
					continue;
				if (n.ModNPC is CloneUnit unit) {
					unit.Blasted(imp * Actors.CC.BlastScale);   // a CC body: the same blast physics as a controlled one
					float near = 1f - Vector2.Distance(n.Center, center) / radius;
					if (damage > 0 && near > 0f)
						n.SimpleStrikeNPC(Math.Max(1, (int)(damage * near)), Math.Sign(n.Center.X - center.X), noPlayerInteraction: true);
					continue;
				}
				n.velocity += imp * Math.Min(1f, n.knockBackResist * 1.5f);
				n.netUpdate = true;
			}
		}

		/// <summary>
		/// A CC explosive going off (runs on every machine; each applies what it has authority over): the terrain is blown
		/// out, everything in the radius is marked (deaths gib), hurt with distance falloff - the thrower included, as in CC -
		/// and thrown by the blast.
		/// </summary>
		public static void Detonate(Vector2 center, float radius, int damage, int owner, float power = -1f)
		{
			MarkBlasted(center, radius);
			bool authority = Main.netMode != NetmodeID.MultiplayerClient;
			if (authority) {
				Destruction.PixelTerrain.RequestCarve(center, radius * 0.6f, power < 0 ? Destruction.TerrainRules.ExplosivePower : (int)power,
					Destruction.CarveFlags.Explosion);
				foreach (NPC n in Main.ActiveNPCs) {
					if (n.friendly || n.dontTakeDamage)
						continue;
					float near = 1f - Vector2.Distance(n.Center, center) / (radius + Math.Max(n.width, n.height) / 2f);
					if (near > 0f)
						n.SimpleStrikeNPC(Math.Max(1, (int)(damage * near)), Math.Sign(n.Center.X - center.X), knockBack: 0f);
				}
			}
			if (!Main.dedServ) {
				Player lp = Main.LocalPlayer;
				float near = 1f - Vector2.Distance(lp.Center, center) / radius;
				if (lp.active && !lp.dead && near > 0f)
					lp.Hurt(Terraria.DataStructures.PlayerDeathReason.ByCustomReason(Terraria.Localization.NetworkText.FromLiteral($"{lp.name} was blown up.")),
						Math.Max(1, (int)(damage * near)), Math.Sign(lp.Center.X - center.X), knockback: 0f);
				Terraria.Audio.SoundEngine.PlaySound(SoundID.Item14, center);
				for (int i = 0; i < 30; i++)
					Dust.NewDust(center - new Vector2(radius / 3f), (int)(radius / 1.5f), (int)(radius / 1.5f), DustID.Smoke, Main.rand.NextFloat(-5, 5), Main.rand.NextFloat(-5, 5), 100, default, 2f);
				for (int i = 0; i < 20; i++) {
					var d = Dust.NewDustDirect(center - new Vector2(radius / 4f), (int)(radius / 2f), (int)(radius / 2f), DustID.Torch, Main.rand.NextFloat(-6, 6), Main.rand.NextFloat(-6, 6), 100, default, 2.5f);
					d.noGravity = true;
				}
			}
			Blast(center, radius, Tuning.BlastImpulse, damage);
		}

		/// <summary>
		/// Marks everything inside a blast (on every machine) so that deaths in the next moments gib. Must run
		/// before vanilla applies the explosion's damage, i.e. from PreKill.
		/// </summary>
		public static void MarkBlasted(Vector2 center, float radius)
		{
			foreach (Player p in Main.ActivePlayers)
				if (Vector2.Distance(p.Center, center) < radius) {
					p.GetModPlayer<Combat.DamagePlayer>().BlastedTimer = 15;
					p.GetModPlayer<Combat.DamagePlayer>().BlastCenter = center;
				}
			foreach (NPC n in Main.ActiveNPCs)
				if (Vector2.Distance(n.Center, center) < radius + Math.Max(n.width, n.height) / 2f) {
					n.GetGlobalNPC<Combat.DamageGlobalNPC>().BlastedTimer = 15;
					n.GetGlobalNPC<Combat.DamageGlobalNPC>().BlastCenter = center;
				}
		}

		static Vector2 Impulse(Vector2 center, Vector2 target, float radius, float strength)
		{
			Vector2 d = target - center;
			float dist = d.Length();
			if (dist >= radius)
				return Vector2.Zero;
			Vector2 dir = dist < 1f ? -Vector2.UnitY : d / dist;
			dir.Y -= 0.35f; // blasts lift things
			dir.Normalize();
			return dir * strength * (1f - dist / radius);
		}
	}
}
