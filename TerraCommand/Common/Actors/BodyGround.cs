using Microsoft.Xna.Framework;
using Terraria;

namespace TerraCommand.Common.Actors
{
	/// <summary>
	/// How a CC body meets pixel terrain (players and idle bodies alike). Terraria's 20px-wide hitbox can rest on its very
	/// edge, on one pixel of a rim, while the body's feet hang over a hole; and walking down rough ground it drops a pixel at
	/// a time, flickering between standing and falling. A CC actor stands on its feet: it slips off edges it can't stand on,
	/// and walks down small drops without leaving the ground.
	/// </summary>
	public static class BodyGround
	{
		public const float FeetHalfWidth = 4f;     // the feet occupy the middle of the (14px) hitbox
		public const float MaxStepDown = 6f;

		static bool Solid(float x, float y, float w, float h) => Collision.SolidCollision(new Vector2(x, y), (int)w, (int)h);

		/// <summary>
		/// Standing on the very edge of the hitbox with the feet over nothing: which way to slip (-1 / +1), or 0 if the feet
		/// are supported (or it isn't standing on anything).
		/// </summary>
		public static int SlipDirection(Vector2 pos, int w, int h)
		{
			float bottom = pos.Y + h, center = pos.X + w / 2f;
			if (Solid(center - FeetHalfWidth, bottom, FeetHalfWidth * 2f, 3f))
				return 0;
			bool left = Solid(pos.X, bottom, center - FeetHalfWidth - pos.X, 2f);
			bool right = Solid(center + FeetHalfWidth, bottom, pos.X + w - (center + FeetHalfWidth), 2f);
			if (left == right)
				return 0;
			return left ? 1 : -1;      // away from the side holding it up
		}

		/// <summary>
		/// Standing over a shallow dip (held up by its rims): how far below the hitbox the ground under the feet is (0..6 px).
		/// The body is drawn that much lower, so its feet stand in the dip instead of hovering over it.
		/// </summary>
		public static float FeetSink(Vector2 pos, int w, int h)
		{
			float bottom = pos.Y + h, center = pos.X + w / 2f;
			for (float d = 0f; d <= MaxStepDown; d += 1f)
				if (Solid(center - FeetHalfWidth, bottom + d, FeetHalfWidth * 2f, 1f))
					return d;
			return 0f;   // nothing within reach: the feet just hang
		}

		/// <summary>The distance down to the ground under the feet, if it's within <see cref="MaxStepDown"/> (else -1).</summary>
		public static float DropToGround(Vector2 pos, int w, int h)
		{
			float bottom = pos.Y + h;
			for (float d = 0.5f; d <= MaxStepDown; d += 0.5f)
				if (Solid(pos.X + 2, bottom + d - 0.5f, w - 4, 1f))
					return d - 0.5f;
			return -1f;
		}
	}
}
