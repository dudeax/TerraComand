using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.Chat;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Net;
using TerraCommand.Common.Units;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Orbital
{
	/// <summary>
	/// The link between the ground and orbit: ordering cargo ships, their cargo (kept on the authority), unloading, loading,
	/// launching and delivery to the stockpile. Also the client-side "pick a landing zone" mode.
	/// </summary>
	public class ShipSystem : ModSystem
	{
		static readonly Dictionary<int, List<Item>> cargo = new();      // authority: npc index -> cargo
		static readonly Dictionary<int, List<Item>> cargoClient = new(); // clients: npc index -> a copy of the cargo (label + hold UI)
		static readonly List<Item> transitOnLoad = new();               // cargo of ships in flight when the world was saved

		static bool IsClient => Main.netMode == NetmodeID.MultiplayerClient;
		public static int Delivered;                                     // ships that reached orbit (tests)

		public override void ClearWorld()
		{
			cargo.Clear();
			cargoClient.Clear();
			PickingLz = false;
		}

		public static int CargoCountClient(int npc) => CargoView(npc).Sum(i => i.stack);

		/// <summary>The cargo as this machine knows it (the real list on the authority, a synced copy on clients).</summary>
		public static IReadOnlyList<Item> CargoView(int npc) =>
			(Main.netMode == NetmodeID.MultiplayerClient ? cargoClient : cargo).TryGetValue(npc, out var c) ? c : Array.Empty<Item>();

		// ------------------------------------------------------------ the hold, opened by a body standing by the ship

		/// <summary>Take cargo item <paramref name="index"/> into the player's pack (authority does it; the item goes to the player).</summary>
		public static void RequestTake(Player p, int npc, int index)
		{
			if (IsClient) {
				ModPacket pk = PacketHandler.Create(PacketType.ShipHold);
				pk.Write((short)npc);
				pk.Write(true);
				pk.Write((short)index);
				pk.Send();
				return;
			}
			Item got = DoTake(p, npc, index);
			if (got != null)
				Give(p, got);
		}

		/// <summary>Put an item from the player into the hold. The caller has already removed it from the player.</summary>
		public static void RequestPut(Player p, int npc, Item item)
		{
			if (item.IsAir)
				return;
			if (IsClient) {
				ModPacket pk = PacketHandler.Create(PacketType.ShipHold);
				pk.Write((short)npc);
				pk.Write(false);
				ItemIO.Send(item, pk, writeStack: true);
				pk.Send();
				return;
			}
			DoPut(p, npc, item);
		}

		static bool CanUseHold(Player p, int npc) =>
			npc >= 0 && npc < Main.maxNPCs && Main.npc[npc].active && Main.npc[npc].ModNPC is CargoShip s && s.DoorOpen
			&& s.Team == p.team && Vector2.Distance(p.Center, s.NPC.Center) < HoldReach;

		public const float HoldReach = 16 * 6;

		static Item DoTake(Player p, int npc, int index)
		{
			if (!CanUseHold(p, npc) || !cargo.TryGetValue(npc, out var items) || index < 0 || index >= items.Count)
				return null;
			Item it = items[index];
			items.RemoveAt(index);
			SendCargo(npc);
			return it;
		}

		static void DoPut(Player p, int npc, Item item)
		{
			if (!CanUseHold(p, npc)) {
				Give(p, item);   // not allowed: back to the player
				return;
			}
			if (!cargo.TryGetValue(npc, out var items))
				cargo[npc] = items = new List<Item>();
			// stack onto what's there
			foreach (Item have in items) {
				if (item.IsAir)
					break;
				if (have.type == item.type && have.prefix == item.prefix && have.stack < have.maxStack) {
					int move = Math.Min(item.stack, have.maxStack - have.stack);
					have.stack += move;
					item.stack -= move;
				}
			}
			if (item.stack > 0)
				items.Add(item);
			SendCargo(npc);
		}

		/// <summary>Hand an item to a player: straight into their pack (or at their feet if it's full).</summary>
		static void Give(Player p, Item item)
		{
			if (Main.netMode == NetmodeID.Server) {
				ModPacket pk = PacketHandler.Create(PacketType.ShipGive);
				ItemIO.Send(item, pk, writeStack: true);
				pk.Send(p.whoAmI);
				return;
			}
			Item left = p.GetItem(p.whoAmI, item, GetItemSettings.InventoryUIToInventorySettings);
			if (!left.IsAir)
				p.QuickSpawnItem(p.GetSource_FromThis(), left, left.stack);
			SoundEngine.PlaySound(SoundID.Grab, p.Center);
		}

		public static IEnumerable<CargoShip> Ships() =>
			Main.npc.Where(n => n.active && n.ModNPC is CargoShip).Select(n => (CargoShip)n.ModNPC);

		// ------------------------------------------------------------ ordering

		/// <summary>Order a ship carrying items from the stockpile (may be empty: a pickup) to land at <paramref name="lz"/>.</summary>
		public static void RequestShip(int team, IReadOnlyList<(StockKey key, int count)> manifest, Vector2 lz)
		{
			if (IsClient) {
				ModPacket p = PacketHandler.Create(PacketType.OrderShip);
				p.Write((byte)team);
				p.Write(manifest.Count);
				foreach (var (k, n) in manifest) {
					p.Write(k.Type); p.Write(k.Prefix); p.Write(n);
				}
				p.WriteVector2(lz);
				p.Send();
				return;
			}
			DoShip(team, manifest, lz);
		}

		static void DoShip(int team, IReadOnlyList<(StockKey key, int count)> manifest, Vector2 lz)
		{
			var items = new List<Item>();
			foreach (var (k, n) in manifest) {
				int amount = (int)Math.Min(n, OrbitalStockpile.Count(k));
				if (amount <= 0 || !OrbitalStockpile.TryTake(k, amount))
					continue;
				int maxStack = Math.Max(1, ContentSamples.ItemsByType[k.Type].maxStack);
				while (amount > 0) {
					var it = new Item(k.Type, Math.Min(amount, maxStack));
					if (k.Prefix > 0)
						it.Prefix(k.Prefix);
					items.Add(it);
					amount -= it.stack;
				}
			}
			// it comes in from high above the landing zone
			float x = Math.Clamp(lz.X, 16 * 60, Main.maxTilesX * 16 - 16 * 60);
			float y = Math.Max(16 * 45f, lz.Y - 1500f);
			int idx = NPC.NewNPC(Entity.GetSource_NaturalSpawn(), (int)x, (int)y, ModContent.NPCType<CargoShip>());
			if (idx < 0 || idx >= Main.maxNPCs)
				return;
			var ship = (CargoShip)Main.npc[idx].ModNPC;
			ship.Team = team;
			ship.State = ShipState.Descending;
			cargo[idx] = items;
			SendCargo(idx);
			Main.npc[idx].netUpdate = true;
			Testing.AutoTest.Trace($"ship #{idx} ordered to {lz.ToTileCoordinates()} with {items.Sum(i => i.stack)} items");
		}

		/// <summary>A new team's first delivery: two soldier bodies landing next to the brain.</summary>
		public static void StarterShip(int team, Vector2 near, int dir)
		{
			if (IsClient)
				return;
			OrbitalStockpile.Change(new StockKey(ModContent.ItemType<Content.Items.CloneBody>(), 0), 2);
			DoShip(team, new[] { (new StockKey(ModContent.ItemType<Content.Items.CloneBody>(), 0), 2) }, near + new Vector2(dir * 16 * 7, 0));
		}

		// ------------------------------------------------------------ on the ground

		/// <summary>Authority: put the cargo on the ground next to the ship; ordered bodies step out as soldiers.</summary>
		public static void Unload(CargoShip ship)
		{
			if (!cargo.TryGetValue(ship.NPC.whoAmI, out var items) || items.Count == 0)
				return;
			int side = 1;
			foreach (Item it in items) {
				side = -side;
				Vector2 at = ship.NPC.Bottom + new Vector2(side * (ship.NPC.width / 2f + 14f + Main.rand.Next(0, 12)), -2f);
				if (it.ModItem is Content.Items.BodyItem body) {
					for (int i = 0; i < it.stack; i++)
						UnitSystem.SpawnFresh(at + new Vector2(side * i * 14f, 0f), ship.Team, body.Kind, body.BuiltInWeapon);
					continue;
				}
				int n = Item.NewItem(ship.NPC.GetSource_FromAI(), at - new Vector2(8, 16), 16, 16, it.type, it.stack, false, it.prefix, noGrabDelay: true);
				if (Main.netMode == NetmodeID.Server)
					NetMessage.SendData(MessageID.SyncItem, -1, -1, null, n, 1f);
			}
			items.Clear();
			SendCargo(ship.NPC.whoAmI);
		}

		/// <summary>Authority: anything thrown into the bay, and idle team bodies standing in it, go into the cargo.</summary>
		public static void LoadFromBay(CargoShip ship)
		{
			if (!cargo.TryGetValue(ship.NPC.whoAmI, out var items))
				cargo[ship.NPC.whoAmI] = items = new List<Item>();
			Rectangle bay = ship.Bay;
			bool changed = false;
			for (int i = 0; i < Main.maxItems; i++) {
				Item it = Main.item[i];
				if (!it.active || it.IsAir || !bay.Intersects(it.Hitbox))
					continue;
				items.Add(it.Clone());
				it.TurnToAir();
				it.active = false;
				if (Main.netMode == NetmodeID.Server)
					NetMessage.SendData(MessageID.SyncItem, -1, -1, null, i);
				changed = true;
			}
			foreach (CloneUnit u in UnitSystem.TeamUnits(ship.Team).ToList()) {
				if (u.Loadout.Kind == Actors.ActorKind.Brain || !bay.Intersects(u.NPC.Hitbox))
					continue;
				// the body goes up (as a body, to be ordered again), with everything it carried
				items.Add(new Item(Content.Items.BodyItem.ItemFor(u.Loadout.Kind)));
				foreach (Item[] arr in new[] { u.Loadout.Inventory, u.Loadout.Armor, u.Loadout.MiscEquips })
					foreach (Item it in arr)
						if (!it.IsAir)
							items.Add(it.Clone());
				UnitSystem.DespawnUnit(u.NPC);
				changed = true;
			}
			if (changed) {
				SendCargo(ship.NPC.whoAmI);
				Terraria.Audio.SoundEngine.PlaySound(SoundID.Grab, ship.NPC.Center);
			}
		}

		/// <summary>Authority: the ship left the atmosphere; its cargo joins the stockpile.</summary>
		public static void ReachedOrbit(CargoShip ship)
		{
			int n = 0;
			if (cargo.Remove(ship.NPC.whoAmI, out var items))
				foreach (Item it in items) {
					OrbitalStockpile.Add(it);
					n += it.stack;
				}
			Delivered++;
			Testing.AutoTest.Trace($"ship #{ship.NPC.whoAmI} reached orbit with {n} items");
			ship.NPC.active = false;
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendData(MessageID.SyncNPC, number: ship.NPC.whoAmI);
			if (n > 0)
				Announce($"Cargo ship reached orbit: {n} item{(n == 1 ? "" : "s")} added to the stockpile.", Color.LightBlue);
		}

		public static void Destroyed(CargoShip ship)
		{
			cargo.Remove(ship.NPC.whoAmI);
			Testing.AutoTest.Trace($"ship #{ship.NPC.whoAmI} destroyed");
			Announce("A cargo ship was destroyed. Its cargo is lost.", Color.OrangeRed);
		}

		static void Announce(string text, Color c)
		{
			if (Main.netMode == NetmodeID.Server)
				ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral(text), c);
			else if (!Main.dedServ)
				Main.NewText(text, c);
		}

		// ------------------------------------------------------------ launching

		/// <summary>Send the team's landed ship nearest to the player back to orbit.</summary>
		public static void RequestLaunch(Player p)
		{
			if (IsClient) {
				ModPacket pk = PacketHandler.Create(PacketType.ShipLaunch);
				pk.Write((byte)p.team);
				pk.WriteVector2(p.Center);
				pk.Send();
				return;
			}
			DoLaunch(p.team, p.Center);
		}

		static void DoLaunch(int team, Vector2 near)
		{
			CargoShip ship = Ships().Where(s => s.Team == team && s.DoorOpen)
				.OrderBy(s => Vector2.DistanceSquared(s.NPC.Center, near)).FirstOrDefault();
			ship?.Launch();
		}

		public static bool HasLandedShip(int team) => Ships().Any(s => s.Team == team && s.DoorOpen);

		// ------------------------------------------------------------ net

		static void SendCargo(int npc)
		{
			if (Main.netMode != NetmodeID.Server)
				return;
			cargo.TryGetValue(npc, out var items);
			ModPacket p = PacketHandler.Create(PacketType.ShipCargo);
			p.Write((short)npc);
			int n = Math.Min(items?.Count ?? 0, 200);
			p.Write((short)n);
			for (int k = 0; k < n; k++)
				ItemIO.Send(items[k], p, writeStack: true);
			p.Send();
		}

		public static void Receive(PacketType type, BinaryReader r, int from)
		{
			switch (type) {
				case PacketType.OrderShip: {
					int team = r.ReadByte();
					int n = Math.Min(r.ReadInt32(), 50);
					var manifest = new List<(StockKey, int)>(n);
					for (int i = 0; i < n; i++)
						manifest.Add((new StockKey(r.ReadInt32(), r.ReadInt32()), r.ReadInt32()));
					Vector2 lz = r.ReadVector2();
					if (Main.netMode == NetmodeID.Server && team == Main.player[from].team)
						DoShip(team, manifest, lz);
					break;
				}
				case PacketType.ShipLaunch: {
					int team = r.ReadByte();
					Vector2 near = r.ReadVector2();
					if (Main.netMode == NetmodeID.Server && team == Main.player[from].team)
						DoLaunch(team, near);
					break;
				}
				case PacketType.ShipCargo: {
					int npc = r.ReadInt16();
					int n = r.ReadInt16();
					var list = new List<Item>(n);
					for (int k = 0; k < n; k++)
						list.Add(ItemIO.Receive(r, readStack: true));
					cargoClient[npc] = list;
					break;
				}
				case PacketType.ShipHold: {
					int npc = r.ReadInt16();
					bool take = r.ReadBoolean();
					if (take) {
						int index = r.ReadInt16();
						if (Main.netMode == NetmodeID.Server) {
							Item got = DoTake(Main.player[from], npc, index);
							if (got != null)
								Give(Main.player[from], got);
						}
					}
					else {
						Item item = ItemIO.Receive(r, readStack: true);
						if (Main.netMode == NetmodeID.Server)
							DoPut(Main.player[from], npc, item);
					}
					break;
				}
				case PacketType.ShipGive: {
					Item item = ItemIO.Receive(r, readStack: true);
					if (Main.netMode == NetmodeID.MultiplayerClient)
						Give(Main.LocalPlayer, item);
					break;
				}
			}
		}

		// ------------------------------------------------------------ persistence: cargo in flight goes back to orbit

		public override void SaveWorldData(TagCompound tag)
		{
			var list = new List<TagCompound>();
			foreach (CargoShip s in Ships())
				if (cargo.TryGetValue(s.NPC.whoAmI, out var items))
					foreach (Item it in items)
						list.Add(ItemIO.Save(it));
			tag["transit"] = list;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			transitOnLoad.Clear();
			foreach (TagCompound t in tag.GetList<TagCompound>("transit"))
				transitOnLoad.Add(ItemIO.Load(t));
		}

		public override void PostUpdateWorld()
		{
			if (transitOnLoad.Count == 0)
				return;
			foreach (Item it in transitOnLoad)
				OrbitalStockpile.Add(it);
			transitOnLoad.Clear();
		}

		// ------------------------------------------------------------ client: picking a landing zone

		public static bool PickingLz;
		static List<(StockKey, int)> pendingManifest = new();

		public static void BeginPickLz(List<(StockKey, int)> manifest)
		{
			pendingManifest = manifest;
			PickingLz = true;
			Main.NewText("Choose a landing zone: left-click to confirm, right-click to cancel.", Color.LightBlue);
		}

		public override void PostUpdateInput()
		{
			if (!PickingLz || Main.gameMenu)
				return;
			if (Main.mouseLeft && Main.mouseLeftRelease) {
				RequestShip(Main.LocalPlayer.team, pendingManifest, Main.MouseWorld);
				Main.NewText("Cargo ship inbound.", Color.Orange);
				PickingLz = false;
				Main.mouseLeftRelease = false;
			}
			else if (Main.mouseRight && Main.mouseRightRelease) {
				PickingLz = false;
				Main.mouseRightRelease = false;
			}
		}

		public override void PostDrawInterface(SpriteBatch sb)
		{
			if (!PickingLz)
				return;
			// a marker column under the cursor: where the ship will come down
			int x = Main.mouseX;
			for (int y = 0; y < Main.screenHeight; y += 12)
				sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle(x - 1, y, 2, 6), Color.Orange * 0.7f);
			Utils.DrawBorderString(sb, "Landing zone", new Vector2(x + 8, Main.mouseY - 24), Color.Orange, 0.8f);
		}
	}

	/// <summary>No firing while choosing a landing zone (the click confirms it).</summary>
	public class ShipPlayer : ModPlayer
	{
		public override void SetControls()
		{
			if (ShipSystem.PickingLz && Player.whoAmI == Main.myPlayer) {
				Player.controlUseItem = false;
				Player.controlUseTile = false;
			}
		}
	}
}
