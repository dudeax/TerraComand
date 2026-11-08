using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Net;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Units
{
	/// <summary>Spawning, swapping, ordering and persisting clone bodies.</summary>
	public class UnitSystem : ModSystem
	{
		record PendingUnit(Vector2 Bottom, int Team, UnitOrder Order, int LifeMax, UnitLoadout Loadout);
		static readonly List<PendingUnit> pending = new();

		static bool IsClient => Main.netMode == NetmodeID.MultiplayerClient;

		public static IEnumerable<CloneUnit> AllUnits()
		{
			int type = ModContent.NPCType<CloneUnit>();
			foreach (NPC n in Main.npc)
				if (n.active && n.type == type && n.ModNPC is CloneUnit u)
					yield return u;
		}

		public static IEnumerable<CloneUnit> TeamUnits(int team) => AllUnits().Where(u => u.Team == team);

		// ------------------------------------------------------------ authority helpers

		static int Spawn(Vector2 bottom, UnitLoadout loadout, int team, UnitOrder order, int lifeMax)
		{
			if (TeamUnits(team).Count() >= TerraCommandConfig.Instance.MaxUnitsPerTeam)
				return -1;
			int idx = NPC.NewNPC(Terraria.Entity.GetSource_NaturalSpawn(), (int)bottom.X, (int)bottom.Y, ModContent.NPCType<CloneUnit>());
			if (idx < 0 || idx >= Main.maxNPCs)
				return -1;
			((CloneUnit)Main.npc[idx].ModNPC).InitFrom(loadout, team, order, lifeMax);
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendData(MessageID.SyncNPC, number: idx);
			return idx;
		}

		/// <summary>Authority: a fresh, empty soldier body (delivered by a ship).</summary>
		public static int SpawnFresh(Vector2 bottom, int team) =>
			Spawn(bottom, new UnitLoadout { Life = 100, Kind = Actors.ActorKind.Soldier }, team, UnitOrder.Sentry, 100);

		/// <summary>Authority: remove an idle body (e.g. loaded into a ship).</summary>
		public static void DespawnUnit(NPC npc) => Despawn(npc);

		static void Despawn(NPC npc)
		{
			npc.active = false;
			npc.life = 0;
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendData(MessageID.SyncNPC, number: npc.whoAmI);
		}

		// ------------------------------------------------------------ deploy a new body

		public static void RequestDeploy(Player p, Vector2 bottom)
		{
			var loadout = UnitLoadout.NewBodyFor(p);
			if (IsClient) {
				ModPacket pk = PacketHandler.Create(PacketType.DeployUnit);
				pk.WriteVector2(bottom);
				pk.Write(p.statLifeMax2);
				loadout.Write(pk);
				pk.Send();
				return;
			}
			Spawn(bottom, loadout, p.team, UnitOrder.Sentry, p.statLifeMax2);
		}

		// ------------------------------------------------------------ swapping

		/// <summary>
		/// Move the player's control into the unit. If <paramref name="leaveBody"/> the player's current
		/// body (items + health) becomes an idle unit where it stands; otherwise it is discarded (death).
		/// </summary>
		public static void RequestSwap(Player p, CloneUnit target, bool leaveBody)
		{
			if (IsClient) {
				ModPacket pk = PacketHandler.Create(PacketType.SwapRequest);
				pk.Write((short)target.NPC.whoAmI);
				pk.Write(leaveBody);
				pk.WriteVector2(p.Bottom);
				pk.Write(p.statLifeMax2);
				UnitLoadout.FromPlayer(p).Write(pk);
				pk.Send();
				return;
			}
			UnitLoadout mine = leaveBody ? UnitLoadout.FromPlayer(p) : null;
			if (ServerSwap(p.team, target.NPC.whoAmI, mine, p.Bottom, p.statLifeMax2, out UnitLoadout got, out Vector2 at))
				ApplySwap(p, got, at);
		}

		static bool ServerSwap(int team, int npcIndex, UnitLoadout mine, Vector2 myBottom, int myLifeMax, out UnitLoadout got, out Vector2 at)
		{
			got = null;
			at = default;
			if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
				return false;
			NPC npc = Main.npc[npcIndex];
			if (!npc.active || npc.ModNPC is not CloneUnit unit || unit.Team != team)
				return false;
			got = unit.Loadout;
			got.Life = npc.life;
			at = npc.Bottom;
			UnitOrder order = unit.Order;
			Despawn(npc);
			if (mine != null)
				Spawn(myBottom, mine, team, order, myLifeMax);
			return true;
		}

		static void ApplySwap(Player p, UnitLoadout got, Vector2 bottom)
		{
			got.ApplyTo(p);
			p.Bottom = bottom;
			p.velocity = Vector2.Zero;
			p.GetModPlayer<Physics.BodyPhysicsPlayer>().SkipImpactCheck();
			p.fallStart = (int)(p.position.Y / 16f);
			p.immune = true;
			p.immuneTime = 30;
			if (p.whoAmI == Main.myPlayer) {
				Terraria.Audio.SoundEngine.PlaySound(SoundID.Item6, p.Center);
				for (int i = 0; i < 20; i++)
					Dust.NewDust(p.position, p.width, p.height, DustID.MagicMirror, 0, -1f);
			}
		}

		// ------------------------------------------------------------ orders

		public static void RequestOrders(Player p, UnitOrder order)
		{
			if (IsClient) {
				ModPacket pk = PacketHandler.Create(PacketType.SetOrders);
				pk.Write((byte)p.team);
				pk.Write((byte)order);
				pk.Send();
				return;
			}
			SetOrders(p.team, order);
		}

		static void SetOrders(int team, UnitOrder order)
		{
			foreach (CloneUnit u in TeamUnits(team)) {
				u.Order = order;
				u.NPC.netUpdate = true;
			}
		}

		// ------------------------------------------------------------ packets

		internal static void HandlePacket(PacketType type, BinaryReader r, int from)
		{
			switch (type) {
				case PacketType.DeployUnit: {
					Vector2 bottom = r.ReadVector2();
					int lifeMax = r.ReadInt32();
					UnitLoadout l = UnitLoadout.Read(r);
					if (Main.netMode == NetmodeID.Server)
						Spawn(bottom, l, Main.player[from].team, UnitOrder.Sentry, lifeMax);
					break;
				}
				case PacketType.SwapRequest: {
					int npc = r.ReadInt16();
					bool leave = r.ReadBoolean();
					Vector2 bottom = r.ReadVector2();
					int lifeMax = r.ReadInt32();
					UnitLoadout mine = UnitLoadout.Read(r);
					if (Main.netMode != NetmodeID.Server)
						break;
					if (ServerSwap(Main.player[from].team, npc, leave ? mine : null, bottom, lifeMax, out var got, out var at)) {
						ModPacket pk = PacketHandler.Create(PacketType.SwapGranted);
						pk.WriteVector2(at);
						got.Write(pk);
						pk.Send(from);
					}
					break;
				}
				case PacketType.SwapGranted: {
					Vector2 at = r.ReadVector2();
					UnitLoadout got = UnitLoadout.Read(r);
					if (Main.netMode == NetmodeID.MultiplayerClient)
						ApplySwap(Main.LocalPlayer, got, at);
					break;
				}
				case PacketType.SetOrders: {
					int team = r.ReadByte();
					var order = (UnitOrder)r.ReadByte();
					if (Main.netMode == NetmodeID.Server && team == Main.player[from].team)
						SetOrders(team, order);
					break;
				}
			}
		}

		// ------------------------------------------------------------ persistence

		public override void ClearWorld() => pending.Clear();

		public override void SaveWorldData(TagCompound tag)
		{
			var list = new List<TagCompound>();
			foreach (CloneUnit u in AllUnits()) {
				u.Loadout.Life = u.NPC.life;
				list.Add(new TagCompound {
					["pos"] = u.NPC.Bottom, ["team"] = u.Team, ["order"] = (int)u.Order,
					["lifeMax"] = u.NPC.lifeMax, ["loadout"] = u.Loadout.Save(),
				});
			}
			tag["units"] = list;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			pending.Clear();
			foreach (var t in tag.GetList<TagCompound>("units"))
				pending.Add(new PendingUnit(t.Get<Vector2>("pos"), t.GetInt("team"), (UnitOrder)t.GetInt("order"),
					t.GetInt("lifeMax"), UnitLoadout.Load(t.GetCompound("loadout"))));
		}

		public override void PostUpdateWorld()
		{
			if (pending.Count == 0)
				return;
			foreach (var u in pending)
				Spawn(u.Bottom, u.Loadout, u.Team, u.Order, u.LifeMax);
			pending.Clear();
		}
	}
}
