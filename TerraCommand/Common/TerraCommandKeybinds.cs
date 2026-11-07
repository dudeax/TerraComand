using Terraria.ModLoader;

namespace TerraCommand.Common
{
	public class TerraCommandKeybinds : ModSystem
	{
		public static ModKeybind SwitchBody { get; private set; }
		public static ModKeybind CycleOrders { get; private set; }
		public static ModKeybind OrbitalMenu { get; private set; }

		public override void Load()
		{
			SwitchBody = KeybindLoader.RegisterKeybind(Mod, "SwitchBody", "Q");
			CycleOrders = KeybindLoader.RegisterKeybind(Mod, "CycleOrders", "G");
			OrbitalMenu = KeybindLoader.RegisterKeybind(Mod, "OrbitalMenu", "K");
		}

		public override void Unload()
		{
			SwitchBody = CycleOrders = OrbitalMenu = null;
		}
	}
}
