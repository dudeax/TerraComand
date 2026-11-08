using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraCommand.Common.Net;

namespace TerraCommand.Common.Orbital
{
	/// <summary>
	/// Requests that change the stockpile. Each Request* method runs directly in singleplayer, or sends a
	/// packet that the server validates and executes.
	/// </summary>
	public static class OrbitalNet
	{
		static bool IsClient => Main.netMode == NetmodeID.MultiplayerClient;

		// ---------------------------------------------------------------- craft

		public static void RequestCraft(int recipeIndex, int times)
		{
			if (IsClient) {
				ModPacket p = PacketHandler.Create(PacketType.CraftRequest);
				p.Write(recipeIndex); p.Write(times);
				p.Send();
				return;
			}
			DoCraft(recipeIndex, times);
		}

		static void DoCraft(int recipeIndex, int times)
		{
			if (recipeIndex < 0 || recipeIndex >= Recipe.numRecipes)
				return;
			OrbitalCrafting.Craft(Main.recipe[recipeIndex], Math.Clamp(times, 1, 999));
		}

		// ---------------------------------------------------------------- build

		public static void RequestBuild(int x, int y, StockKey key)
		{
			if (IsClient) {
				ModPacket p = PacketHandler.Create(PacketType.BuildRequest);
				p.Write(x); p.Write(y); p.Write(key.Type); p.Write(key.Prefix);
				p.Send();
				return;
			}
			DoBuild(x, y, key, Main.myPlayer);
		}

		static void DoBuild(int x, int y, StockKey key, int player)
		{
			if (!WorldGen.InWorld(x, y, 5) || OrbitalStockpile.Count(key) < 1)
				return;
			Item sample = ContentSamples.ItemsByType[key.Type];
			Tile tile = Main.tile[x, y];
			bool placed = false;
			if (sample.createTile >= TileID.Dirt) {
				if (tile.HasTile && !Main.tileCut[tile.TileType])
					return;
				placed = WorldGen.PlaceTile(x, y, sample.createTile, mute: false, forced: false, plr: player, style: sample.placeStyle)
					&& Main.tile[x, y].HasTile;
			}
			else if (sample.createWall > WallID.None) {
				if (tile.WallType != WallID.None)
					return;
				WorldGen.PlaceWall(x, y, sample.createWall);
				placed = Main.tile[x, y].WallType == sample.createWall;
			}
			if (!placed)
				return;
			OrbitalStockpile.TryTake(key, 1);
			if (Main.netMode == NetmodeID.Server)
				NetMessage.SendTileSquare(-1, x - 2, y - 2, 5, 5);
		}

		// ---------------------------------------------------------------- server dispatch

		internal static void HandleRequest(PacketType type, BinaryReader r, int from)
		{
			if (Main.netMode != NetmodeID.Server)
				return;
			Player sender = Main.player[from];
			switch (type) {
				case PacketType.CraftRequest:
					DoCraft(r.ReadInt32(), r.ReadInt32());
					break;
				case PacketType.BuildRequest: {
					int x = r.ReadInt32(), y = r.ReadInt32();
					var key = new StockKey(r.ReadInt32(), r.ReadInt32());
					if (Vector2.Distance(new Vector2(x * 16, y * 16), sender.Center) < 1600f)
						DoBuild(x, y, key, from);
					break;
				}
			}
		}
	}
}
