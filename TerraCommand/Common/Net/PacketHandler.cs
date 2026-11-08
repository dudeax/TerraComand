using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraCommand.Common.Destruction;
using TerraCommand.Common.Orbital;
using TerraCommand.Common.Units;

namespace TerraCommand.Common.Net
{
	public enum PacketType : byte
	{
		// Terrain
		CarveRequest,      // C->S
		CarveApply,        // S->C
		TerrainFull,       // S->C
		MineRequest,       // C->S
		MaskSet,           // S->C
		// Join sync
		FullSyncRequest,   // C->S
		// Orbital stockpile
		StockFull,         // S->C
		StockDelta,        // S->C
		LaunchItem,        // C->S
		CraftRequest,      // C->S
		OrderDropPod,      // C->S
		BuildRequest,      // C->S
		// Units
		DeployUnit,        // C->S
		SwapRequest,       // C->S
		SwapGranted,       // S->C
		SetOrders,         // C->S
		// Bodies
		StanceSync,        // C->S->C
	}

	public static class PacketHandler
	{
		public static ModPacket Create(PacketType type)
		{
			ModPacket p = ModContent.GetInstance<TerraCommand>().GetPacket();
			p.Write((byte)type);
			return p;
		}

		public static void Handle(BinaryReader r, int from)
		{
			var type = (PacketType)r.ReadByte();
			switch (type) {
				case PacketType.CarveRequest:
				case PacketType.CarveApply:
					PixelTerrain.ReceiveCarve(r, type, from);
					break;
				case PacketType.TerrainFull:
					PixelTerrain.ReceiveFull(r);
					break;
				case PacketType.MineRequest:
					PixelTerrain.ReceiveMine(r, from);
					break;
				case PacketType.MaskSet:
					PixelTerrain.ReceiveMaskSet(r);
					break;
				case PacketType.FullSyncRequest:
					if (Main.netMode == NetmodeID.Server) {
						PixelTerrain.SendFull(from);
						OrbitalStockpile.SendFull(from);
					}
					break;
				case PacketType.StockFull:
					OrbitalStockpile.ReceiveFull(r);
					break;
				case PacketType.StockDelta:
					OrbitalStockpile.ReceiveDelta(r);
					break;
				case PacketType.LaunchItem:
				case PacketType.CraftRequest:
				case PacketType.OrderDropPod:
				case PacketType.BuildRequest:
					OrbitalNet.HandleRequest(type, r, from);
					break;
				case PacketType.StanceSync:
					Physics.BodyPhysicsPlayer.ReceiveStance(r, from);
					break;
				case PacketType.DeployUnit:
				case PacketType.SwapRequest:
				case PacketType.SwapGranted:
				case PacketType.SetOrders:
					UnitSystem.HandlePacket(type, r, from);
					break;
			}
		}
	}
}
