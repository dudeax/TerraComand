using System;
namespace TerraCommand.Common.Physics {
	/// <summary>CC material values: integrity is momentum per source pixel (kg*m/s).</summary>
	public readonly record struct PhysicalMaterial(float Integrity, float Friction, float Restitution, float Density) {
		public static readonly PhysicalMaterial Earth = new(70,.85f,.1f,2), Sand = new(45,.95f,.075f,1.6f),
			Stone = new(140,.9f,.6f,2.5f), Concrete = new(200,.9f,.3f,2.3f), Metal = new(400,.9f,.2f,8.5f),
			Ice = new(70,.15f,.3f,2), Wood = new(60,1,.4f,.5f), Rubber = new(60,.9f,.2f,.8f),
			Armour = new(160,.4f,.5f,5);
	}
	public static class MaterialSolver {
		// SceneMan::TryPenetrate: retardation = -integrity/|impulse|. A 2px cell covers four source pixels.
		public static bool Penetration(float momentum, float integrity, float area, out float loss) {
			if(!float.IsFinite(momentum) || momentum<=0 || integrity<0 || area<=0) { loss=1; return false; }
			float resistance = integrity * area;
			loss = momentum > 0 ? Math.Clamp(resistance / momentum,0,1) : 1;
			return momentum > 0 && momentum >= resistance && float.IsFinite(resistance);
		}
	}
}
