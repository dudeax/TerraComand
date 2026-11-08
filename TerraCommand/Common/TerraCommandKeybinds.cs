using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.GameInput;
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

		/// <summary>Our keybinds and their defaults (tModLoader leaves new mod keybinds unbound until "reset" is pressed).</summary>
		static readonly List<(string fullName, string key)> defaults = new();

		static ModKeybind Register(Mod mod, string name, string key)
		{
			ModKeybind k = KeybindLoader.RegisterKeybind(mod, name, key);
			if (key != "None")
				defaults.Add((mod.Name + "/" + name, key));   // tModLoader's key for a mod keybind
			return k;
		}

		public override void Load()
		{
			defaults.Clear();
			SwitchBody = Register(Mod, "SwitchBody", "Q");
			CycleOrders = Register(Mod, "CycleOrders", "None"); // orders live in the pie menu now
			OrbitalMenu = Register(Mod, "OrbitalMenu", "K");
			Crouch = Register(Mod, "Crouch", "LeftControl");
			Run = Register(Mod, "Run", "LeftShift");
			Pickup = Register(Mod, "Pickup", "F");
			Drop = Register(Mod, "Drop", "G");
			Reload = Register(Mod, "Reload", "R");
		}

		public override void Unload()
		{
			SwitchBody = CycleOrders = OrbitalMenu = Crouch = Run = Pickup = Drop = Reload = null;
			defaults.Clear();
		}

		bool applied;

		/// <summary>
		/// Gives our keybinds their default keys once (per keybind, ever): tModLoader registers mod keybinds empty, so out of
		/// the box nothing would be bound. A key the player later unbinds stays unbound (we remember what we've applied).
		/// </summary>
		public override void PostUpdateInput()
		{
			if (applied || Main.dedServ)
				return;
			applied = true;
			try {
				string marker = Path.Combine(Main.SavePath, "TerraCommand.keybinds-applied.txt");
				var done = File.Exists(marker) ? new HashSet<string>(File.ReadAllLines(marker)) : new HashSet<string>();
				bool changed = false;
				foreach ((string fullName, string key) in defaults) {
					if (done.Contains(fullName))
						continue;
					foreach (PlayerInputProfile profile in PlayerInput.Profiles.Values) {
						if (!profile.InputModes.TryGetValue(InputMode.Keyboard, out KeyConfiguration cfg))
							continue;
						if (!cfg.KeyStatus.TryGetValue(fullName, out List<string> keys))
							cfg.KeyStatus[fullName] = keys = new List<string>();
						if (keys.Count == 0)
							keys.Add(key);
					}
					done.Add(fullName);
					changed = true;
				}
				if (changed) {
					PlayerInput.Save();
					File.WriteAllLines(marker, done);
					Mod.Logger.Info("Applied default keybinds: " + string.Join(", ", defaults.Select(d => $"{d.fullName}={d.key}")));
				}
			}
			catch (Exception e) {
				Mod.Logger.Warn("Couldn't apply default keybinds: " + e.Message);
			}
		}
	}
}
