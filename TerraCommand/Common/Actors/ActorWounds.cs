using System;
using System.Collections.Generic;
using System.IO;

namespace TerraCommand.Common.Actors
{
    public enum WoundKind : byte { Entry, Exit, TorsoExit, Stump, StrongEntry }

    /// <summary>
    /// Resolved Base.rte/Effects/Wounds.ini emitters at 60 Hz. Each stream has its own
    /// accumulator; bursts count toward the emission limit, but deal burst damage only.
    /// This state belongs to a physical body, never to the controlling player.
    /// </summary>
    public sealed class ActorWounds
    {
        sealed class Emitter
        {
            public byte Region;
            public WoundKind Kind;
            public int Age, Emitted;
            public bool Burst;
            public Emitter Clone() => (Emitter)MemberwiseClone();
        }
        public const int MaxEmitters = 128;
        readonly List<Emitter> emitters = new();
        public float Remainder { get; private set; }
        public uint Revision { get; private set; }
        public int Count => emitters.Count;
        public bool Bleeding => emitters.Exists(e => e.Burst || e.Emitted <= Limit(e.Kind));

        static int Limit(WoundKind kind) => kind switch { WoundKind.StrongEntry => 50, WoundKind.Entry => 40, WoundKind.Exit => 80, _ => 480 };
        static float BurstDamage(WoundKind kind) => kind switch { WoundKind.StrongEntry => 2, WoundKind.Entry => 3, WoundKind.Exit => 1, WoundKind.TorsoExit => 2, _ => 30 };
        static float EmissionDamage(WoundKind kind) => kind == WoundKind.Stump ? .2f : .05f;
        static int BurstCount(WoundKind kind) => kind switch { WoundKind.Entry or WoundKind.StrongEntry => 6, WoundKind.Exit => 10, WoundKind.TorsoExit => 11, _ => 9 };
        // Entry: 30+30/min. Exit: 60+60/min. Deadly exit adds 0+60/min.
        // Body/stump inherits an inactive blood stream and adds 60+120/min.
        static int Emissions(WoundKind kind, int age)
        {
            int Every(int ticks) => age % ticks == 0 ? 1 : 0;
            return kind switch {
                WoundKind.Entry or WoundKind.StrongEntry => 2 * Every(120),
                WoundKind.Exit => 2 * Every(60),
                WoundKind.TorsoExit => 3 * Every(60),
                _ => Every(60) + Every(30)
            };
        }

        /// <param name="region">0..3 limbs, 4 torso, 5 head.</param>
        /// <param name="burst">False when Terraria has already delivered the initial impact damage.</param>
        public bool Add(int region, WoundKind kind = WoundKind.Entry, bool burst = false)
        {
            if (region < 0 || region > 5 || (byte)kind > (byte)WoundKind.StrongEntry || emitters.Count >= MaxEmitters) return false;
            emitters.Add(new Emitter { Region = (byte)region, Kind = kind, Burst = burst, Emitted = burst ? 0 : BurstCount(kind) });
            Revision++;
            return true;
        }

        public void Detach(int region, bool flesh)
        {
            emitters.RemoveAll(e => e.Region == region);
            if (flesh) Add(4, WoundKind.Stump, burst: true);
            Revision++;
        }

        /// <summary>Whole health points due this tick; fractional damage survives save, swap and reload.</summary>
        public int Tick()
        {
            float damage = Remainder;
            foreach (var e in emitters) {
                if (!e.Burst && e.Emitted > Limit(e.Kind)) continue;
                e.Age++;
                int count = Emissions(e.Kind, e.Age);
                if (e.Burst) {
                    count += BurstCount(e.Kind);
                    damage += BurstDamage(e.Kind);
                    e.Burst = false;
                } else damage += count * EmissionDamage(e.Kind);
                e.Emitted += count;
            }
            int whole = (int)MathF.Floor(damage + .00001f);
            Remainder = Math.Max(0, damage - whole);
            return whole;
        }

        public void Clear() { emitters.Clear(); Remainder = 0; Revision++; }
        public void CopyFrom(ActorWounds other)
        {
            if (ReferenceEquals(this, other)) return;
            emitters.Clear();
            foreach (var e in other.emitters) emitters.Add(e.Clone());
            Remainder = other.Remainder;
            Revision++;
        }
        public void Write(BinaryWriter writer)
        {
            writer.Write((byte)1); // versioned independently of movement snapshots
            writer.Write((byte)emitters.Count);
            writer.Write(Remainder);
            foreach (var e in emitters) {
                writer.Write(e.Region); writer.Write((byte)e.Kind);
                writer.Write(e.Age); writer.Write(e.Emitted); writer.Write(e.Burst);
            }
        }
        public static ActorWounds Read(BinaryReader reader)
        {
            if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported actor wound state");
            int count = reader.ReadByte();
            float remainder = reader.ReadSingle();
            if (count > MaxEmitters || !float.IsFinite(remainder) || remainder < 0 || remainder >= 1)
                throw new InvalidDataException("Invalid actor wound state");
            var state = new ActorWounds { Remainder = remainder };
            for (int i = 0; i < count; i++) {
                var e = new Emitter { Region = reader.ReadByte(), Kind = (WoundKind)reader.ReadByte(), Age = reader.ReadInt32(), Emitted = reader.ReadInt32(), Burst = reader.ReadBoolean() };
                if (e.Region > 5 || (byte)e.Kind > (byte)WoundKind.StrongEntry || e.Age < 0 || e.Age > 100000 || e.Emitted < 0 || e.Emitted > 500)
                    throw new InvalidDataException("Invalid wound emitter");
                state.emitters.Add(e);
            }
            return state;
        }
    }
}
