using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Orbital;
using TerraCommand.Common.UI;
using TerraCommand.Common.Units;

namespace TerraCommand.Common.Testing
{
	/// <summary>
	/// Scripted in-game test driver. Dormant unless the TC_AUTOTEST environment variable lists scenario
	/// files (separated by ':'; see tools/testenv/). For each scenario it restores TC_BASELINE over
	/// TC_WORLD, creates a fresh test character, loads the world, runs the scenario one command per tick,
	/// then quits to the menu without saving and starts the next one - all in a single game session.
	/// Results and screenshots go to TC_OUT (a subfolder per scenario when running several).
	/// </summary>
	public class AutoTest : ModSystem
	{
		public static bool Enabled { get; private set; }
		static string outRoot, worldPath, baselinePath;
		static string[] scenarios;
		static int scenarioIndex = -1, totalFails;
		static StreamWriter summary;
		static bool scenarioDone = true, allDone, quitRequested;
		static int menuSettle;
		static int speed = 1, drawEvery = 1, shotWarmup = 10, frame;

		// per-scenario state
		static string outDir;
		static List<string> lines;
		static int line, waitTicks, ticksInWorld, passes, fails;
		static StreamWriter results;
		static string pendingShot;
		static int shotFrames;   // frames to render at normal speed before capturing (render targets + lighting catch up)

		// read by AutoTestPlayer / AutoTestNPC
		internal static Vector2? AimOffset;
		internal static int UseTicks;
		internal static bool NoSpawns;
		internal static int Probe;
		internal static bool NoPickup;

		static int EnvInt(string name, int def) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : def;

		/// <summary>Logs a line to the test results when the driver is active (no-op otherwise).</summary>
		public static void Trace(string msg)
		{
			if (Enabled)
				Report("TRACE", msg);
		}

		internal static void ProbeLog(Player p, string where)
		{
			if (Probe <= 0)
				return;
			Probe--;
			Report("PROBE", $"{where}: target={Player.tileTargetX},{Player.tileTargetY} mouseWorld={Main.MouseWorld.ToTileCoordinates()} " +
				$"held={p.HeldItem.Name} ctrlUse={p.controlUseItem} release={p.releaseUseItem} anim={p.itemAnimation} time={p.itemTime} " +
				$"mouseInterface={p.mouseInterface} noBuilding={p.noBuilding} focus={Main.hasFocus} playerTile={p.Bottom.ToTileCoordinates()}");
		}

		public override void Load()
		{
			string list = Environment.GetEnvironmentVariable("TC_AUTOTEST");
			if (string.IsNullOrEmpty(list) || Main.dedServ)
				return;
			Enabled = true;
			scenarios = list.Split(':', StringSplitOptions.RemoveEmptyEntries);
			outRoot = Environment.GetEnvironmentVariable("TC_OUT") ?? Path.Combine(Path.GetTempPath(), "tc-autotest");
			worldPath = Environment.GetEnvironmentVariable("TC_WORLD");
			baselinePath = Environment.GetEnvironmentVariable("TC_BASELINE");
			speed = Math.Max(1, EnvInt("TC_SPEED", 10));
			drawEvery = Math.Max(1, EnvInt("TC_DRAW_EVERY", 30));
			shotWarmup = Math.Max(1, EnvInt("TC_SHOT_FRAMES", 10));
			Directory.CreateDirectory(outRoot);
			summary = new StreamWriter(Path.Combine(outRoot, "summary.txt"), append: false) { AutoFlush = true };
			Main.OnTickForThirdPartySoftwareOnly += SessionTick;
			Main.OnPostDraw += PostDraw;
			On_Main.DoUpdate += FastForward;
		}

		public override void Unload()
		{
			if (!Enabled)
				return;
			Main.OnTickForThirdPartySoftwareOnly -= SessionTick;
			Main.OnPostDraw -= PostDraw;
			results?.Dispose();
			summary?.Dispose();
			Enabled = false;
		}

		/// <summary>
		/// Fast-forward: DoUpdate is exactly one game tick, so running it several times per frame speeds the
		/// game up while keeping the simulation identical. Rendering (slow in software GL) is skipped on most
		/// frames, except around screenshots.
		/// </summary>
		static void FastForward(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
		{
			if (scenarioDone || Main.gameMenu || shotFrames > 0 || speed == 1) {
				orig(self, ref gameTime);
				if (shotFrames > 0 && !Main.gameMenu)
					shotFrames--;
				return;
			}
			for (int i = 0; i < speed && !scenarioDone && pendingShot == null; i++)
				orig(self, ref gameTime);
			if (pendingShot == null && ++frame % drawEvery != 0)
				self.SuppressDraw();
		}

		static void Report(string kind, string msg)
		{
			string l = $"[{kind}] {msg}";
			results?.WriteLine(l);
			ModContent.GetInstance<TerraCommand>().Logger.Info("AUTOTEST " + l);
		}

		static string ScenarioName => Path.GetFileNameWithoutExtension(scenarios[scenarioIndex]);

		static void FinishScenario(string why)
		{
			if (scenarioDone)
				return;
			scenarioDone = true;
			Report("INFO", why);
			Report("DONE", $"{passes} passed, {fails} failed");
			results?.Dispose();
			results = null;
			totalFails += fails;
			summary.WriteLine($"{(fails == 0 ? "PASS" : "FAIL")} {ScenarioName}: {passes} passed, {fails} failed");
			if (scenarioIndex + 1 < scenarios.Length) {
				quitRequested = true; // the next scenario starts from the main menu with a fresh world
			}
			else {
				allDone = true;
				File.WriteAllText(Path.Combine(outRoot, "DONE"), totalFails == 0 ? "PASS" : "FAIL");
			}
		}

		// ------------------------------------------------------------ session: menu -> world -> menu

		/// <summary>Runs every update (menu and world): quits finished worlds and starts the next scenario.</summary>
		static void SessionTick()
		{
			if (allDone)
				return;
			if (quitRequested && !Main.gameMenu) {
				quitRequested = false;
				menuSettle = 10;
				WorldGen.JustQuit(); // lands on the world-select menu
				return;
			}
			if (!scenarioDone || quitRequested || !Main.gameMenu)
				return;
			if (scenarioIndex < 0) {
				if (Main.menuMode == 0) // first scenario: wait for the title screen (content fully loaded)
					StartNext();
			}
			else if (--menuSettle <= 0) {
				StartNext();
			}
		}

		static void StartNext()
		{
			scenarioIndex++;
			line = waitTicks = ticksInWorld = passes = fails = 0;
			pendingShot = null;
			shotFrames = 0;
			AimOffset = null;
			UseTicks = Probe = 0;
			NoSpawns = NoPickup = false;
			scenarioDone = false;

			outDir = scenarios.Length == 1 ? outRoot : Path.Combine(outRoot, $"{scenarioIndex + 1:D2}-{ScenarioName}");
			Directory.CreateDirectory(outDir);
			results = new StreamWriter(Path.Combine(outDir, "results.txt"), append: false) { AutoFlush = true };
			Report("INFO", $"scenario {scenarios[scenarioIndex]} ({scenarioIndex + 1}/{scenarios.Length}); speed {speed} ticks/frame");
			try {
				lines = File.ReadAllLines(scenarios[scenarioIndex]).Select(l => l.Split('#')[0].Trim()).ToList();
				Boot();
			}
			catch (Exception e) {
				fails++;
				FinishScenario($"boot failed: {e.Message}");
			}
		}

		/// <summary>Runs once per game tick while in a world.</summary>
		public override void PostUpdateEverything()
		{
			if (!Enabled || scenarioDone || Main.gameMenu)
				return;
			try {
				if (Main.LocalPlayer == null || !Main.LocalPlayer.active)
					return;
				if (ticksInWorld++ < 90) // let the world settle
					return;
				if (Main.netMode == NetmodeID.SinglePlayer) {
					Main.dayTime = true;
					Main.time = Math.Max(Main.time, 16200) % 54000;
				}
				Step();
			}
			catch (Exception e) {
				fails++;
				Report("FAIL", $"line {line}: exception {e.GetType().Name}: {e.Message}");
				ModContent.GetInstance<TerraCommand>().Logger.Error("AUTOTEST exception", e);
				line++;
			}
		}

		static void Boot()
		{
			if (string.IsNullOrEmpty(worldPath))
				throw new InvalidOperationException("TC_WORLD not set");
			if (!string.IsNullOrEmpty(baselinePath)) {
				File.Copy(baselinePath, worldPath, overwrite: true);
				string twld = Path.ChangeExtension(baselinePath, ".twld");
				if (File.Exists(twld))
					File.Copy(twld, Path.ChangeExtension(worldPath, ".twld"), overwrite: true);
			}
			if (!File.Exists(worldPath))
				throw new FileNotFoundException("world not found", worldPath);

			var p = new Player { name = "TCTest" };
			var vanilla = new List<Item> { new(ItemID.CopperShortsword), new(ItemID.CopperPickaxe), new(ItemID.CopperAxe) };
			PlayerLoader.SetStartInventory(p, PlayerLoader.GetStartingItems(p, vanilla, mediumCoreDeath: false));
			// Save, then load back from disk like the character-creation menu does, so every field the
			// game expects (journey powers, etc.) is initialized.
			string path = PlayerFileData.CreateAndSave(p).Path;
			PlayerFileData data = Player.LoadPlayer(path, false);
			data.SetAsActive();

			Main.ActiveWorldFileData = WorldFile.GetAllMetadata(worldPath, false);
			Main.menuMode = 10;
			WorldGen.playWorld();
		}

		// ------------------------------------------------------------ scenario

		static void Step()
		{
			if (waitTicks > 0) { waitTicks--; return; }
			if (UseTicks > 0 || pendingShot != null) return;
			while (line < lines.Count && lines[line].Length == 0)
				line++;
			if (line >= lines.Count) {
				FinishScenario("end of scenario");
				return;
			}
			string[] a = lines[line].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			line++;
			Run(a);
		}

		static Player P => Main.LocalPlayer;
		static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
		static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
		static Point PlayerTile => P.Bottom.ToTileCoordinates();

		static int ItemType(string s)
		{
			if (int.TryParse(s, out int id)) return id;
			if (ItemID.Search.TryGetId(s, out id)) return id;
			if (ModContent.TryFind(s.Contains('/') ? s : "TerraCommand/" + s, out ModItem mi)) return mi.Type;
			throw new ArgumentException("unknown item " + s);
		}

		static void Expect(bool ok, string what)
		{
			if (ok) passes++; else fails++;
			Report(ok ? "PASS" : "FAIL", what);
		}

		static bool Cmp(double v, string op, double n) => op switch {
			">=" => v >= n, "<=" => v <= n, ">" => v > n, "<" => v < n, "==" => v == n, "!=" => v != n,
			_ => throw new ArgumentException("bad op " + op),
		};

		static void Run(string[] a)
		{
			Point t = PlayerTile;
			switch (a[0]) {
				case "wait": waitTicks = I(a[1]); break;
				case "log": Report("INFO", string.Join(' ', a.Skip(1))); break;
				case "quit": FinishScenario("quit"); break;
				case "nospawn": NoSpawns = true; foreach (NPC n in Main.npc) if (n.active && !n.friendly && !n.townNPC) n.active = false; break;
				case "goto": {
					Vector2 to = a[1] == "spawn" ? new Vector2(Main.spawnTileX * 16, Main.spawnTileY * 16) : new Vector2(I(a[1]) * 16, I(a[2]) * 16);
					P.Bottom = to; P.velocity = Vector2.Zero; P.fallStart = (int)(P.position.Y / 16);
					break;
				}
				case "flatten": { // clear a w x h area above the player's feet and lay a dirt floor, for repeatable setups
					int w = I(a[1]), h = I(a[2]);
					for (int x = t.X - w / 2; x <= t.X + w / 2; x++) {
						for (int y = t.Y - h; y < t.Y; y++) { Main.tile[x, y].ClearEverything(); PixelTerrain.Clear(x, y); }
						for (int y = t.Y; y < t.Y + 6; y++) { Main.tile[x, y].ClearEverything(); Main.tile[x, y].ResetToType(TileID.Dirt); PixelTerrain.Clear(x, y); }
					}
					WorldGen.RangeFrame(t.X - w / 2 - 1, t.Y - h - 1, t.X + w / 2 + 1, t.Y + 7);
					Main.sectionManager.SetAllFramedSectionsAsNeedingRefresh();
					break;
				}
				case "settile": { // settile <dx> <dy> <TileID name>
					int x = t.X + I(a[1]), y = t.Y + I(a[2]);
					Main.tile[x, y].ResetToType((ushort)TileID.Search.GetId(a[3]));
					PixelTerrain.Clear(x, y);
					WorldGen.SquareTileFrame(x, y);
					break;
				}
				case "give": P.QuickSpawnItem(P.GetSource_FromThis(), ItemType(a[1]), a.Length > 2 ? I(a[2]) : 1); break;
				case "clearinv": for (int i = 0; i < 58; i++) P.inventory[i].TurnToAir(); break;
				case "hold": {
					int type = ItemType(a[1]);
					int slot = Array.FindIndex(P.inventory, it => it.type == type);
					if (slot < 0) throw new ArgumentException("not in inventory: " + a[1]);
					if (slot >= 10) { (P.inventory[0], P.inventory[slot]) = (P.inventory[slot], P.inventory[0]); slot = 0; }
					P.selectedItem = slot;
					break;
				}
				case "aim": AimOffset = new Vector2(F(a[1]), F(a[2])); break;
				case "aimtile": AimOffset = new Vector2((t.X + I(a[1])) * 16 + 8, (t.Y + I(a[2])) * 16 + 8) - P.Center; break;
				case "use": UseTicks = I(a[1]); break;
				case "droptile": Item.NewItem(P.GetSource_FromThis(), new Vector2((t.X + I(a[3])) * 16 + 8, (t.Y + I(a[4])) * 16), ItemType(a[1]), I(a[2])); break;
				case "drop": Item.NewItem(P.GetSource_FromThis(), P.Center + new Vector2(F(a[3]), F(a[4])), ItemType(a[1]), I(a[2])); break;
				case "stock": OrbitalStockpile.Change(new StockKey(ItemType(a[1]), 0), a.Length > 2 ? long.Parse(a[2]) : 1); break;
				case "carve": PixelTerrain.RequestCarve(P.Center + new Vector2(F(a[1]), F(a[2])), F(a[3]), a.Length > 4 ? I(a[4]) : 1000, CarveFlags.None); break;
				case "projectile": { // projectile <type> <dx> <dy> <vx> <vy>
					int type = int.TryParse(a[1], out int id) ? id : ProjectileID.Search.GetId(a[1]);
					Projectile.NewProjectile(P.GetSource_FromThis(), P.Center + new Vector2(F(a[2]), F(a[3])), new Vector2(F(a[4]), F(a[5])), type, 20, 0f, Main.myPlayer);
					break;
				}
				case "spawnnpc": NPC.NewNPC(P.GetSource_FromThis(), (int)(P.Center.X + F(a[2])), (int)(P.Center.Y + F(a[3])), NPCID.Search.GetId(a[1])); break;
				case "interact": // interact <dx> <dy> [item count]  (optionally with an item on the cursor, set in the same tick)
					if (a.Length > 3)
						Main.mouseItem = new Item(ItemType(a[3]), a.Length > 4 ? I(a[4]) : 1);
					TileLoader.RightClick(t.X + I(a[1]), t.Y + I(a[2]));
					break;
				case "mouseitem": Main.mouseItem = new Item(ItemType(a[1]), a.Length > 2 ? I(a[2]) : 1); break;
				case "switch": P.GetModPlayer<UnitPlayer>().SwitchBody(); break;
				case "orders": P.GetModPlayer<UnitPlayer>().CycleOrders(); break;
				case "menu": OrbitalUISystem.Toggle(); break;
				case "buildkey": P.GetModPlayer<OrbitalPlayer>().BuildKey = new StockKey(ItemType(a[1]), 0); break;
				case "craft": {
					int type = ItemType(a[1]);
					int r = Enumerable.Range(0, Recipe.numRecipes).First(i => Main.recipe[i].createItem.type == type);
					OrbitalNet.RequestCraft(r, a.Length > 2 ? I(a[2]) : 1);
					break;
				}
				case "order": OrbitalNet.RequestDropPod(new[] { (new StockKey(ItemType(a[1]), 0), I(a[2])) }, P.Center); break;
				case "hurt": P.Hurt(PlayerDeathReason.LegacyDefault(), I(a[1]), 0, dodgeable: false); break;
				case "pickup": NoPickup = a[1] == "off"; break;
				case "probe": Probe = I(a[1]); break;
				case "screenshot": pendingShot = a[1]; shotFrames = shotWarmup; break;
				case "expect": RunExpect(a); break;
				default: throw new ArgumentException("unknown command " + a[0]);
			}
		}

		static void RunExpect(string[] a)
		{
			Point t = PlayerTile;
			string desc = string.Join(' ', a.Skip(1));
			switch (a[1]) {
				case "stock": Expect(Cmp(OrbitalStockpile.CountType(ItemType(a[2])), a[3], F(a[4])), $"{desc} (have {OrbitalStockpile.CountType(ItemType(a[2]))})"); break;
				case "inv": {
					int type = ItemType(a[2]);
					long n = P.inventory.Take(58).Where(i => i.type == type).Sum(i => (long)i.stack);
					Expect(Cmp(n, a[3], F(a[4])), $"{desc} (have {n})");
					break;
				}
				case "units": { int n = UnitSystem.AllUnits().Count(); Expect(Cmp(n, a[2], F(a[3])), $"{desc} (have {n})"); break; }
				case "damaged": Expect(Cmp(PixelTerrain.DamagedTileCount, a[2], F(a[3])), $"{desc} (have {PixelTerrain.DamagedTileCount})"); break;
				case "tile": {
					int x = t.X + I(a[2]), y = t.Y + I(a[3]);
					bool has = Main.tile[x, y].HasTile;
					bool ok = a[4] switch {
						"empty" => !has,
						"solid" => has,
						"partial" => has && PixelTerrain.TryGet(x, y, out var d) && d.Mask != 0,
						"launchpad" => has && Main.tile[x, y].TileType == ModContent.TileType<Content.Tiles.LaunchPad>(),
						_ => throw new ArgumentException("tile state must be empty|solid|partial|launchpad"),
					};
					Expect(ok, $"{desc} (tile {x},{y} has={has} type={Main.tile[x, y].TileType})");
					break;
				}
				case "wall": { int x = t.X + I(a[2]), y = t.Y + I(a[3]); Expect(Main.tile[x, y].WallType != WallID.None, $"{desc} (wall {Main.tile[x, y].WallType})"); break; }
				case "alive": Expect(!P.dead, $"{desc} (life {P.statLife})"); break;
				case "life": Expect(Cmp(P.statLife, a[2], F(a[3])), $"{desc} (life {P.statLife})"); break;
				case "ground": { // items lying in the world
					int n = Main.item.Count(i => i.active && !i.IsAir);
					Expect(Cmp(n, a[2], F(a[3])), $"{desc} (have {n})");
					break;
				}
				case "projectiles": {
					int type = int.TryParse(a[2], out int id) ? id : ModContent.TryFind("TerraCommand/" + a[2], out ModProjectile mp) ? mp.Type : ProjectileID.Search.GetId(a[2]);
					int n = Main.projectile.Count(pr => pr.active && pr.type == type);
					Expect(Cmp(n, a[3], F(a[4])), $"{desc} (have {n})");
					break;
				}
				case "npcs": {
					int type = NPCID.Search.GetId(a[2]);
					int n = Main.npc.Count(x => x.active && x.type == type);
					Expect(Cmp(n, a[3], F(a[4])), $"{desc} (have {n})");
					break;
				}
				case "menu": Expect(OrbitalUISystem.Visible == (a[2] == "open"), desc); break;
				default: throw new ArgumentException("unknown expectation " + a[1]);
			}
		}

		// ------------------------------------------------------------ screenshots

		static void PostDraw(GameTime gt)
		{
			if (pendingShot == null || shotFrames > 0)
				return;
			string name = pendingShot;
			pendingShot = null;
			try {
				GraphicsDevice gd = Main.instance.GraphicsDevice;
				int w = gd.PresentationParameters.BackBufferWidth, h = gd.PresentationParameters.BackBufferHeight;
				var data = new Color[w * h];
				gd.GetBackBufferData(data);
				for (int i = 0; i < data.Length; i++) data[i].A = 255;
				using var tex = new Texture2D(gd, w, h);
				tex.SetData(data);
				string path = Path.Combine(outDir, name + ".png");
				using (var fs = File.Create(path))
					tex.SaveAsPng(fs, w, h);
				Report("SHOT", path);
			}
			catch (Exception e) {
				fails++;
				Report("FAIL", $"screenshot {name}: {e.Message}");
			}
		}
	}

	public class AutoTestPlayer : ModPlayer
	{
		public override void SetControls()
		{
			if (!AutoTest.Enabled || Player.whoAmI != Main.myPlayer)
				return;
			if (AutoTest.AimOffset is Vector2 off) {
				Vector2 world = Player.Center + off;
				Vector2 screen = world - Main.screenPosition;
				Main.mouseX = (int)screen.X;
				Main.mouseY = (int)screen.Y;
				Player.tileTargetX = (int)(world.X / 16f);
				Player.tileTargetY = (int)(world.Y / 16f);
			}
			if (AutoTest.UseTicks > 0) {
				AutoTest.UseTicks--;
				Player.controlUseItem = true;
				Player.releaseUseItem = AutoTest.UseTicks % 2 == 0 || Player.HeldItem.autoReuse;
			}
			AutoTest.ProbeLog(Player, "SetControls");
		}

		public override void ResetEffects()
		{
			if (AutoTest.Enabled && AutoTest.NoPickup)
				Player.preventAllItemPickups = true;
		}

		public override void PostItemCheck()
		{
			if (AutoTest.Enabled && Player.whoAmI == Main.myPlayer)
				AutoTest.ProbeLog(Player, "PostItemCheck");
		}
	}

	public class AutoTestNPC : GlobalNPC
	{
		public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
		{
			if (AutoTest.NoSpawns)
				maxSpawns = 0;
		}
	}
}
