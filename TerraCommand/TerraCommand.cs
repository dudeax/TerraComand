// Terra Command - a Cortex Command inspired overhaul for Terraria (tModLoader)
// Copyright (C) 2026 Terra Command contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU Affero General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. See LICENSE in the repository root.

using System.IO;
using Terraria.ModLoader;
using TerraCommand.Common.Net;

namespace TerraCommand
{
	public class TerraCommand : Mod
	{
		public override void HandlePacket(BinaryReader reader, int whoAmI) => PacketHandler.Handle(reader, whoAmI);
	}
}
