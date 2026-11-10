using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Physics;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Combat
{
    /// <summary>Authority adapters for persistent CC wound emitters. Damage is collected once per body tick.</summary>
    public static class ActorInjurySystem
    {
        public static bool Flesh(CCActor actor) => actor.Kind is ActorKind.Soldier or ActorKind.Light or ActorKind.Heavy;

        public static void Wound(CCActor actor, int region)
        {
            if (!Flesh(actor) || region < 4 && !actor.Limbs.Has(region)) return;
            // Heavy limbs use Dent Metal Light for entry wounds: zero sustained damage.
            if (actor.Kind == ActorKind.Heavy && region < 4) return;
            actor.Wounds.Add(region,actor.Kind == ActorKind.Heavy && region == 5 ? WoundKind.StrongEntry : WoundKind.Entry);
        }

        public static void Tick(CloneUnit unit)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient || !unit.NPC.active || unit.NPC.life <= 0) return;
            int damage = unit.Actor.Wounds.Tick();
            if (damage == 0) return;
            var npc = unit.NPC;
            // Wound emitter damage is already damage, not another projectile hit. It must not
            // pass through armour, critical hits, immunity or create a second wound.
            npc.life -= damage;
            npc.netUpdate = true;
            if (npc.life <= 0) {
                var remains = npc.GetGlobalNPC<DamageGlobalNPC>();
                bool blasted = remains.BlastedTimer > 0;
                var death = GibSystem.Decide(blasted,damage >= npc.lifeMax * DamageRules.GibOverkill,false,true);
                npc.HitEffect(new NPC.HitInfo { Damage = damage });
                npc.checkDead();
                if (!npc.active && Main.netMode == NetmodeID.Server) {
                    GibSystem.Broadcast(unit.Pose(Color.White), death, Vector2.Zero, blasted ? remains.BlastCenter : null);
                    NetMessage.SendData(MessageID.SyncNPC, number: npc.whoAmI);
                }
            }
            if (!Main.dedServ) GibSystem.Spray(npc.Center, new Vector2(0, .5f), 2);
        }

        public static void Tick(Player player)
        {
            if (player.whoAmI != Main.myPlayer || player.dead || !player.active) return;
            var actor = player.GetModPlayer<BodyPhysicsPlayer>().Actor;
            int damage = actor.Wounds.Tick();
            if (damage == 0) return;
            player.statLife -= damage;
            if (!Main.dedServ) GibSystem.Spray(player.Center, new Vector2(0, .5f), 2);
            if (player.statLife <= 0) {
                // Keep the normal death/control-transfer hooks, without an immunity window
                // turning an internal wound emitter into an invulnerable external attack.
                player.KillMe(PlayerDeathReason.ByCustomReason(NetworkText.FromLiteral($"{player.name} bled out.")), damage, 0);
            }
            if (Main.netMode == NetmodeID.MultiplayerClient) NetMessage.SendData(MessageID.PlayerLifeMana, number: player.whoAmI);
        }
    }
}
