using Terraria.ModLoader;

namespace TerraCommand.Common
{
	public class TerraCommandKeybinds : ModSystem
	{
		public static ModKeybind SwitchBody { get; private set; }
		public static ModKeybind CycleOrders { get; private set; }
		public static ModKeybind OrbitalMenu { get; private set; }
		// Cortex Command body controls (A/D, W jump and S prone come from Terraria's own Left/Right/Up/Down bindings)
		public static ModKeybind Crouch { get; private set; }
		public static ModKeybind Run { get; private set; }
		public static ModKeybind Pickup { get; private set; }
		public static ModKeybind Drop { get; private set; }
		public static ModKeybind Reload { get; private set; }

		public override void Load()
		{
			SwitchBody = KeybindLoader.RegisterKeybind(Mod, "SwitchBody", "Q");
			CycleOrders = KeybindLoader.RegisterKeybind(Mod, "CycleOrders", "None"); // orders live in the pie menu now
			OrbitalMenu = KeybindLoader.RegisterKeybind(Mod, "OrbitalMenu", "K");
			Crouch = KeybindLoader.RegisterKeybind(Mod, "Crouch", "LeftControl");
			Run = KeybindLoader.RegisterKeybind(Mod, "Run", "LeftShift");
			Pickup = KeybindLoader.RegisterKeybind(Mod, "Pickup", "F");
			Drop = KeybindLoader.RegisterKeybind(Mod, "Drop", "G");
			Reload = KeybindLoader.RegisterKeybind(Mod, "Reload", "R");
		}

		public override void Unload()
		{
			SwitchBody = CycleOrders = OrbitalMenu = Crouch = Run = Pickup = Drop = Reload = null;
		}
	}
}
