using System;
using Microsoft.Xna.Framework;

namespace TerraCommand.Common.Actors
{
    public readonly record struct JointTransfer(Vector2 Transmitted, bool Detached, bool Gibbed);

    /// <summary>Attachable::TransferJointImpulses. Arguments and results use source N*s.</summary>
    public readonly record struct LivingJoint(float Strength, float Stiffness, float GibLimit, int WoundLimit)
    {
        public static LivingJoint For(ActorKind kind, int limb) => limb < 2
            ? new(480, .5f, 720, kind == ActorKind.Heavy ? 11 : 10)
            : kind == ActorKind.Heavy ? new(360, .5f, 540, 10) : new(320, .5f, 480, 8);

        public JointTransfer Transfer(Vector2 accumulatedImpulse)
        {
            if (!float.IsFinite(accumulatedImpulse.X) || !float.IsFinite(accumulatedImpulse.Y))
                return default;
            Vector2 load = accumulatedImpulse * Stiffness;
            float magnitude = load.Length();
            if (!float.IsFinite(magnitude)) return default;
            float gibLimit = Strength == 0 ? 0 : Math.Max(GibLimit, Strength);
            bool gibbed = gibLimit > 0 && magnitude > gibLimit;
            bool detached = gibbed || Strength > 0 && magnitude > Strength;
            float cap = gibbed ? gibLimit : detached ? Strength : magnitude;
            return new(magnitude > 0 ? load * (cap / magnitude) : Vector2.Zero, detached, gibbed);
        }
    }
}
