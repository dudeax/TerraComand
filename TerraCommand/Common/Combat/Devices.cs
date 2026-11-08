using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;
using TerraCommand.Common.Actors;
using TerraCommand.Common.Physics;

namespace TerraCommand.Common.Combat
{
	/// <summary>
	/// A Terraria weapon seen as a Cortex Command device (HDFirearm / ThrownDevice / digger). Firearm numbers are derived from
	/// the item (CC equivalents in docs/cc-actor-design.md): magazine and reload from the fire rate, spread ("shake", in
	/// degrees, halved as CC does) wider for fast guns, sharp-aim reach longer for slow, accurate ones.
	/// </summary>
	public readonly record struct FirearmStats(int Magazine, int ReloadTicks, float ShakeDeg, float SharpShakeDeg, float SharpLength, float NoSupportFactor);

	public static class Devices
	{
		/// <summary>Guns and launchers: ranged weapons fed from ammo (bows draw arrows one at a time: no magazine).</summary>
		public static bool IsFirearm(Item it) =>
			it != null && !it.IsAir && (it.ModItem is CCFirearm
				|| it.damage > 0 && it.shoot > ProjectileID.None && it.useAmmo != AmmoID.None
				&& it.useAmmo != AmmoID.Arrow && it.useAmmo != AmmoID.Stake && !it.channel);

		/// <summary>Anything aimed that fires something: firearms, bows, magic (spread and sharp aim apply).</summary>
		public static bool IsAimed(Item it) =>
			it != null && !it.IsAir && it.shoot > ProjectileID.None && it.damage > 0 && !it.channel
			&& (it.CountsAsClass(DamageClass.Ranged) || it.CountsAsClass(DamageClass.Magic)) && !IsThrowable(it);

		/// <summary>Grenades, bombs, dynamite...: thrown by hand with a wind-up.</summary>
		public static bool IsThrowable(Item it) =>
			it != null && !it.IsAir && it.consumable && it.shoot > ProjectileID.None && it.useAmmo == AmmoID.None
			&& it.ammo == AmmoID.None && it.createTile < TileID.Dirt && (it.useStyle == ItemUseStyleID.Swing || it.noUseGraphic);

		/// <summary>Pickaxes (and drills) are CC diggers: they eat the terrain along the aim, close in front of the hands.</summary>
		public static bool IsDigger(Item it) => it != null && !it.IsAir && it.pick > 0;

		static bool IsShotgun(Item it) =>
			it.type is ItemID.Boomstick or ItemID.Shotgun or ItemID.QuadBarrelShotgun or ItemID.TacticalShotgun or ItemID.OnyxBlaster or ItemID.Xenopopper;

		public static FirearmStats Stats(Item it)
		{
			if (it.ModItem is CCFirearm cc) {
				FirearmSpec s = cc.Spec;
				return new FirearmStats(s.Magazine, s.ReloadTicks, s.ShakeDeg, s.SharpShakeDeg, s.SharpLength, s.NoSupportFactor);
			}
			int t = Math.Max(1, it.useTime);
			int mag;
			if (it.useAmmo == AmmoID.Rocket)
				mag = 1;
			else if (IsShotgun(it))
				mag = 6;
			else if (t <= 6)
				mag = 100;          // machine guns
			else if (t <= 10)
				mag = 40;           // assault / sub-machine guns
			else if (t <= 20)
				mag = 15;           // pistols, rifles
			else if (t <= 35)
				mag = 8;
			else
				mag = 5;            // snipers, muskets
			int reload = mag >= 100 ? 180 : mag >= 40 ? 108 : mag >= 15 ? 72 : mag >= 6 ? 90 : 80; // CC assault rifle: 1800 ms
			float shake = t <= 10 ? 5f : t <= 20 ? 3f : t <= 35 ? 1.6f : 0.8f;  // degrees, CC-style full range
			float sharpShake = shake * 0.55f;
			float sharpLength = Math.Clamp(120f + t * 6f, 140f, 420f);              // CC: pistol ~150, rifle 250, sniper 400+
			return new FirearmStats(mag, reload, shake, sharpShake, sharpLength, 2f);
		}

		/// <summary>Sharp-aim reach for any aimed device (camera look-ahead).</summary>
		public static float SharpLength(Item it) =>
			IsFirearm(it) ? Stats(it).SharpLength : IsAimed(it) ? Math.Clamp(100f + it.useTime * 5f, 120f, 300f) : 0f;

		/// <summary>Standard normal sample (CC's RandomNormalNum).</summary>
		public static float Gaussian()
		{
			float u1 = 1f - Main.rand.NextFloat(), u2 = Main.rand.NextFloat();
			return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(MathHelper.TwoPi * u2);
		}
	}

	/// <summary>The rounds in a gun's magazine travel with the item.</summary>
	public class MagazineGlobalItem : GlobalItem
	{
		public override bool InstancePerEntity => true;
		public int Rounds = -1;   // -1: not yet loaded (a new gun comes loaded)

		public override bool AppliesToEntity(Item it, bool lateInstantiation) => lateInstantiation && Devices.IsFirearm(it);

		public int Loaded(Item it)
		{
			if (Rounds < 0)
				Rounds = Devices.Stats(it).Magazine;
			return Rounds;
		}

		public override void SaveData(Item item, TagCompound tag) => tag["rounds"] = Rounds;
		public override void LoadData(Item item, TagCompound tag) => Rounds = tag.ContainsKey("rounds") ? tag.GetInt("rounds") : -1;
	}

	/// <summary>
	/// Using devices the Cortex Command way: firearms have magazines and reload (R, or automatically when empty), spread per
	/// round shrinks with sharp aim and doubles without support, rounds leave the muzzle along the aim and push the shooter
	/// back; diggers dig along the aim; throwables wind up while held and fly on release.
	/// </summary>
	public class AimPlayer : ModPlayer
	{
		/// <summary>Current spread half-range (radians) of the held aimed device, for the HUD/sight.</summary>
		public float Spread;
		public int ReloadTicks, ReloadTotal;
		public float ThrowCharge;           // 0..1 while winding up a throw
		public float GunKick;               // visual: the gun jolts back when fired
		public float LastThrowSpeed;        // px/tick (tests)
		bool wantReload, triggerHeld, throwHeld;

		/// <summary>|deviation| and the spread half-range of recent shots (radians), for tests.</summary>
		public readonly List<float> ShotLog = new();
		public readonly List<float> ConeLog = new();

		public bool Reloading => ReloadTicks > 0;
		public int SpinUp, BurstLeft;
		Item Held => Player.inventory[ActorInventory.Held];
		CCActor Actor => Player.GetModPlayer<BodyPhysicsPlayer>().Actor;

		public override void ProcessTriggers(Terraria.GameInput.TriggersSet triggersSet)
		{
			if (!ActorControlState.Blocked && TerraCommandKeybinds.Reload.JustPressed)
				wantReload = true;
		}

		public void Reload() => wantReload = true;

		/// <summary>CC: unsupported fire (in the air, thrown, jetting) spreads twice as wide; sharp aim narrows it.</summary>
		public float CurrentShake(Item it)
		{
			CCActor a = Actor;
			bool supported = a.Status == ActorStatus.Stable && (a.Grounded || a.Prone != ProneState.NotProne) && !a.Jetting;
			float sharp = Reloading ? 0f : a.SharpAim;
			float shake, sharpShake, noSupport;
			if (Devices.IsFirearm(it)) {
				FirearmStats s = Devices.Stats(it);
				(shake, sharpShake, noSupport) = (s.ShakeDeg, s.SharpShakeDeg, s.NoSupportFactor);
			}
			else
				(shake, sharpShake, noSupport) = (3f, 1.5f, 2f);   // bows and magic
			float deg = (shake - (shake - sharpShake) * sharp) * (supported ? 1f : noSupport);
			return MathHelper.ToRadians(deg / 2f);                   // CC halves ShakeRange on load
		}

		public override void PreUpdate()
		{
			if (Player.whoAmI != Main.myPlayer)
				return;
			Item it = Held;
			GunKick *= 0.7f;
			if (it.type != lastHeldType) {
				SpinUp = BurstLeft = 0;   // a different device in the hands
				lastHeldType = it.type;
			}
			Spread = Devices.IsAimed(it) ? CurrentShake(it) : 0f;

			// reloading: switching away cancels it; a full magazine needs none
			if (Reloading) {
				if (reloadType != it.type) {
					ReloadTicks = 0;
				}
				else if (--ReloadTicks == 0) {
					var mag = it.GetGlobalItem<MagazineGlobalItem>();
					mag.Rounds = Math.Min(Devices.Stats(it).Magazine, AmmoAvailable(it));
					SoundEngine.PlaySound(SoundID.Unlock with { Pitch = 0.4f }, Player.Center);
					Testing.AutoTest.Trace($"reloaded {it.Name}: {mag.Rounds}");
				}
			}
			// CC: an empty gun in the hands reloads by itself
			if (Devices.IsFirearm(it) && !Reloading && it.GetGlobalItem<MagazineGlobalItem>().Loaded(it) <= 0 && AmmoAvailable(it) > 0)
				StartReload(it);
			if (wantReload && Devices.IsFirearm(it) && !Reloading
				&& it.GetGlobalItem<MagazineGlobalItem>().Loaded(it) < Devices.Stats(it).Magazine && AmmoAvailable(it) > 0)
				StartReload(it);
			wantReload = false;
		}

		int reloadType, lastHeldType;

		void StartReload(Item it)
		{
			ReloadTotal = ReloadTicks = Devices.Stats(it).ReloadTicks;
			reloadType = it.type;
			Actor.SharpAim = 0f; // CC: reloading drops sharp aim
			SoundEngine.PlaySound(SoundID.Item149 with { Volume = 0.6f }, Player.Center);
			Testing.AutoTest.Trace($"reloading {it.Name}");
		}

		int AmmoAvailable(Item gun)
		{
			if (gun.ModItem is CCFirearm)
				return int.MaxValue;   // CC magazines refill without ammo
			int n = 0;
			for (int i = 0; i < 58; i++) {
				Item a = Player.inventory[i];
				if (!a.IsAir && a.ammo == gun.useAmmo)
					n += a.stack;
			}
			return n;
		}

		public override void SetControls()
		{
			if (Player.whoAmI != Main.myPlayer)
				return;
			if (ActorControlState.Blocked) {
				Player.controlUseItem = false;
				throwHeld = triggerHeld = false;
				ThrowCharge = 0;
				SpinUp = BurstLeft = 0;
				return;
			}
			Item it = Held;
			// (the scripted test driver presses buttons in a later SetControls hook: read its intent directly)
			bool fire = Player.controlUseItem || Testing.AutoTest.Enabled && Testing.AutoTest.FireHeld;

			if (Devices.IsFirearm(it)) {
				var mag = it.GetGlobalItem<MagazineGlobalItem>();
				FirearmSpec spec = (it.ModItem as CCFirearm)?.Spec;
				if (Reloading)
					Player.controlUseItem = false;
				else if (mag.Loaded(it) <= 0) {
					Player.controlUseItem = false;
					// CC: pulling the trigger on an empty gun (with no ammo to reload) just clicks
					if (fire && !triggerHeld)
						SoundEngine.PlaySound(SoundID.Item98 with { Volume = 0.5f, Pitch = 0.6f }, Player.Center);
				}
				else if (spec != null) {
					// spin-up (CC ActivationDelay): the trigger must be held a moment before the first round flies
					if (spec.ActivationDelay > 0) {
						SpinUp = fire ? Math.Min(spec.ActivationDelay, SpinUp + 1) : Math.Max(0, SpinUp - 2);
						if (fire && SpinUp < spec.ActivationDelay) {
							Player.controlUseItem = false;
							if (SpinUp % 8 == 1)
								SoundEngine.PlaySound(SoundID.Item22 with { Volume = 0.5f, Pitch = -0.5f + SpinUp / (float)spec.ActivationDelay }, Player.Center);
						}
					}
					// bursts: one pull fires the whole burst, then the trigger must be released
					if (spec.Burst > 1) {
						if (fire && !triggerHeld && BurstLeft == 0)
							BurstLeft = spec.Burst;
						Player.controlUseItem = BurstLeft > 0;
						if (Player.controlUseItem)
							Player.releaseUseItem = true;
					}
				}
			}
			else if (Devices.IsThrowable(it)) {
				// wind up while the button is held; throw on release
				Player.controlUseItem = false;
				if (fire && Player.itemAnimation == 0) {
					throwHeld = true;
					ThrowCharge = Math.Min(1f, ThrowCharge + 1f / 45f);
				}
				else if (throwHeld) {
					Throw(it);
					throwHeld = false;
					ThrowCharge = 0f;
				}
			}
			triggerHeld = fire;
		}

		void Throw(Item it)
		{
			if (it.stack <= 0)
				return;
			Vector2 aimDir = AimDir();
			Vector2 from = RigRenderer.Grip(Player.GetModPlayer<RigPlayer>().Pose());
			float speed = MathHelper.Lerp(2.5f, 9f, ThrowCharge);   // CC: MinThrowVel .. MaxThrowVel
			Vector2 vel = aimDir * speed + Player.velocity * 0.5f;
			var source = Player.GetSource_ItemUse(it);
			int dmg = Player.GetWeaponDamage(it);
			Projectile.NewProjectile(source, from, vel, it.shoot, dmg, Player.GetWeaponKnockback(it), Player.whoAmI);
			if (--it.stack <= 0)
				it.TurnToAir();
			SoundEngine.PlaySound(SoundID.Item1, Player.Center);
			LastThrowSpeed = speed;
			Testing.AutoTest.Trace($"threw {ContentSamples.ItemsByType[it.type].Name} at {speed:0.0} px/tick (charge {ThrowCharge:0.00})");
		}

		Vector2 AimDir()
		{
			CCActor a = Actor;
			return (a.Flipped ? MathHelper.Pi + a.AimAngle : -a.AimAngle).ToRotationVector2();
		}

		// ------------------------------------------------------------ diggers dig along the aim

		public override bool PreItemCheck()
		{
			Item it = Held;
			if (Player.whoAmI == Main.myPlayer && Devices.IsDigger(it) && Player.controlUseItem) {
				Vector2 from = RigRenderer.Grip(Player.GetModPlayer<RigPlayer>().Pose());
				Vector2 dir = AimDir();
				const float range = 56f;
				Destruction.PixelMining.DigPoint = null;
				for (float d = 0; d <= range; d += 1f) {
					Vector2 p = from + dir * d;
					if (Collision.SolidCollision(p.Floor(), 1, 1)) {   // exactly the pixel containing p: its tile is the one dug
						Player.tileTargetX = (int)(p.X / 16f);
						Player.tileTargetY = (int)(p.Y / 16f);
						Destruction.PixelMining.DigPoint = p;
						break;
					}
				}
			}
			return true;
		}

		// ------------------------------------------------------------ firing (vanilla's use timing, CC's ballistics)

		/// <summary>Called for every shot of an aimed device (from the GlobalItem hook).</summary>
		internal void OnShoot(Item it, ref Vector2 position, ref Vector2 velocity, float knockback)
		{
			Vector2 dir = AimDir();
			float speed = velocity.Length();
			float cone = CurrentShake(it);
			float dev = Devices.Gaussian() * cone;                  // CC: shake x RandomNormalNum
			// Sweep from the shoulder: a muzzle beyond a thin wall must not fire through it.
			var pose = Player.GetModPlayer<RigPlayer>().Pose();
			Vector2 grip = RigRenderer.GunAt(pose);
			float length = TextureAssets.Item[it.type].IsLoaded ? TextureAssets.Item[it.type].Width() * 0.7f * it.scale : 20f;
			Vector2 shoulder = RigRenderer.Shoulder(pose);
			Vector2 reach = grip + dir * length - shoulder;
			float distance = reach.Length();
			position = shoulder;
			for (float step = 2; step <= distance + 2; step += 2) {
				Vector2 point = shoulder + reach * (Math.Min(step, distance) / Math.Max(1, distance));
				if (Destruction.PixelCollision.BoxSolid(point - Vector2.One, 2, 2))
					break;
				position = point;
			}
			// The gun is offset from the shoulder, especially when prone. Converge on the actual cursor.
			Vector2 aim = Main.MouseWorld - position;
			if (aim.LengthSquared() > 4f)
				dir = Vector2.Normalize(aim);
			velocity = dir.RotatedBy(dev) * speed;
			if (Testing.AutoTest.Enabled) {
				ShotLog.Add(Math.Abs(dev));
				ConeLog.Add(cone);
			}
			if (Devices.IsFirearm(it)) {
				var mag = it.GetGlobalItem<MagazineGlobalItem>();
				mag.Rounds = Math.Max(0, mag.Loaded(it) - 1);
				if (BurstLeft > 0)
					BurstLeft--;
			}
			// recoil pushes the whole body (heavier bodies barely notice); the gun jolts back
			if (Player.whoAmI == Main.myPlayer) {
				float push = knockback * 0.06f * CC.Mass / Actor.Mass;
				Player.velocity -= dir * push;
				GunKick = Math.Min(4f, GunKick + 1.5f + knockback * 0.2f);
			}
		}
	}

	public class DeviceGlobalItem : GlobalItem
	{
		public override void ModifyShootStats(Item item, Player player, ref Vector2 position, ref Vector2 velocity, ref int type, ref int damage, ref float knockback)
		{
			if (Devices.IsAimed(item) && ReferenceEquals(item, player.inventory[ActorInventory.Held]))
				player.GetModPlayer<AimPlayer>().OnShoot(item, ref position, ref velocity, knockback);
		}

		public override bool CanUseItem(Item item, Player player)
		{
			if (Devices.IsThrowable(item) && player.whoAmI == Main.myPlayer)
				return false; // thrown by hand with a wind-up (AimPlayer), not swung
			if (Devices.IsFirearm(item) && player.whoAmI == Main.myPlayer) {
				var aim = player.GetModPlayer<AimPlayer>();
				if (aim.Reloading || item.GetGlobalItem<MagazineGlobalItem>().Loaded(item) <= 0)
					return false;
				if (item.ModItem is CCFirearm cc && cc.Spec.ActivationDelay > 0 && aim.SpinUp < cc.Spec.ActivationDelay)
					return false;   // still spinning up
			}
			return true;
		}
	}

	/// <summary>
	/// CC's camera: the view looks ahead along the aim, by the held device's sharp-aim reach as sharp aim builds up; and a
	/// dotted sight line along the aim shows the current spread (it widens with shake).
	/// </summary>
	public class SharpAimCamera : ModSystem
	{
		static Vector2 offset;

		public override void ModifyScreenPosition()
		{
			Player p = Main.LocalPlayer;
			if (Main.gameMenu || p.dead || !p.active || Testing.AutoTest.Enabled) {
				offset = Vector2.Zero;
				return;
			}
			CCActor a = p.GetModPlayer<BodyPhysicsPlayer>().Actor;
			float reach = Devices.SharpLength(p.inventory[ActorInventory.Held]);
			Vector2 dir = (a.Flipped ? MathHelper.Pi + a.AimAngle : -a.AimAngle).ToRotationVector2();
			Vector2 want = dir * (30f + reach * a.SharpAim);       // CC AimDistance 30 + SharpLength x progress
			offset = Vector2.Lerp(offset, want, 0.12f);
			Main.screenPosition += offset;
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int idx = layers.FindIndex(l => l.Name == "Vanilla: Cursor");
			if (idx < 0)
				idx = layers.Count;
			layers.Insert(idx, new LegacyGameInterfaceLayer("TerraCommand: Aim sight", () => {
				Player p = Main.LocalPlayer;
				if (Main.gameMenu || p.dead || p.mouseInterface || UI.ActorControlsUI.PieOpen || !Devices.IsAimed(p.inventory[ActorInventory.Held]))
					return true;
				var aim = p.GetModPlayer<AimPlayer>();
				CCActor a = p.GetModPlayer<BodyPhysicsPlayer>().Actor;
				Vector2 dir = (a.Flipped ? MathHelper.Pi + a.AimAngle : -a.AimAngle).ToRotationVector2();
				Vector2 from = RigRenderer.GunAt(p.GetModPlayer<RigPlayer>().Pose());
				float len = 60f + Devices.SharpLength(p.inventory[ActorInventory.Held]) * (0.4f + 0.6f * a.SharpAim);
				Texture2D px = TextureAssets.MagicPixel.Value;
				// a line of dots along the aim, fanning into two as the spread widens
				for (float d = 24f; d < len; d += 10f) {
					float w = d * MathF.Tan(aim.Spread);
					Vector2 side = new Vector2(-dir.Y, dir.X) * w;
					Color c = Color.White * (0.55f * (1f - d / len) + 0.15f);
					foreach (Vector2 s in w < 1.5f ? new[] { Vector2.Zero } : new[] { side, -side }) {
						Vector2 at = from + dir * d + s - Main.screenPosition;
						Main.spriteBatch.Draw(px, at, new Rectangle(0, 0, 1, 1), c, 0f, new Vector2(0.5f), 2f, SpriteEffects.None, 0f);
					}
				}
				return true;
			}, InterfaceScaleType.Game));
		}
	}
}
