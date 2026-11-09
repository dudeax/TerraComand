using Microsoft.Xna.Framework;
using Terraria;

namespace TerraCommand.Common.Actors
{
	/// <summary>Shared posture envelope for player and NPC hosts. Feet and horizontal centre stay fixed on resize.</summary>
	public static class ActorBounds
	{
		public static void Fit(Vector2 position, int width, int height, CCActor actor, out int fitWidth, out int fitHeight)
		{
			int wantedHeight = actor.WantedHeight(), wantedWidth = actor.WantedWidth();
			Vector2 feet = position + new Vector2(width / 2f, height);
			fitHeight = wantedHeight;
			if (wantedHeight > height) {
				fitHeight = height;
				for (int h = wantedHeight; h > height; h--)
					if (!Collision.SolidCollision(feet - new Vector2(width / 2f, h), width, h - 1)) {
						fitHeight = h;
						break;
					}
			}
			fitWidth = wantedWidth;
			if (wantedWidth > width) {
				fitWidth = width;
				for (int w = wantedWidth; w > width; w--)
					if (!Collision.SolidCollision(feet - new Vector2(w / 2f, fitHeight), w, fitHeight - 1)) {
						fitWidth = w;
						break;
					}
			}
		}

		public static void Resize(NPC npc, CCActor actor)
		{
			Fit(npc.position, npc.width, npc.height, actor, out int width, out int height);
			npc.position += new Vector2((npc.width - width) / 2f, npc.height - height);
			npc.width = width;
			npc.height = height;
		}
	}
}
