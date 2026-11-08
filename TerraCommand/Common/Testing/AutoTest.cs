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
		static bool serverMode;           // running a setup script on the dedicated server
		static string joinAddress, playerName = "TCTest", syncDir, clientId;
		static int peers;
		static string waitingSync;
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
		internal static int WalkDir, WalkTicks, JumpTicks, DownTicks;
		internal static bool HoldDown, HoldJump, HoldLeft, HoldRight;   // `hold <down|jump|left|right> on|off`
		internal static bool HoldCrouch, HoldRun;                        // `hold <crouch|run> on|off` (mod keybinds)
		/// <summary>The driver is holding the fire button this tick (use / shoot commands).</summary>
		internal static bool FireHeld => UseTicks > 0;
		internal static readonly Dictionary<int, (int type, Vector2 pos)> Pinned = new();
		static Vector2 markPos;
		static float peakRise;
		static int shotsLeft, shotGap, shotWait, gibsAtMark, deliveredAtMark;

		static int EnvInt(string name, int def) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : def;

		public static void TraceIf(bool cond, string msg)
		{
			if (cond)
				Trace(msg);
		}

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
			var ac = p.GetModPlayer<Physics.BodyPhysicsPlayer>().Actor;
			Report("PROBE", $"{where}: actor move={ac.Move} prone={ac.Prone} status={ac.Status} grounded={ac.Grounded} vel={p.velocity.X:0.00},{p.velocity.Y:0.00} rot={ac.Rot:0.00} h={p.height} " +
				$"legs={ac.Legs[0].Segment}/{ac.Legs[0].Planted},{ac.Legs[1].Segment}/{ac.Legs[1].Planted}");
			Report("PROBE", $"{where}: target={Player.tileTargetX},{Player.tileTargetY} mouseWorld={Main.MouseWorld.ToTileCoordinates()} " +
				$"held={p.HeldItem.Name} ctrlUse={p.controlUseItem} release={p.releaseUseItem} anim={p.itemAnimation} time={p.itemTime} " +
				$"mouseInterface={p.mouseInterface} noBuilding={p.noBuilding} focus={Main.hasFocus} playerTile={p.Bottom.ToTileCoordinates()} " +
				$"down={p.controlDown} vel={p.velocity} crouch={p.GetModPlayer<Physics.BodyPhysicsPlayer>().Crouching} spread={p.GetModPlayer<Combat.AimPlayer>().Spread:0.000}");
		}

		public override void Load()
		{
			string list = Environment.GetEnvironmentVariable("TC_AUTOTEST");
			if (string.IsNullOrEmpty(list) || Main.dedServ)
				return;
			Enabled = true;
			joinAddress = Environment.GetEnvironmentVariable("TC_JOIN");          // "ip:port" -> multiplayer client
			playerName = Environment.GetEnvironmentVariable("TC_PLAYER") ?? playerName;
			syncDir = Environment.GetEnvironmentVariable("TC_SYNC_DIR");
			clientId = Environment.GetEnvironmentVariable("TC_CLIENT_ID") ?? playerName;
			peers = EnvInt("TC_PEERS", 1);
			scenarios = list.Split(':', StringSplitOptions.RemoveEmptyEntries);
			outRoot = Environment.GetEnvironmentVariable("TC_OUT") ?? Path.Combine(Path.GetTempPath(), "tc-autotest");
			worldPath = Environment.GetEnvironmentVariable("TC_WORLD");
			baselinePath = Environment.GetEnvironmentVariable("TC_BASELINE");
			speed = Math.Max(1, EnvInt("TC_SPEED", 10));
			drawEvery = Math.Max(1, EnvInt("TC_DRAW_EVERY", 30));
			shotWarmup = Math.Max(1, EnvInt("TC_SHOT_FRAMES", 24));
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
			if (!serverMode)
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
			if (scenarioIndex + 1 < scenarios.Length && !serverMode && joinAddress == null) {
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

		static void StartNext(bool boot = true)
		{
			scenarioIndex++;
			line = waitTicks = ticksInWorld = passes = fails = 0;
			pendingShot = null;
			shotFrames = 0;
			AimOffset = null;
			UseTicks = Probe = WalkTicks = JumpTicks = DownTicks = shotsLeft = 0;
			NoSpawns = NoPickup = HoldDown = HoldJump = HoldLeft = HoldRight = HoldCrouch = HoldRun = false;
			Pinned.Clear();
			waitingSync = null;
			scenarioDone = false;

			outDir = scenarios.Length == 1 ? outRoot : Path.Combine(outRoot, $"{scenarioIndex + 1:D2}-{ScenarioName}");
			Directory.CreateDirectory(outDir);
			results = new StreamWriter(Path.Combine(outDir, "results.txt"), append: false) { AutoFlush = true };
			Report("INFO", $"scenario {scenarios[scenarioIndex]} ({scenarioIndex + 1}/{scenarios.Length}); speed {speed} ticks/frame");
			try {
				lines = File.ReadAllLines(scenarios[scenarioIndex]).Select(l => l.Split('#')[0].Trim()).ToList();
				if (boot)
					Boot();
			}
			catch (Exception e) {
				fails++;
				FinishScenario($"boot failed: {e.Message}");
			}
		}

		/// <summary>
		/// Dedicated server setup (`tc script FILE [OUTDIR]` in the server console). A server doesn't update its
		/// world while no players are connected, so the script runs synchronously; `wait` and `sync` are ignored.
		/// Writes READY + results to OUTDIR.
		/// </summary>
		public static void RunServerScript(string path, string outDirectory)
		{
			Enabled = serverMode = true;
			outRoot = outDirectory;
			Directory.CreateDirectory(outRoot);
			summary = new StreamWriter(Path.Combine(outRoot, "summary.txt"), append: false) { AutoFlush = true };
			scenarios = new[] { path };
			scenarioIndex = -1;
			StartNext(boot: false);
			while (!scenarioDone) {
				try {
					while (line < lines.Count && lines[line].Length == 0)
						line++;
					if (line >= lines.Count) {
						FinishScenario("end of server script");
						break;
					}
					string[] a = lines[line++].Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (a[0] != "wait" && a[0] != "sync")
						Run(a);
				}
				catch (Exception e) {
					fails++;
					Report("FAIL", $"line {line}: exception {e.GetType().Name}: {e.Message}");
				}
			}
			File.WriteAllText(Path.Combine(outRoot, "READY"), "");
		}

		/// <summary>Runs once per game tick while in a world.</summary>
		public override void PostUpdateEverything()
		{
			if (!Enabled || scenarioDone || Main.gameMenu)
				return;
			try {
				if (!serverMode && (Main.LocalPlayer == null || !Main.LocalPlayer.active))
					return;
				if (!serverMode)
					Main.BlackFadeIn = 0; // the enter-world fade advances per drawn frame, and fast-forward skips most draws
				if (ticksInWorld++ < 90) // let the world settle
					return;
				if (Main.netMode != NetmodeID.MultiplayerClient) {
					Main.dayTime = true;
					Main.time = Math.Max(Main.time, 16200) % 54000;
				}
				if (!serverMode && P != null)
					peakRise = Math.Max(peakRise, (markPos.Y - P.Bottom.Y) / 16f);

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
			if (joinAddress == null && string.IsNullOrEmpty(worldPath))
				throw new InvalidOperationException("TC_WORLD not set");
			if (joinAddress == null && !string.IsNullOrEmpty(baselinePath)) {
				File.Copy(baselinePath, worldPath, overwrite: true);
				string twld = Path.ChangeExtension(baselinePath, ".twld");
				if (File.Exists(twld))
					File.Copy(twld, Path.ChangeExtension(worldPath, ".twld"), overwrite: true);
			}
			if (joinAddress == null && !File.Exists(worldPath))
				throw new FileNotFoundException("world not found", worldPath);

			var p = new Player { name = playerName };
			var vanilla = new List<Item> { new(ItemID.CopperShortsword), new(ItemID.CopperPickaxe), new(ItemID.CopperAxe) };
			PlayerLoader.SetStartInventory(p, PlayerLoader.GetStartingItems(p, vanilla, mediumCoreDeath: false));
			// Save, then load back from disk like the character-creation menu does, so every field the
			// game expects (journey powers, etc.) is initialized.
			string path = PlayerFileData.CreateAndSave(p).Path;
			PlayerFileData data = Player.LoadPlayer(path, false);
			data.SetAsActive();

			if (joinAddress != null) {
				string[] hp = joinAddress.Split(':');
				Netplay.ListenPort = hp.Length > 1 ? int.Parse(hp[1]) : 7777;
				Netplay.SetRemoteIP(hp[0]);
				Main.autoPass = true;
				Main.statusText = "Joining test server";
				Netplay.StartTcpClient();
				Main.menuMode = 14;
				return;
			}
			Main.ActiveWorldFileData = WorldFile.GetAllMetadata(worldPath, false);
			Main.menuMode = 10;
			WorldGen.playWorld();
		}

		// ------------------------------------------------------------ scenario

		static void Step()
		{
			if (waitTicks > 0) { waitTicks--; return; }
			if (WalkTicks > 0 || JumpTicks > 0 || DownTicks > 0) return;
			if (shotsLeft > 0) {
				if (UseTicks > 0) return;
				if (shotWait > 0) { shotWait--; return; }
				UseTicks = 2;
				shotWait = shotGap;
				shotsLeft--;
				return;
			}
			if (waitingSync != null) {
				if (Directory.GetFiles(syncDir, waitingSync + ".*").Length < peers)
					return;
				Report("INFO", $"sync {waitingSync} reached");
				waitingSync = null;
			}
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
		static Point PlayerTile => serverMode ? new Point(Main.spawnTileX, Main.spawnTileY) : P.Bottom.ToTileCoordinates();

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
				case "goto": { // goto spawn | goto x y | goto rel dx dy   (tiles)
					Vector2 to = a[1] == "spawn" ? new Vector2(Main.spawnTileX * 16, Main.spawnTileY * 16)
						: a[1] == "rel" ? P.Bottom + new Vector2(F(a[2]) * 16, F(a[3]) * 16)
						: new Vector2(I(a[1]) * 16, I(a[2]) * 16);
					P.Bottom = to; P.velocity = Vector2.Zero; P.fallStart = (int)(P.position.Y / 16);
					P.GetModPlayer<Physics.BodyPhysicsPlayer>().SkipImpactCheck();
					break;
				}
				case "equip": { // equip <item> : into the first empty accessory slot
					int slot = Array.FindIndex(P.armor, 3, 7, it => it.IsAir);
					P.armor[slot] = new Item(ItemType(a[1]));
					break;
				}
				case "placeunit": { // placeunit <dtiles> : stand the nearest idle body next to the player
					var u = UnitSystem.AllUnits().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).First();
					u.NPC.Bottom = P.Bottom + new Vector2(F(a[1]) * 16, 0);
					u.NPC.velocity = Vector2.Zero;
					u.SkipImpactCheck();
					break;
				}
				case "blastunit": { // blastunit <dx px> : apply blast physics (no vanilla explosive) next to the nearest idle body
					var u = UnitSystem.AllUnits().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).First();
					Vector2 c = u.NPC.Center + new Vector2(F(a[1]), 8);
					Physics.Explosions.MarkBlasted(c, 96f);
					Physics.Explosions.Blast(c, 96f, Physics.Tuning.BlastImpulse);
					break;
				}
				case "liftunit": { // liftunit <tiles> : teleport the nearest idle body straight up
					var u = UnitSystem.AllUnits().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).First();
					u.NPC.position.Y -= F(a[1]) * 16;
					u.NPC.velocity = Vector2.Zero;
					u.SkipImpactCheck();
					break;
				}
				case "heal": P.statLife = P.statLifeMax2; break;
				case "maxlife": // max life comes from consumed life crystals (+20 each over the base 100)
					P.ConsumedLifeCrystals = Math.Clamp((I(a[1]) - 100) / 20, 0, Player.LifeCrystalMax);
					break;
				case "flatten": { // clear a w x h area above the player's feet and lay a dirt floor, for repeatable setups
					int w = I(a[1]), h = I(a[2]);
					for (int x = t.X - w / 2; x <= t.X + w / 2; x++) {
						for (int y = t.Y - h; y < t.Y; y++) { Main.tile[x, y].ClearEverything(); PixelTerrain.Clear(x, y); }
						for (int y = t.Y; y < t.Y + 6; y++) { Main.tile[x, y].ClearEverything(); Main.tile[x, y].ResetToType(TileID.Dirt); PixelTerrain.Clear(x, y); }
					}
					WorldGen.RangeFrame(t.X - w / 2 - 1, t.Y - h - 1, t.X + w / 2 + 1, t.Y + 7);
					if (!Main.dedServ)
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
				case "fill": { // fill dx0 dy0 dx1 dy1 TileName
					ushort type = (ushort)TileID.Search.GetId(a[5]);
					for (int x = t.X + I(a[1]); x <= t.X + I(a[3]); x++)
						for (int y = t.Y + I(a[2]); y <= t.Y + I(a[4]); y++) {
							Main.tile[x, y].ResetToType(type);
							PixelTerrain.Clear(x, y);
						}
					WorldGen.RangeFrame(t.X + I(a[1]) - 1, t.Y + I(a[2]) - 1, t.X + I(a[3]) + 1, t.Y + I(a[4]) + 1);
					if (Main.netMode == NetmodeID.Server)
						NetMessage.SendTileSquare(-1, t.X + I(a[1]), t.Y + I(a[2]), I(a[3]) - I(a[1]) + 1, I(a[4]) - I(a[2]) + 1);
					break;
				}
				case "carvetile": // carvetile <dtx> <dty> <r> : carve centered on a tile-grid point (dtx, dty may be fractional)
					PixelTerrain.RequestCarve(new Vector2((t.X + F(a[1])) * 16, (t.Y + F(a[2])) * 16), F(a[3]), 1000, CarveFlags.None);
					break;
				case "carverect": // carverect x0 y0 x1 y1 : world-pixel offsets from the top-left of the player's foot tile
					PixelTerrain.CarveRect(new Rectangle(t.X * 16 + I(a[1]), t.Y * 16 + I(a[2]), I(a[3]) - I(a[1]), I(a[4]) - I(a[2])));
					break;
				case "walk": WalkDir = a[1] == "left" ? -1 : 1; WalkTicks = I(a[2]); break;
				case "jump": JumpTicks = I(a[1]); break;
				case "crouch": DownTicks = I(a[1]); break;
				case "mark":
					markPos = P.Bottom;
					peakRise = 0f;
					P.GetModPlayer<Combat.AimPlayer>().ShotLog.Clear();
					P.GetModPlayer<Combat.AimPlayer>().ConeLog.Clear();
					gibsAtMark = Combat.DamageRules.GibCount;
					deliveredAtMark = ShipSystem.Delivered;
					break;
				case "shoot": shotsLeft = I(a[1]); shotGap = I(a[2]); shotWait = 0; break;
				case "aimnpc": { // aimnpc <NPCName> head|body : aim at the nearest such NPC
					NPC n = NearestNpc(a[1]);
					Vector2 at = a[2] == "head" ? new Vector2(n.Center.X, n.position.Y + n.height * 0.1f) : n.Center + new Vector2(0, n.height * 0.15f);
					AimOffset = at - P.Center;
					break;
				}
				case "give": { // give <item> [n] : straight into the pack (equipment isn't picked up by walking over it)
					var it = new Item(ItemType(a[1]), a.Length > 2 ? I(a[2]) : 1);
					it = P.GetItem(Main.myPlayer, it, GetItemSettings.InventoryUIToInventorySettings);
					if (!it.IsAir)
						P.QuickSpawnItem(P.GetSource_FromThis(), it, it.stack);
					break;
				}
				case "drop" when a.Length == 1: P.GetModPlayer<Actors.ActorInventory>().DropHeld(); break;
				case "pickup" when a.Length == 1: P.GetModPlayer<Actors.ActorInventory>().PickUp(); break;
				case "next": P.GetModPlayer<Actors.ActorInventory>().Next(); break;
				case "reload": P.GetModPlayer<Combat.AimPlayer>().Reload(); break;
				case "pieshow": UI.ActorControlsUI.ShowForTest(I(a[1])); break;
				case "pie": if (!UI.ActorControlsUI.Activate(string.Join(' ', a.Skip(1)))) throw new ArgumentException("pie slice unavailable: " + string.Join(' ', a.Skip(1))); break;
				case "prev": P.GetModPlayer<Actors.ActorInventory>().Prev(); break;
				case "spawnitem": { // spawnitem <item> <dx px> <dy px> : an item lying in the world near the player
					int idx = Item.NewItem(P.GetSource_FromThis(), P.Center + new Vector2(F(a[2]), F(a[3])), 1, 1, ItemType(a[1]), 1, noBroadcast: false, noGrabDelay: true);
					Main.item[idx].velocity = Vector2.Zero;
					if (Main.netMode == NetmodeID.MultiplayerClient)
						NetMessage.SendData(MessageID.SyncItem, -1, -1, null, idx, 1f);
					Report("INFO", $"spawned item {Main.item[idx].Name} #{idx} at {Main.item[idx].Center.ToTileCoordinates()} active={Main.item[idx].active}");
					break;
				}
				case "clearinv": for (int i = 0; i < 58; i++) P.inventory[i].TurnToAir(); break;
				case "hold" when a.Length > 2 && (a[2] == "on" || a[2] == "off"): {
					bool on = a[2] == "on";
					switch (a[1]) {
						case "down": HoldDown = on; break;
						case "jump": HoldJump = on; break;
						case "left": HoldLeft = on; break;
						case "right": HoldRight = on; break;
						case "crouch": HoldCrouch = on; break;
						case "run": HoldRun = on; break;
						default: throw new ArgumentException("hold what? " + a[1]);
					}
					break;
				}
				case "clearnpcs": // remove every NPC except idle bodies (town NPCs shoot at test targets)
					foreach (NPC n in Main.npc)
						if (n.active && n.ModNPC is not Content.NPCs.CloneUnit)
							n.active = false;
					break;
				case "pin": { // pin <NPCName> : freeze the nearest such NPC in place (stationary target)
					NPC n = NearestNpc(a[1]);
					Pinned[n.whoAmI] = (n.type, n.position);
					Report("INFO", $"pinned {a[1]}#{n.whoAmI} at {n.Center.ToTileCoordinates()}");
					break;
				}
				case "hold": {
					int type = ItemType(a[1]);
					int slot = Array.FindIndex(P.inventory, it => it.type == type);
					if (slot < 0) throw new ArgumentException("not in inventory: " + a[1]);
					// CC: the held item is slot 0
					(P.inventory[0], P.inventory[slot]) = (P.inventory[slot], P.inventory[0]);
					P.selectedItem = 0;
					break;
				}
				case "aim": AimOffset = new Vector2(F(a[1]), F(a[2])); break;
				case "aimtile": AimOffset = new Vector2((t.X + I(a[1])) * 16 + 8, (t.Y + I(a[2])) * 16 + 8) - P.Center; break;
				case "use": UseTicks = I(a[1]); break;
				case "droptile": DropItem(new Vector2((t.X + I(a[3])) * 16 + 8, (t.Y + I(a[4])) * 16), ItemType(a[1]), I(a[2])); break;
				case "drop": DropItem(P.Center + new Vector2(F(a[3]), F(a[4])), ItemType(a[1]), I(a[2])); break;
				case "stock": OrbitalStockpile.Change(new StockKey(ItemType(a[1]), 0), a.Length > 2 ? long.Parse(a[2]) : 1); break;
				case "carve": PixelTerrain.RequestCarve(P.Center + new Vector2(F(a[1]), F(a[2])), F(a[3]), a.Length > 4 ? I(a[4]) : 1000, CarveFlags.None); break;
				case "projectile": { // projectile <type> <dx> <dy> <vx> <vy> [damage]
					int type = int.TryParse(a[1], out int id) ? id : ProjectileID.Search.GetId(a[1]);
					Projectile.NewProjectile(P.GetSource_FromThis(), P.Center + new Vector2(F(a[2]), F(a[3])), new Vector2(F(a[4]), F(a[5])), type,
						a.Length > 6 ? I(a[6]) : 20, 0f, Main.myPlayer);
					break;
				}
				case "shotat": { // shotat <type> <dx> <dy> <vx> <vy> [damage] : hostile projectile, offsets from the player's feet
					int type = int.TryParse(a[1], out int id) ? id : ProjectileID.Search.GetId(a[1]);
					int i = Projectile.NewProjectile(P.GetSource_FromThis(), P.Bottom + new Vector2(F(a[2]), F(a[3])), new Vector2(F(a[4]), F(a[5])), type,
						a.Length > 6 ? I(a[6]) : 20, 0f, Main.myPlayer);
					Main.projectile[i].hostile = true;
					Main.projectile[i].friendly = false;
					break;
				}
				case "spawnnpc": {
					int idx = NPC.NewNPC(P.GetSource_FromThis(), (int)(P.Center.X + F(a[2])), (int)(P.Center.Y + F(a[3])), NPCID.Search.GetId(a[1]));
					Pinned.Remove(idx); // a reused slot isn't the NPC that was pinned
					Report("INFO", $"spawned {a[1]}#{idx} at {Main.npc[idx].Center.ToTileCoordinates()}");
					break;
				}
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
				case "ship": { // ship <dx tiles> [Item count]... : order a ship from the stockpile, landing dx tiles from the player
					var manifest = new List<(StockKey, int)>();
					for (int i = 2; i + 1 < a.Length; i += 2)
						manifest.Add((new StockKey(ItemType(a[i]), 0), I(a[i + 1])));
					ShipSystem.RequestShip(P.team, manifest, P.Center + new Vector2(F(a[1]) * 16f, 0f));
					break;
				}
				case "shiplaunch": ShipSystem.RequestLaunch(P); break;
				case "shiphit": { // shiphit <damage> : enemy fire hits the nearest ship
					NPC s = Main.npc.Where(n => n.active && n.ModNPC is Content.NPCs.CargoShip).OrderBy(n => Vector2.Distance(n.Center, P.Center)).First();
					s.SimpleStrikeNPC(I(a[1]), 0);
					break;
				}
				case "hurt": P.Hurt(PlayerDeathReason.LegacyDefault(), I(a[1]), 0, dodgeable: false); break;
				case "sync": // barrier: wait until all TC_PEERS clients reached this label
					if (syncDir == null) break;
					Directory.CreateDirectory(syncDir);
					File.WriteAllText(Path.Combine(syncDir, $"{a[1]}.{clientId}"), "");
					waitingSync = a[1];
					break;
				case "ready": File.WriteAllText(Path.Combine(outRoot, "READY"), ""); break;
				case "pickup": NoPickup = a[1] == "off"; break;
				case "probe": Probe = I(a[1]); break;
				case "screenshot": pendingShot = a[1]; shotFrames = shotWarmup; break;
				case "claimbrain": // claimbrain [starter] : enter-world brain logic (tests otherwise start as plain soldiers)
					Units.BrainSystem.claimInTests = true;
					Units.BrainSystem.starterInTests = a.Length > 1 && a[1] == "starter";
					Units.BrainSystem.OnEnterWorld(P);
					Units.BrainSystem.claimInTests = false;
					break;
				case "killunit": { // killunit : destroy the nearest idle body
					var u = UnitSystem.AllUnits().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).First();
					u.NPC.StrikeInstantKill();
					break;
				}
				case "kind": P.GetModPlayer<Actors.RigPlayer>().Kind = a[1] == "brain" ? Actors.ActorKind.Brain : Actors.ActorKind.Soldier; break;
				case "dumptex": { // dumptex : save the player skin sheets (Players[0, i]) as PNGs into the run folder
					for (int i = 0; i < Terraria.GameContent.TextureAssets.Players.GetLength(1); i++) {
						var asset = Terraria.GameContent.TextureAssets.Players[0, i];
						if (asset == null) continue;
						var tex = asset.Value;
						using var fs = File.Create(Path.Combine(outDir, $"players_0_{i}.png"));
						tex.SaveAsPng(fs, tex.Width, tex.Height);
					}
					break;
				}
				case "expect": RunExpect(a); break;
				default: throw new ArgumentException("unknown command " + a[0]);
			}
		}

		/// <summary>Like a player throwing an item: on a client the new item must be sent to the server explicitly.</summary>
		static void DropItem(Vector2 at, int type, int stack)
		{
			int idx = Item.NewItem(P.GetSource_FromThis(), at, type, stack);
			Main.item[idx].velocity = Vector2.Zero; // deterministic: no random toss
			if (Main.netMode == NetmodeID.MultiplayerClient)
				NetMessage.SendData(MessageID.SyncItem, -1, -1, null, idx, 1f);
		}

		static NPC NearestNpc(string name)
		{
			int type = NPCID.Search.GetId(name);
			NPC best = null;
			foreach (NPC n in Main.ActiveNPCs)
				if (n.type == type && (best == null || Vector2.Distance(n.Center, P.Center) < Vector2.Distance(best.Center, P.Center)))
					best = n;
			return best ?? throw new ArgumentException("no NPC " + name);
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
						_ => throw new ArgumentException("tile state must be empty|solid|partial|launchpad"),
					};
					Expect(ok, $"{desc} (tile {x},{y} has={has} type={Main.tile[x, y].TileType})");
					break;
				}
				case "moved": { // horizontal distance in tiles since `mark` (signed: + = right)
					float d = (P.Bottom.X - markPos.X) / 16f;
					Expect(Cmp(d, a[2], F(a[3])), $"{desc} (moved {d:0.0} tiles, now at {P.Bottom.ToTileCoordinates()})");
					break;
				}
				case "rose": { // vertical distance in tiles since `mark` (+ = up)
					float d = (markPos.Y - P.Bottom.Y) / 16f;
					Expect(Cmp(d, a[2], F(a[3])), $"{desc} (rose {d:0.0} tiles)");
					break;
				}
				case "peak": Expect(Cmp(peakRise, a[2], F(a[3])), $"{desc} (peak {peakRise:0.0} tiles above mark)"); break;
				case "mounted": Expect(P.mount.Active == (a[2] == "yes"), $"{desc} (mount active={P.mount.Active})"); break;
				case "stance": { var st = P.GetModPlayer<Physics.BodyPhysicsPlayer>().Stance; Expect(st.ToString().ToLower() == a[2], $"{desc} (stance {st}, height {P.height})"); break; }
				case "fuelnow": { float f = P.GetModPlayer<Physics.BodyPhysicsPlayer>().Fuel; Expect(Cmp(f, a[2], F(a[3])), $"{desc} (fuel {f:0})"); break; }
				case "status": { var st = P.GetModPlayer<Physics.BodyPhysicsPlayer>().Actor.Status; Expect(st.ToString().ToLower() == a[2], $"{desc} (status {st}, rot {P.fullRotation:0.00})"); break; }
				case "ships": { int n = ShipSystem.Ships().Count(); Expect(Cmp(n, a[2], F(a[3])), $"{desc} (have {n})"); break; }
				case "shipstate": {
					var s = ShipSystem.Ships().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).FirstOrDefault();
					Expect(s != null && s.State.ToString().ToLower() == a[2], $"{desc} (state {s?.State}, at {s?.NPC.Bottom.ToTileCoordinates()})");
					break;
				}
				case "shipcargo": {
					var s = ShipSystem.Ships().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).FirstOrDefault();
					int n = s == null ? 0 : ShipSystem.CargoCountClient(s.NPC.whoAmI);
					Expect(Cmp(n, a[2], F(a[3])), $"{desc} (cargo {n})");
					break;
				}
				case "delivered": Expect(Cmp(ShipSystem.Delivered - deliveredAtMark, a[2], F(a[3])), $"{desc} ({ShipSystem.Delivered - deliveredAtMark} since mark)"); break;
				case "rounds": { Item h = P.inventory[0]; int r = h.IsAir || !Combat.Devices.IsFirearm(h) ? -1 : h.GetGlobalItem<Combat.MagazineGlobalItem>().Loaded(h); Expect(Cmp(r, a[2], F(a[3])), $"{desc} (rounds {r})"); break; }
				case "magazine": { Item h = P.inventory[0]; int m = Combat.Devices.IsFirearm(h) ? Combat.Devices.Stats(h).Magazine : -1; Expect(Cmp(m, a[2], F(a[3])), $"{desc} (magazine {m})"); break; }
				case "reloading": Expect(P.GetModPlayer<Combat.AimPlayer>().Reloading == (a[2] == "yes"), $"{desc} (reload ticks {P.GetModPlayer<Combat.AimPlayer>().ReloadTicks})"); break;
				case "throwspeed": { float v = P.GetModPlayer<Combat.AimPlayer>().LastThrowSpeed; Expect(Cmp(v, a[2], F(a[3])), $"{desc} (last throw {v:0.0} px/tick)"); break; }
				case "sharpaim": { float s = P.GetModPlayer<Physics.BodyPhysicsPlayer>().Actor.SharpAim; Expect(Cmp(s, a[2], F(a[3])), $"{desc} (sharp aim {s:0.00})"); break; }
				case "holding": { Item h = P.inventory[0]; Expect(a[2] == "nothing" ? h.IsAir : h.type == ItemType(a[2]), $"{desc} (holding {(h.IsAir ? "nothing" : h.Name)})"); break; }
				case "queue": { int n = P.inventory.Skip(1).Take(49).Count(Actors.ActorInventory.IsDevice); Expect(Cmp(n, a[2], F(a[3])), $"{desc} ({n} carried)"); break; }
				case "mass": { float m = P.GetModPlayer<Actors.ActorInventory>().CarriedMass; Expect(Cmp(m, a[2], F(a[3])), $"{desc} (carrying {m:0.0} kg)"); break; }
				case "worlditem": { int n = Main.item.Count(i => i.active && i.type == ItemType(a[2])); Expect(Cmp(n, a[3], F(a[4])), $"{desc} ({n} lying around)"); break; }
				case "kind": { var k = P.GetModPlayer<Actors.RigPlayer>().Kind; Expect(k.ToString().ToLower() == a[2], $"{desc} (kind {k})"); break; }
				case "brainstate": { var st = Units.BrainSystem.StateOf(P.team); Expect(st.ToString().ToLower() == a[2], $"{desc} (state {st})"); break; }
				case "gameover": Expect(Units.BrainSystem.GameOver == (a[2] == "yes"), $"{desc} (game over {Units.BrainSystem.GameOver})"); break;
				case "unitkind": {
					var u = UnitSystem.AllUnits().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).FirstOrDefault();
					Expect(u != null && u.Loadout.Kind.ToString().ToLower() == a[2], $"{desc} (unit kind {u?.Loadout.Kind})");
					break;
				}
				case "height": Expect(Cmp(P.height, a[2], F(a[3])), $"{desc} (height {P.height})"); break;
				case "knocked": Expect((P.GetModPlayer<Physics.BodyPhysicsPlayer>().KnockedTicks > 0) == (a[2] == "yes"), desc); break;
				case "fuel": { float f = P.GetModPlayer<Physics.BodyPhysicsPlayer>().FuelMax; Expect(Cmp(f, a[2], F(a[3])), $"{desc} (max fuel {f:0})"); break; }
				case "spread": {
					var log = P.GetModPlayer<Combat.AimPlayer>().ShotLog;
					float avg = log.Count == 0 ? 0f : log.Average();
					Expect(log.Count > 0 && Cmp(avg, a[2], F(a[3])), $"{desc} (avg |deviation| {avg:0.000} rad over {log.Count} shots)");
					break;
				}
				case "cone": { // average cone half-angle at the shots since `mark` (deterministic, unlike spread)
					var log = P.GetModPlayer<Combat.AimPlayer>().ConeLog;
					float avg = log.Count == 0 ? 0f : log.Average();
					Expect(log.Count > 0 && Cmp(avg, a[2], F(a[3])), $"{desc} (avg cone {avg:0.000} rad over {log.Count} shots)");
					break;
				}
				case "headshot": Expect(Combat.DamageGlobalNPC.LastHeadshot == (a[2] == "yes"), $"{desc} (last hit headshot={Combat.DamageGlobalNPC.LastHeadshot})"); break;
				case "wounds": {
					int n = NearestNpc(a[2]).GetGlobalNPC<Combat.DamageGlobalNPC>().Wounds.Count;
					Expect(Cmp(n, a[3], F(a[4])), $"{desc} (has {n})");
					break;
				}
				case "mywounds": { int n = P.GetModPlayer<Combat.DamagePlayer>().Wounds.Count; Expect(Cmp(n, a[2], F(a[3])), $"{desc} (has {n})"); break; }
				case "unitlife": {
					var u = UnitSystem.AllUnits().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).First();
					Expect(Cmp(u.NPC.life, a[2], F(a[3])), $"{desc} (unit life {u.NPC.life}/{u.NPC.lifeMax})");
					break;
				}
				case "unitknocked": {
					var u = UnitSystem.AllUnits().OrderBy(x => Vector2.Distance(x.NPC.Center, P.Center)).First();
					Expect((u.KnockedTicks > 0) == (a[2] == "yes"), $"{desc} (knocked {u.KnockedTicks})");
					break;
				}
				case "gibs": { int n = Combat.DamageRules.GibCount - gibsAtMark; Expect(Cmp(n, a[2], F(a[3])), $"{desc} ({n} since mark)"); break; }
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
				Report("SHOT", $"{path} (day={Main.dayTime} time={Main.time:0} player={(Main.LocalPlayer.dead ? "dead" : "alive")} at {Main.LocalPlayer.Center.ToTileCoordinates()} screen={(Main.screenPosition / 16f).ToPoint()})");
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
			if (AutoTest.WalkTicks > 0) {
				AutoTest.WalkTicks--;
				Player.controlLeft = AutoTest.WalkDir < 0;
				Player.controlRight = AutoTest.WalkDir > 0;
			}
			if (AutoTest.JumpTicks > 0) {
				AutoTest.JumpTicks--;
				Player.controlJump = true;
			}
			if (AutoTest.DownTicks > 0) {
				AutoTest.DownTicks--;
				Player.controlDown = true;
			}
			if (AutoTest.HoldDown) Player.controlDown = true;
			if (AutoTest.HoldJump) Player.controlJump = true;
			if (AutoTest.HoldLeft) Player.controlLeft = true;
			if (AutoTest.HoldRight) Player.controlRight = true;
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
		public override void PostAI(NPC npc)
		{
			if (AutoTest.Enabled && AutoTest.Pinned.TryGetValue(npc.whoAmI, out var pin) && pin.type == npc.type) {
				npc.position = pin.pos;
				npc.velocity = Vector2.Zero;
			}
		}

		public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
		{
			if (AutoTest.NoSpawns)
				maxSpawns = 0;
		}
	}
}
