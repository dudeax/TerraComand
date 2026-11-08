using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Orbital;
using TerraCommand.Content.Items;
using TerraCommand.Content.Items.Devices;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Units
{
	/// <summary>
	/// The AI faction (CC's computer player): its brain digs in to a bunker some way off, it earns money over time and spends
	/// it on dropships of soldiers sent to hunt the players' brain. Destroy its brain and the world is won. Server-authoritative;
	/// off in the config to play without it.
	/// </summary>
	public class EnemyFaction : ModSystem
	{
		/// <summary>The bunker's floor centre (tiles); X &lt; 0 before the faction has arrived.</summary>
		public static Point Bunker = new(-1, -1);
		public static bool Defeated;
		public static long Budget;
		public static int Waves;
		static int ticks, attackTimer;
		static bool brainSeen;

		const int BunkerW = 16, BunkerH = 9;
		static readonly long Income = Item.buyPrice(gold: 1);   // per minute

		static bool Active => TerraCommandConfig.Instance.EnemyFaction && Main.netMode != NetmodeID.MultiplayerClient;

		public override void ClearWorld()
		{
			Bunker = new Point(-1, -1);
			Defeated = brainSeen = false;
			Budget = Waves = ticks = attackTimer = 0;
		}

		public override void SaveWorldData(TagCompound tag)
		{
			if (Bunker.X < 0)
				return;
			tag["enemyBunker"] = new Vector2(Bunker.X, Bunker.Y);
			tag["enemyDefeated"] = Defeated;
			tag["enemyBudget"] = Budget;
			tag["enemyWaves"] = Waves;
			tag["enemyAttackTimer"] = attackTimer;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			if (!tag.ContainsKey("enemyBunker"))
				return;
			Vector2 b = tag.Get<Vector2>("enemyBunker");
			Bunker = new Point((int)b.X, (int)b.Y);
			Defeated = tag.GetBool("enemyDefeated");
			Budget = tag.GetLong("enemyBudget");
			Waves = tag.GetInt("enemyWaves");
			attackTimer = tag.GetInt("enemyAttackTimer");
			brainSeen = true;
		}

		public static bool BrainAlive() => UnitSystem.AllUnits().Any(u => u.Team == CloneUnit.EnemyTeam && u.Loadout.Kind == ActorKind.Brain);

		public override void PostUpdateWorld()
		{
			if (!Active || Defeated || !Main.player.Any(p => p.active && !p.dead))
				return;
			ticks++;
			if (Bunker.X < 0) {
				// it arrives a little while after the players do
				if (ticks > 60 * 20 && !Testing.AutoTest.Enabled)
					Setup(Main.spawnTileX + (Main.spawnTileX < Main.maxTilesX / 2 ? 1 : -1) * 220);
				return;
			}
			if (ticks % 60 == 0) {
				bool alive = BrainAlive();
				if (alive)
					brainSeen = true;
				else if (brainSeen) {
					Defeat();
					return;
				}
			}
			if (ticks % 60 == 0)
				Budget += Income / 60;
			int interval = 60 * 60 * (Waves == 0 ? TerraCommandConfig.Instance.EnemyFirstAttackMinutes : TerraCommandConfig.Instance.EnemyAttackMinutes);
			if (++attackTimer >= interval && !Testing.AutoTest.Enabled) {
				attackTimer = 0;
				Attack(null);
			}
		}

		// ------------------------------------------------------------ setup

		/// <summary>Authority: the enemy brain digs in at this column: a brick bunker on the surface, the brain and two guards.</summary>
		public static void Setup(int x)
		{
			x = Math.Clamp(x, 60, Main.maxTilesX - 60);
			int floor = Surface(x);
			int left = x - BunkerW / 2;
			int door = x < Main.spawnTileX ? 1 : -1;   // the door faces the players' side
			for (int i = left; i < left + BunkerW; i++) {
				for (int j = floor - BunkerH; j < floor; j++) {
					Tile t = Main.tile[i, j];
					t.ClearEverything();
					PixelTerrain.Clear(i, j);
					bool edge = i == left || i == left + BunkerW - 1 || j == floor - BunkerH;
					bool doorway = j >= floor - 4 && (door > 0 ? i == left + BunkerW - 1 : i == left);
					if (edge && !doorway)
						t.ResetToType(TileID.GrayBrick);
					if (!edge)
						t.WallType = WallID.GrayBrick;
				}
				for (int j = floor; j < floor + 3; j++) {
					Main.tile[i, j].ClearEverything();
					Main.tile[i, j].ResetToType(TileID.GrayBrick);
					PixelTerrain.Clear(i, j);
				}
			}
			WorldGen.RangeFrame(left - 1, floor - BunkerH - 1, left + BunkerW + 1, floor + 4);
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendTileSquare(-1, left - 1, floor - BunkerH - 1, BunkerW + 2, BunkerH + 5);
			if (!Main.dedServ)
				Main.sectionManager.SetAllFramedSectionsAsNeedingRefresh();

			Bunker = new Point(x, floor);
			Vector2 floorAt = new(x * 16 + 8, floor * 16);
			UnitSystem.SpawnFresh(floorAt, CloneUnit.EnemyTeam, ActorKind.Brain);
			UnitSystem.SpawnFresh(floorAt + new Vector2(-16 * 4, 0), CloneUnit.EnemyTeam, ActorKind.Soldier, ModContent.ItemType<CoalitionAssaultRifle>());
			UnitSystem.SpawnFresh(floorAt + new Vector2(16 * 4, 0), CloneUnit.EnemyTeam, ActorKind.Soldier, ModContent.ItemType<CoalitionAssaultRifle>());
			brainSeen = true;
			attackTimer = 0;
			Budget = Item.buyPrice(gold: 4);
			string side = x > Main.spawnTileX ? "east" : "west";
			Announce($"An enemy brain has dug in to the {side}. It will send soldiers for your brain: find its bunker and destroy it.", Color.OrangeRed);
			Testing.AutoTest.Trace($"enemy bunker at {Bunker}");
		}

		/// <summary>The first solid tile from the sky down in this column.</summary>
		static int Surface(int x)
		{
			for (int y = 40; y < Main.maxTilesY - 200; y++)
				if (WorldGen.SolidTile(x, y) || Main.tile[x, y].LiquidAmount > 0)
					return y;
			return (int)Main.worldSurface;
		}

		// ------------------------------------------------------------ attacks

		/// <summary>Authority: buy and send a dropship of soldiers at the players' brain (or at <paramref name="near"/>).</summary>
		public static bool Attack(Vector2? near)
		{
			Vector2? target = near ?? Main.player.Where(p => p.active && !p.dead)
				.OrderByDescending(p => p.GetModPlayer<RigPlayer>().Kind == ActorKind.Brain)
				.Select(p => (Vector2?)p.Bottom).FirstOrDefault();
			if (target is not Vector2 t)
				return false;
			// land a fair walk away from the target, on the bunker's side
			int dir = Bunker.X >= 0 && Bunker.X * 16 < t.X ? -1 : 1;
			float lzX = near != null ? t.X : t.X + dir * 16 * Main.rand.Next(30, 46);
			int lzTile = (int)(lzX / 16);
			Vector2 lz = new(lzX, Surface(lzTile) * 16);

			long cost = Economy.CostOf(ShipStyle.Dropship);
			var items = new List<Item>();
			int want = Math.Min(4, 2 + Waves / 2);
			int[] guns = { ModContent.ItemType<CoalitionAssaultRifle>(), ModContent.ItemType<CoalitionSniperRifle>(), ModContent.ItemType<CoalitionCompactRifle>(), ModContent.ItemType<Spinner>() };
			for (int k = 0; k < want; k++) {
				int body = Waves >= 3 && Main.rand.NextBool(3) ? ModContent.ItemType<HeavyBody>()
					: Main.rand.NextBool(3) ? ModContent.ItemType<LightBody>() : ModContent.ItemType<CloneBody>();
				int gun = guns[Main.rand.Next(Math.Min(guns.Length, 2 + Waves / 2))];
				long price = Economy.Price(body) + Economy.Price(gun);
				if (Budget < cost + price && k > 0)
					break;
				cost += price;
				items.Add(new Item(body));
				items.Add(new Item(gun));
			}
			if (Budget < cost && near == null)
				return false;
			Budget = Math.Max(0, Budget - cost);
			Waves++;
			ShipSystem.SpawnShip(CloneUnit.EnemyTeam, items, lz, ShipStyle.Dropship);
			Announce("Enemy dropship inbound!", Color.OrangeRed);
			Testing.AutoTest.Trace($"enemy wave {Waves}: {items.Count / 2} soldiers to {lz.ToTileCoordinates()}");
			return true;
		}

		static void Defeat()
		{
			Defeated = true;
			Announce("The enemy brain is destroyed. You have won this world!", Color.Gold);
			Testing.AutoTest.Trace("enemy faction defeated");
			if (!Main.dedServ)
				Terraria.Audio.SoundEngine.PlaySound(SoundID.Roar);
		}

		static void Announce(string text, Color c)
		{
			if (Main.netMode == NetmodeID.Server)
				ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral(text), c);
			else
				Main.NewText(text, c);
		}
	}
}
