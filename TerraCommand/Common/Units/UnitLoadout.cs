using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader.IO;

namespace TerraCommand.Common.Units
{
	/// <summary>
	/// Everything that belongs to a body: carried items, equipment, current health and looks.
	/// Progression stats (max life/mana from crystals, permanent upgrades) belong to the player
	/// controlling it, so they are never stored here.
	/// </summary>
	public class UnitLoadout
	{
		public const int InvSize = 58; // excludes the mouse slot (index 58)
		public Item[] Inventory = Empty(InvSize);
		public Item[] Armor = Empty(20);
		public Item[] Dye = Empty(10);
		public Item[] MiscEquips = Empty(5);
		public Item[] MiscDyes = Empty(5);
		public int Life = 100;
		public int Selected;

		// appearance
		public bool Male = true;
		public int SkinVariant, Hair;
		public Color HairColor = new(215, 90, 55), SkinColor = new(255, 125, 90), EyeColor = new(105, 90, 75),
			ShirtColor = new(175, 165, 140), UnderShirtColor = new(160, 180, 215), PantsColor = new(255, 230, 175), ShoeColor = new(160, 105, 60);

		static Item[] Empty(int n)
		{
			var a = new Item[n];
			for (int i = 0; i < n; i++)
				a[i] = new Item();
			return a;
		}

		public Item Held => Inventory[Math.Clamp(Selected, 0, InvSize - 1)];

		public int Defense
		{
			get
			{
				int d = 0;
				for (int i = 0; i < 10; i++)
					if (!Armor[i].IsAir)
						d += Armor[i].defense;
				return d;
			}
		}

		/// <summary>A fresh body grown from the given player's genome (looks only, no items).</summary>
		public static UnitLoadout NewBodyFor(Player p)
		{
			var l = new UnitLoadout();
			l.CopyAppearance(p);
			l.Life = p.statLifeMax2;
			return l;
		}

		public static UnitLoadout FromPlayer(Player p)
		{
			var l = new UnitLoadout();
			for (int i = 0; i < InvSize; i++) l.Inventory[i] = p.inventory[i].Clone();
			for (int i = 0; i < 20; i++) l.Armor[i] = p.armor[i].Clone();
			for (int i = 0; i < 10; i++) l.Dye[i] = p.dye[i].Clone();
			for (int i = 0; i < 5; i++) { l.MiscEquips[i] = p.miscEquips[i].Clone(); l.MiscDyes[i] = p.miscDyes[i].Clone(); }
			l.Life = p.statLife;
			l.Selected = Math.Clamp(p.selectedItem, 0, 9);
			l.CopyAppearance(p);
			return l;
		}

		void CopyAppearance(Player p)
		{
			Male = p.Male; SkinVariant = p.skinVariant; Hair = p.hair;
			HairColor = p.hairColor; SkinColor = p.skinColor; EyeColor = p.eyeColor; ShirtColor = p.shirtColor;
			UnderShirtColor = p.underShirtColor; PantsColor = p.pantsColor; ShoeColor = p.shoeColor;
		}

		public void ApplyAppearance(Player p)
		{
			p.Male = Male; p.skinVariant = SkinVariant; p.hair = Hair;
			p.hairColor = HairColor; p.skinColor = SkinColor; p.eyeColor = EyeColor; p.shirtColor = ShirtColor;
			p.underShirtColor = UnderShirtColor; p.pantsColor = PantsColor; p.shoeColor = ShoeColor;
		}

		/// <summary>Move this body's items into the player. The player's own appearance is kept.</summary>
		public void ApplyTo(Player p)
		{
			for (int i = 0; i < InvSize; i++) p.inventory[i] = Inventory[i].Clone();
			for (int i = 0; i < 20; i++) p.armor[i] = Armor[i].Clone();
			for (int i = 0; i < 10; i++) p.dye[i] = Dye[i].Clone();
			for (int i = 0; i < 5; i++) { p.miscEquips[i] = MiscEquips[i].Clone(); p.miscDyes[i] = MiscDyes[i].Clone(); }
			p.selectedItem = Math.Clamp(Selected, 0, 9);
			p.statLife = Math.Clamp(Life, 1, Math.Max(1, p.statLifeMax2));
		}

		/// <summary>Visual-only copy for rendering a body with a dummy Player.</summary>
		public void ApplyVisuals(Player p)
		{
			ApplyAppearance(p);
			for (int i = 0; i < 20; i++) p.armor[i] = Armor[i];
			for (int i = 0; i < 10; i++) p.dye[i] = Dye[i];
			for (int i = 0; i < 5; i++) { p.miscEquips[i] = MiscEquips[i]; p.miscDyes[i] = MiscDyes[i]; }
			p.inventory[0] = Held;
			p.selectedItem = 0;
		}

		public void DropAll(IEntitySource src, Rectangle area)
		{
			foreach (Item[] arr in new[] { Inventory, Armor, Dye, MiscEquips, MiscDyes }) {
				for (int i = 0; i < arr.Length; i++) {
					if (!arr[i].IsAir)
						Item.NewItem(src, area, arr[i].Clone());
					arr[i] = new Item();
				}
			}
		}

		// ------------------------------------------------------------ serialization

		static void WriteArr(BinaryWriter w, Item[] a) { foreach (Item it in a) ItemIO.Send(it, w, writeStack: true, writeFavorite: true); }
		static void ReadArr(BinaryReader r, Item[] a) { for (int i = 0; i < a.Length; i++) a[i] = ItemIO.Receive(r, readStack: true, readFavorite: true); }

		public void Write(BinaryWriter w, bool visualsOnly = false)
		{
			w.Write(visualsOnly);
			w.Write(Life); w.Write((byte)Selected);
			w.Write(Male); w.Write((byte)SkinVariant); w.Write((short)Hair);
			foreach (Color c in new[] { HairColor, SkinColor, EyeColor, ShirtColor, UnderShirtColor, PantsColor, ShoeColor })
				w.Write(c.PackedValue);
			WriteArr(w, Armor); WriteArr(w, Dye); WriteArr(w, MiscEquips); WriteArr(w, MiscDyes);
			if (visualsOnly)
				ItemIO.Send(Held, w, writeStack: true);
			else
				WriteArr(w, Inventory);
		}

		public static UnitLoadout Read(BinaryReader r)
		{
			var l = new UnitLoadout();
			bool visualsOnly = r.ReadBoolean();
			l.Life = r.ReadInt32(); l.Selected = r.ReadByte();
			l.Male = r.ReadBoolean(); l.SkinVariant = r.ReadByte(); l.Hair = r.ReadInt16();
			var cols = new Color[7];
			for (int i = 0; i < 7; i++) cols[i] = new Color { PackedValue = r.ReadUInt32() };
			(l.HairColor, l.SkinColor, l.EyeColor, l.ShirtColor, l.UnderShirtColor, l.PantsColor, l.ShoeColor) = (cols[0], cols[1], cols[2], cols[3], cols[4], cols[5], cols[6]);
			ReadArr(r, l.Armor); ReadArr(r, l.Dye); ReadArr(r, l.MiscEquips); ReadArr(r, l.MiscDyes);
			if (visualsOnly) {
				l.Selected = 0;
				l.Inventory[0] = ItemIO.Receive(r, readStack: true);
			}
			else
				ReadArr(r, l.Inventory);
			return l;
		}

		static List<TagCompound> SaveArr(Item[] a) { var t = new List<TagCompound>(a.Length); foreach (Item it in a) t.Add(ItemIO.Save(it)); return t; }
		static void LoadArr(TagCompound tag, string key, Item[] a)
		{
			var list = tag.GetList<TagCompound>(key);
			for (int i = 0; i < a.Length && i < list.Count; i++) a[i] = ItemIO.Load(list[i]);
		}

		public TagCompound Save() => new() {
			["inv"] = SaveArr(Inventory), ["armor"] = SaveArr(Armor), ["dye"] = SaveArr(Dye),
			["misc"] = SaveArr(MiscEquips), ["miscDye"] = SaveArr(MiscDyes),
			["life"] = Life, ["sel"] = Selected, ["male"] = Male, ["skin"] = SkinVariant, ["hair"] = Hair,
			["colors"] = new List<Color> { HairColor, SkinColor, EyeColor, ShirtColor, UnderShirtColor, PantsColor, ShoeColor },
		};

		public static UnitLoadout Load(TagCompound tag)
		{
			var l = new UnitLoadout();
			LoadArr(tag, "inv", l.Inventory); LoadArr(tag, "armor", l.Armor); LoadArr(tag, "dye", l.Dye);
			LoadArr(tag, "misc", l.MiscEquips); LoadArr(tag, "miscDye", l.MiscDyes);
			l.Life = tag.GetInt("life"); l.Selected = tag.GetInt("sel"); l.Male = tag.GetBool("male");
			l.SkinVariant = tag.GetInt("skin"); l.Hair = tag.GetInt("hair");
			var cols = tag.GetList<Color>("colors");
			if (cols.Count == 7)
				(l.HairColor, l.SkinColor, l.EyeColor, l.ShirtColor, l.UnderShirtColor, l.PantsColor, l.ShoeColor) = (cols[0], cols[1], cols[2], cols[3], cols[4], cols[5], cols[6]);
			return l;
		}
	}
}
