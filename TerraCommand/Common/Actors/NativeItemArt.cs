using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace TerraCommand.Common.Actors
{
    /// <summary>Imported world equipment uses the same art pixel size as the actor holding it. UI icons keep UI scale.</summary>
    public class NativeItemArt : GlobalItem
    {
        public override void SetDefaults(Item item) {
            if(item.ModItem?.Mod.Name!="TerraCommand")return;
            item.width*=CC.WorldScale;item.height*=CC.WorldScale;
        }
        public override bool PreDrawInWorld(Item item,SpriteBatch spriteBatch,Color lightColor,Color alphaColor,ref float rotation,ref float scale,int whoAmI) {
            if(item.ModItem?.Mod.Name=="TerraCommand")scale*=CC.WorldScale;
            return true;
        }
    }
}
