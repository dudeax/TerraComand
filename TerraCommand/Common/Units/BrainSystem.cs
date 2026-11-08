using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI.Chat;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Net;
using TerraCommand.Content.NPCs;

namespace TerraCommand.Common.Units
{
	public enum BrainState : byte { None, Alive, Lost }

	/// <summary>
	/// Each team has one brain: a slow walking robot carrying a brain in a jar. It's a body like any other (control it, or leave
	/// it idle somewhere safe), but if it's destroyed the game is over and the world is lost for that team, as in Cortex
	/// Command. The first player of a team to enter a world becomes its brain. Server-authoritative; saved with the world.
	/// </summary>
	public class BrainSystem : ModSystem
	{
		static readonly Dictionary<int, BrainState> states = new();

		/// <summary>Client side: this player's team has lost (game-over screen, then back to the menu).</summary>
		public static bool GameOver { get; private set; }
		static int gameOverTicks;
		const int GameOverShowTicks = 60 * 6;

		internal static bool claimInTests;
		static int starterFor = -1;

		public static BrainState StateOf(int team) => states.TryGetValue(team, out BrainState s) ? s : BrainState.None;

		public override void ClearWorld()
		{
			states.Clear();
			GameOver = false;
			gameOverTicks = 0;
		}

		public override void SaveWorldData(TagCompound tag)
		{
			var list = new List<int>();
			foreach ((int team, BrainState s) in states)
				list.Add(team * 4 + (int)s);
			tag["brains"] = list;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			states.Clear();
			foreach (int v in tag.GetList<int>("brains"))
				states[v / 4] = (BrainState)(v % 4);
		}

		// ------------------------------------------------------------ claiming the brain on entering a world

		/// <summary>Called by the entering player (client or singleplayer).</summary>
		public static void OnEnterWorld(Player p)
		{
			// scripted tests start as plain soldiers; brain scenarios claim it explicitly (`claimbrain`)
			if (Testing.AutoTest.Enabled && !claimInTests)
				return;
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket pk = PacketHandler.Create(PacketType.BrainClaim);
				pk.Write((byte)p.team);
				pk.Write(p.GetModPlayer<RigPlayer>().Kind == ActorKind.Brain);
				pk.Send();
				return;
			}
			bool isBrain = ServerClaim(p.team, p.GetModPlayer<RigPlayer>().Kind == ActorKind.Brain);
			Apply(p, StateOf(p.team), isBrain);
			SendStarterShip(p);
		}

		/// <summary>Decides whether the entering player is (or becomes) the team's brain.</summary>
		static bool ServerClaim(int team, bool claimsToBeBrain)
		{
			switch (StateOf(team)) {
				case BrainState.None:
					states[team] = BrainState.Alive;
					starterFor = team;   // the new team's first ship comes down next to its brain
					return true;
				case BrainState.Alive:
					// keep being the brain only if it isn't standing idle somewhere already
					return claimsToBeBrain && !IdleBrainExists(team);
				default:
					return false;
			}
		}

		internal static bool starterInTests;

		static void SendStarterShip(Player p)
		{
			if (starterFor != p.team)
				return;
			if (Testing.AutoTest.Enabled && !starterInTests) {
				starterFor = -1;
				return;
			}
			starterFor = -1;
			Orbital.ShipSystem.StarterShip(p.team, p.Bottom, p.direction == 0 ? 1 : p.direction);
		}

		static bool IdleBrainExists(int team)
		{
			foreach (CloneUnit u in UnitSystem.TeamUnits(team))
				if (u.Loadout.Kind == ActorKind.Brain)
					return true;
			return false;
		}

		static void Apply(Player p, BrainState state, bool isBrain)
		{
			var rig = p.GetModPlayer<RigPlayer>();
			rig.Kind = isBrain ? ActorKind.Brain : ActorKind.Soldier;
			if (state == BrainState.Lost)
				StartGameOver();
			else if (isBrain && p.whoAmI == Main.myPlayer && !Main.dedServ)
				Main.NewText("You are your team's brain. Keep it alive: if it is destroyed, the game is over.", new Color(230, 120, 150));
		}

		// ------------------------------------------------------------ destruction

		/// <summary>Server/singleplayer: a team's brain was destroyed.</summary>
		public static void BrainDestroyed(int team)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient || StateOf(team) == BrainState.Lost)
				return;
			states[team] = BrainState.Lost;
			Testing.AutoTest.Trace($"brain of team {team} destroyed");
			if (Main.netMode == NetmodeID.Server) {
				ModPacket pk = PacketHandler.Create(PacketType.BrainState);
				pk.Write((byte)team);
				pk.Write((byte)BrainState.Lost);
				pk.Write(false);
				pk.Send();
			}
			else if (Main.LocalPlayer.team == team)
				StartGameOver();
		}

		public static void Receive(PacketType type, BinaryReader r, int from)
		{
			if (type == PacketType.BrainClaim) {
				int team = r.ReadByte();
				bool claims = r.ReadBoolean();
				if (Main.netMode != NetmodeID.Server)
					return;
				bool isBrain = ServerClaim(team, claims);
				SendStarterShip(Main.player[from]);
				ModPacket pk = PacketHandler.Create(PacketType.BrainState);
				pk.Write((byte)team);
				pk.Write((byte)StateOf(team));
				pk.Write(isBrain);
				pk.Send(from);
				return;
			}
			// BrainState (S->C): either the answer to our claim, or a broadcast that a team lost
			int t = r.ReadByte();
			var state = (BrainState)r.ReadByte();
			bool brain = r.ReadBoolean();
			if (Main.netMode == NetmodeID.Server) {
				if (state == BrainState.Lost)
					BrainDestroyed(t); // a client's controlled brain died
				return;
			}
			states[t] = state;
			Player me = Main.LocalPlayer;
			if (me.team != t)
				return;
			if (state == BrainState.Lost)
				StartGameOver();
			else
				Apply(me, state, brain);
		}

		// ------------------------------------------------------------ game over

		static void StartGameOver()
		{
			if (GameOver || Main.dedServ)
				return;
			GameOver = true;
			gameOverTicks = 0;
			SoundEngine.PlaySound(SoundID.Roar with { Pitch = -0.6f });
		}

		public override void PostUpdateEverything()
		{
			if (!GameOver || Main.dedServ)
				return;
			gameOverTicks++;
			// the scripted test driver checks the state instead of being thrown back to the menu
			if (gameOverTicks == GameOverShowTicks && !Testing.AutoTest.Enabled)
				WorldGen.SaveAndQuit();
		}

		public override void PostDrawInterface(SpriteBatch sb)
		{
			if (!GameOver || Main.gameMenu)
				return;
			float a = Math.Min(1f, gameOverTicks / 60f);
			sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black * (0.6f * a));
			DrawCentered(sb, "BRAIN DESTROYED", Main.screenHeight * 0.42f, 1.6f, new Color(230, 70, 70) * a);
			DrawCentered(sb, "Game over. This world is lost to your team.", Main.screenHeight * 0.52f, 0.9f, Color.White * a);
		}

		static void DrawCentered(SpriteBatch sb, string text, float y, float scale, Color c)
		{
			var font = FontAssets.DeathText.Value;
			Vector2 size = font.MeasureString(text) * scale;
			ChatManager.DrawColorCodedStringWithShadow(sb, font, text, new Vector2((Main.screenWidth - size.X) / 2f, y), c, 0f, Vector2.Zero, new Vector2(scale));
		}
	}

	/// <summary>Hooks the brain into the player: claim on entering a world, game over when the controlled brain dies.</summary>
	public class BrainPlayer : ModPlayer
	{
		public override void OnEnterWorld() => BrainSystem.OnEnterWorld(Player);

		public override void Kill(double damage, int hitDirection, bool pvp, Terraria.DataStructures.PlayerDeathReason damageSource)
		{
			if (Player.GetModPlayer<RigPlayer>().Kind != ActorKind.Brain || Player.whoAmI != Main.myPlayer)
				return;
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket pk = PacketHandler.Create(PacketType.BrainState); // tell the server: our brain died
				pk.Write((byte)Player.team);
				pk.Write((byte)BrainState.Lost);
				pk.Write(true);
				pk.Send();
			}
			else
				BrainSystem.BrainDestroyed(Player.team);
		}
	}
}
