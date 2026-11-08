using System.IO;
using Microsoft.Xna.Framework;

namespace TerraCommand.Common.Actors
{
	/// <summary>Physical state belongs to the body, including while control changes owners.</summary>
	public sealed class ActorMotion
	{
		public Vector2 Velocity;
		public float Rotation, AngularVelocity, Fuel = -1f, Crouch, Aim;
		public ActorStatus Status;
		public ProneState Prone;
		public bool Grounded = true, Flipped;
		public int RecoveryTicks;

		public static ActorMotion Capture(CCActor actor, Vector2 velocity) => new() {
			Velocity = velocity, Rotation = actor.Rot, AngularVelocity = actor.AngVel,
			Fuel = actor.Fuel, Crouch = actor.CrouchAmount, Aim = actor.AimAngle,
			Status = actor.Status, Prone = actor.Prone, Grounded = actor.Grounded,
			Flipped = actor.Flipped, RecoveryTicks = actor.UnstableTicks,
		};

		public void Apply(CCActor actor, ActorKind kind)
		{
			ActorStats stats = ActorStats.Get(kind);
			actor.FuelMax = stats.Fuel;
			actor.JetForce = stats.JetForce;
			actor.Apply(stats, 0);
			actor.Vel = Velocity;
			actor.Rot = Rotation;
			actor.AngVel = AngularVelocity;
			actor.Fuel = Fuel < 0 ? stats.Fuel : System.Math.Max(0, Fuel);
			actor.CrouchAmount = Crouch;
			actor.AimAngle = Aim;
			actor.Status = Status;
			actor.Prone = Prone;
			actor.Grounded = Grounded;
			actor.Flipped = Flipped;
			actor.UnstableTicks = RecoveryTicks;
			actor.SharpAim = 0;
			actor.Jetting = actor.WasJetting = false;
			actor.Move = MoveState.Stand;
			foreach (Gait leg in actor.Legs)
				leg.Terminate();
		}

		public void Write(BinaryWriter w)
		{
			w.Write(Velocity.X); w.Write(Velocity.Y); w.Write(Rotation); w.Write(AngularVelocity);
			w.Write(Fuel); w.Write(Crouch); w.Write(Aim); w.Write((byte)Status); w.Write((byte)Prone);
			w.Write(Grounded); w.Write(Flipped); w.Write(RecoveryTicks);
		}

		public static ActorMotion Read(BinaryReader r) => new() {
			Velocity = new Vector2(r.ReadSingle(), r.ReadSingle()), Rotation = r.ReadSingle(), AngularVelocity = r.ReadSingle(),
			Fuel = r.ReadSingle(), Crouch = r.ReadSingle(), Aim = r.ReadSingle(), Status = (ActorStatus)r.ReadByte(),
			Prone = (ProneState)r.ReadByte(), Grounded = r.ReadBoolean(), Flipped = r.ReadBoolean(), RecoveryTicks = r.ReadInt32(),
		};

		// Store the same compact representation in world saves; old bodies load with fresh physical state.
		public byte[] Save()
		{
			using var stream = new MemoryStream();
			using var writer = new BinaryWriter(stream);
			Write(writer);
			return stream.ToArray();
		}

		public static ActorMotion Load(byte[] bytes)
		{
			if (bytes.Length == 0)
				return new();
			using var stream = new MemoryStream(bytes);
			using var reader = new BinaryReader(stream);
			return Read(reader);
		}
	}
}
