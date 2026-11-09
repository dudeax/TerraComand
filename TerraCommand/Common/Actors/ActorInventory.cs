using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Physics;

namespace TerraCommand.Common.Actors
{
	/// <summary>
	/// Cortex Command style carrying, replacing Terraria's hotbar. The body holds one item (slot 0); everything else it carries
	/// is a queue (CC's inventory deque): "next item" (mouse wheel / 2) puts the held item at the back and takes the front one,
	/// "previous" (1) does the reverse. Weapons, tools and gear are picked up deliberately (F, the item nearest the hands) and
	/// dropped (G); materials, ammo, coins and potions are still collected by walking over them, as mining needs. What a body
	/// carries has mass, and a heavier body walks and jets more sluggishly (CC: forces act on the whole mass).
	/// </summary>
	public class ActorInventory : ModPlayer
	{
		public const int Held = 0;
		const int QueueEnd = 50;      // slots 1..49 are the carried queue (50..57 stay coins/ammo)
		public const float Reach = 34f;

		/// <summary>The item F would pick up (local player, refreshed each tick).</summary>
		public int ItemInReach = -1;
		/// <summary>Ticks left to show the inventory list after switching (CC's equip HUD).</summary>
		public int EquipHudTicks;
		public float CarriedMass;

		bool wantNext, wantPrev, wantPickup, wantDrop;

		// ------------------------------------------------------------ input

		public override void ProcessTriggers(TriggersSet triggers)
		{
			if (ActorControlState.Blocked)
				return;
			foreach (string action in new[] { "SmartCursor", "SmartSelect", "QuickMount", "QuickHeal", "QuickMana", "QuickBuff", "Grapple" })
				triggers.KeyStatus[action] = false;
			// CC: mouse wheel / 1 / 2 change the held item; Terraria's hotbar selection is switched off
			int scroll = PlayerInput.ScrollWheelDelta / 120;
			if (scroll < 0 || triggers.KeyStatus["Hotbar2"] && !wasHotbar2)
				wantNext = true;
			if (scroll > 0 || triggers.KeyStatus["Hotbar1"] && !wasHotbar1)
				wantPrev = true;
			wasHotbar1 = triggers.KeyStatus["Hotbar1"];
			wasHotbar2 = triggers.KeyStatus["Hotbar2"];
			PlayerInput.ScrollWheelDelta = 0;
			for (int i = 1; i <= 10; i++)
				triggers.KeyStatus["Hotbar" + i] = false;
			triggers.KeyStatus["HotbarPlus"] = false;
			triggers.KeyStatus["HotbarMinus"] = false;

			if (TerraCommandKeybinds.Pickup.JustPressed)
				wantPickup = true;
			if (TerraCommandKeybinds.Drop.JustPressed)
				wantDrop = true;
		}

		bool wasHotbar1, wasHotbar2;

		/// <summary>Requests from tests and the pie menu.</summary>
		public void Next() => wantNext = true;
		public void Prev() => wantPrev = true;
		public void PickUp() => wantPickup = true;
		public void DropHeld() => wantDrop = true;

		// ------------------------------------------------------------ each tick

		/// <summary>An item on the inventory cursor (blocks being placed, potions...) is used the Terraria way.</summary>
		bool UsingCursorItem => !Main.mouseItem.IsAir && Player.whoAmI == Main.myPlayer;

		public override void PreUpdate()
		{
			if (!UsingCursorItem)
				Player.selectedItem = Held;
			if (Player.whoAmI != Main.myPlayer || Player.dead)
				return;
			if (EquipHudTicks > 0)
				EquipHudTicks--;
			bool busy = Player.itemAnimation > 0 || Player.channel;
			if (wantNext && !busy)
				Cycle(+1);
			if (wantPrev && !busy)
				Cycle(-1);
			ItemInReach = FindItemInReach();
			if (wantPickup && !busy) {
				var ship = UI.ShipHoldUI.NearestLandedShip(Player);
				if (ItemInReach >= 0)
					PickUpItem(ItemInReach);
				else if (ship != null)
					UI.ShipHoldUI.Open(ship);   // standing by a landed ship: open its hold
				else
					interactTicks = 2; // nothing in reach: use / open what's under the cursor (doors, chests...)
			}
			if (wantDrop && !busy)
				Drop();
			wantNext = wantPrev = wantPickup = wantDrop = false;
		}

		int interactTicks;

		public override void SetControls()
		{
			if (Player.whoAmI != Main.myPlayer)
				return;
			// RMB belongs to the pie menu; tile interaction happens through F instead
			Player.controlUseTile = interactTicks > 0;
			if (interactTicks > 0)
				interactTicks--;
		}

		public override void PostUpdate()
		{
			if (!UsingCursorItem)
				Player.selectedItem = Held;
			CarriedMass = Mass(Player);
		}

		// ------------------------------------------------------------ the carried queue

		/// <summary>
		/// Is this carried "in the queue" (a device you hold and use: weapon, tool, gadget), rather than loose stock in the
		/// pack? Blocks, potions and materials are stock: placed or used from the inventory cursor, the Terraria way.
		/// </summary>
		public static bool IsDevice(Item item) =>
			!item.IsAir && item.useStyle != ItemUseStyleID.None && item.ammo == AmmoID.None && !item.IsACoin
			&& item.createTile < TileID.Dirt && item.createWall <= 0
			&& (!item.consumable || item.damage > 0 || item.ModItem is Content.Items.CloneBody);

		List<int> QueueSlots()
		{
			var list = new List<int>();
			for (int i = 1; i < QueueEnd; i++)
				if (IsDevice(Player.inventory[i]))
					list.Add(i);
			return list;
		}

		/// <summary>CC's SwapNextInventory / SwapPrevInventory: the held item goes to the back (or front) of the queue.</summary>
		void Cycle(int dir)
		{
			List<int> slots = QueueSlots();
			if (slots.Count == 0 && Player.inventory[Held].IsAir)
				return;
			var queue = slots.Select(i => Player.inventory[i]).ToList();
			Item held = Player.inventory[Held];
			if (dir > 0) {
				if (!held.IsAir)
					queue.Add(held);
				held = queue.Count > 0 ? queue[0] : new Item();
				if (queue.Count > 0)
					queue.RemoveAt(0);
			}
			else {
				if (!held.IsAir)
					queue.Insert(0, held);
				held = queue.Count > 0 ? queue[^1] : new Item();
				if (queue.Count > 0)
					queue.RemoveAt(queue.Count - 1);
			}
			// write back: queue items keep the queue's slots in order; a held item that joined the queue takes a free slot
			var targets = new List<int>(slots);
			for (int i = 1; i < QueueEnd && targets.Count < queue.Count; i++)
				if (Player.inventory[i].IsAir && !targets.Contains(i))
					targets.Add(i);
			targets.Sort();
			foreach (int s in slots)
				Player.inventory[s] = new Item();
			for (int k = 0; k < queue.Count && k < targets.Count; k++)
				Player.inventory[targets[k]] = queue[k];
			Player.inventory[Held] = held;
			EquipHudTicks = 120;
			SoundEngine.PlaySound(SoundID.Grab with { Volume = 0.5f, Pitch = 0.3f }, Player.Center);
			Testing.AutoTest.Trace($"holding {held.Name}");
		}

		// ------------------------------------------------------------ pick up / drop

		/// <summary>Equipment needs a deliberate pick-up (F); loose stock is still grabbed by walking over it.</summary>
		public static bool NeedsDeliberatePickup(Item item) =>
			!item.IsAir && (item.ModItem is Combat.CCThrowable || !item.IsACoin && item.ammo == AmmoID.None && !item.consumable
			&& (item.damage > 0 || item.pick > 0 || item.axe > 0 || item.hammer > 0 || item.accessory
				|| item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0 || item.ModItem is Content.Items.OrbitalConstructor));

		/// <summary>The nearest equipment item within reach of the hands, preferring what's in front.</summary>
		int FindItemInReach()
		{
			var contacts = Player.GetModPlayer<BodyPhysicsPlayer>().Actor.Contacts;
			Vector2 hands = contacts.Initialized ? contacts.Limbs[2].Position : Player.Center + new Vector2(Player.direction * 8f, 0f);
			int best = -1;
			float bestD = float.MaxValue;
			for (int i = 0; i < Main.maxItems; i++) {
				Item it = Main.item[i];
				if (!it.active || !NeedsDeliberatePickup(it))
					continue;
				float d = Vector2.Distance(it.Center, hands);
				if (d < Reach + Math.Max(it.width, it.height) / 2f && d < bestD && ClearReach(hands, it.Center)) {
					bestD = d;
					best = i;
				}
			}
			return best;
		}

		static bool ClearReach(Vector2 from, Vector2 to)
		{
			Vector2 delta = to - from;
			float length = delta.Length();
			if (length < 1f)
				return true;
			for (float step = 2; step < length; step += 2)
				if (Destruction.PixelCollision.BoxSolid(from + delta * (step / length) - Vector2.One, 2, 2))
					return false;
			return true;
		}

		void PickUpItem(int index)
		{
			Item world = Main.item[index];
			// the held item goes to the back of the queue (a free slot); the picked-up one is now in the hands
			Item held = Player.inventory[Held];
			if (!held.IsAir) {
				int free = -1;
				for (int i = 1; i < QueueEnd; i++)
					if (Player.inventory[i].IsAir) {
						free = i;
						break;
					}
				if (free < 0) {
					CombatText.NewText(Player.Hitbox, Color.Gray, "Can't carry any more");
					return;
				}
				Player.inventory[free] = held;
			}
			Player.inventory[Held] = world.Clone();
			world.TurnToAir();
			world.active = false;
			if (Main.netMode == NetmodeID.MultiplayerClient)
				NetMessage.SendData(MessageID.SyncItem, -1, -1, null, index);
			EquipHudTicks = 120;
			SoundEngine.PlaySound(SoundID.Grab, Player.Center);
			Testing.AutoTest.Trace($"picked up {Player.inventory[Held].Name}");
		}

		/// <summary>CC drop: the held item is thrown a little way along the aim; the next one comes into the hands.</summary>
		void Drop()
		{
			Item held = Player.inventory[Held];
			if (held.IsAir)
				return;
			var actor = Player.GetModPlayer<BodyPhysicsPlayer>().Actor;
			float aim = actor.Flipped ? MathHelper.Pi + actor.AimAngle : -actor.AimAngle;
			Vector2 vel = aim.ToRotationVector2() * 3f + new Vector2(0f, -1f) + Player.velocity;
			int idx = Item.NewItem(Player.GetSource_DropAsItem(), Player.Center + new Vector2(Player.direction * 10f, -6f), 16, 16, held.type, held.stack, false, held.prefix, noGrabDelay: false);
			if (idx >= 0 && idx < Main.maxItems) {
				Main.item[idx].velocity = vel;
				Main.item[idx].noGrabDelay = 60;
				if (Main.netMode == NetmodeID.MultiplayerClient)
					NetMessage.SendData(MessageID.SyncItem, -1, -1, null, idx, 1f);
			}
			Player.inventory[Held] = new Item();
			Testing.AutoTest.Trace($"dropped {held.Name}");
			Cycle(+1);
		}

		// ------------------------------------------------------------ mass

		/// <summary>Carried mass in kg (CC: everything has mass; a loaded body is sluggish). Materials weigh by the stack.</summary>
		public static float ItemMass(Item it)
		{
			if (it.IsAir || it.IsACoin)
				return 0f;
			if (it.ModItem is Combat.CCFirearm firearm && firearm.Spec.Mass > 0f)
				return firearm.Spec.Mass * it.stack;
			if (it.ammo != AmmoID.None)
				return 0.02f * it.stack;
			if (it.damage > 0 || it.pick > 0 || it.axe > 0 || it.hammer > 0)
				return (2f + it.width * it.height / 400f) * it.stack;     // a pistol ~3 kg, a big gun or hammer ~6-8 (CC rifle: 11)
			if (it.headSlot >= 0 || it.bodySlot >= 0 || it.legSlot >= 0)
				return 2f + it.defense * 0.6f;
			if (it.accessory)
				return 1f;
			if (it.consumable && it.createTile < TileID.Dirt && it.createWall <= 0)
				return 0.3f * it.stack;
			return 0.05f * it.stack;
		}

		public static float Mass(Player p)
		{
			float m = 0f;
			for (int i = 0; i < 58; i++)
				m += ItemMass(p.inventory[i]);
			for (int i = 0; i < 10; i++)
				m += ItemMass(p.armor[i]);
			return m;
		}
	}

	/// <summary>Equipment lying around isn't vacuumed up: pick it up deliberately (F).</summary>
	public class DeliberatePickupGlobalItem : GlobalItem
	{
		public override bool CanPickup(Item item, Player player) => !ActorInventory.NeedsDeliberatePickup(item);
	}
}
