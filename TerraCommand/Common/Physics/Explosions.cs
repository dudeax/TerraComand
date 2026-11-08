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
		public static void Blast(Vector2 center, float radius, float strength)
		{
			// The local player's own body (each client moves its own player).
			if (!Main.dedServ) {
				Player lp = Main.LocalPlayer;
				if (Vector2.Distance(lp.Center, center) < radius)
					Testing.AutoTest.Trace($"blast catches local player: active={lp.active} dead={lp.dead} life={lp.statLife}");
				if (lp.active && !lp.dead)
					lp.GetModPlayer<BodyPhysicsPlayer>().ApplyImpulse(Impulse(center, lp.Center, radius, strength));
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
				float resist = n.ModNPC is CloneUnit ? 1f : Math.Min(1f, n.knockBackResist * 1.5f);
				n.velocity += imp * resist;
				n.netUpdate = true;
			}
		}

		/// <summary>
		/// Marks everything inside a blast (on every machine) so that deaths in the next moments gib. Must run
		/// before vanilla applies the explosion's damage, i.e. from PreKill.
		/// </summary>
		public static void MarkBlasted(Vector2 center, float radius)
		{
			foreach (Player p in Main.ActivePlayers)
				if (Vector2.Distance(p.Center, center) < radius)
					p.GetModPlayer<Combat.DamagePlayer>().BlastedTimer = 15;
			foreach (NPC n in Main.ActiveNPCs)
				if (Vector2.Distance(n.Center, center) < radius + Math.Max(n.width, n.height) / 2f)
					n.GetGlobalNPC<Combat.DamageGlobalNPC>().BlastedTimer = 15;
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
