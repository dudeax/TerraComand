using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
namespace TerraCommand.Common.Destruction {
	/// <summary>Sub-cell fracture work: stopped rounds spend their whole budget without receiving free continuation.</summary>
	public class BallisticFractures : ModSystem {
		static readonly Dictionary<(int X,int Y),(ushort Type,float Work)> work=new();
		public override void ClearWorld()=>work.Clear();
		public static void Clear(Vector2 point)=>work.Remove(((int)MathF.Floor(point.X/2),(int)MathF.Floor(point.Y/2)));
		public static float Resistance(Vector2 point) {
			int x=(int)MathF.Floor(point.X/2),y=(int)MathF.Floor(point.Y/2);ushort type=TerrainRules.Substrate(Main.tile[x/8,y/8].TileType);
			float full=TerrainRules.Material(type).Integrity*global::TerraCommand.Common.Actors.CC.TerrainCellArea;
			return Math.Max(0,full-(work.TryGetValue((x,y),out var old)&&old.Type==type?old.Work:0));
		}
		public static void Spend(Vector2 point,float amount) {
			int x=(int)MathF.Floor(point.X/2),y=(int)MathF.Floor(point.Y/2);ushort type=TerrainRules.Substrate(Main.tile[x/8,y/8].TileType);
			float full=TerrainRules.Material(type).Integrity*global::TerraCommand.Common.Actors.CC.TerrainCellArea,paid=full-Resistance(point)+amount;
			if(paid>=full-.0001f) { Clear(point);PixelTerrain.PenetrateCell(point); }
			else work[(x,y)]=(type,paid);
		}
		public static void ClearTile(int x,int y) { for(int a=0;a<8;a++)for(int b=0;b<8;b++)work.Remove((x*8+a,y*8+b)); }
		public override void SaveWorldData(TagCompound tag) { var records=new List<TagCompound>();foreach(var pair in work) records.Add(new TagCompound{{"x",pair.Key.X},{"y",pair.Key.Y},{"type",(int)pair.Value.Type},{"work",pair.Value.Work}});tag["ballisticFractures"]=records;tag["fractureCellArea"]=global::TerraCommand.Common.Actors.CC.TerrainCellArea; }
		public override void LoadWorldData(TagCompound tag) { work.Clear();float oldArea=tag.ContainsKey("fractureCellArea")?tag.GetFloat("fractureCellArea"):4; if(!float.IsFinite(oldArea)||oldArea<=0)oldArea=4;float ratio=global::TerraCommand.Common.Actors.CC.TerrainCellArea/oldArea;foreach(var record in tag.GetList<TagCompound>("ballisticFractures")) { float paid=record.GetFloat("work")*ratio;if(float.IsFinite(paid)&&paid>0)work[(record.GetInt("x"),record.GetInt("y"))]=((ushort)record.GetInt("type"),paid); } }
	}
	public class FractureTile : GlobalTile {
		public override void KillTile(int i,int j,int type,ref bool fail,ref bool effectOnly,ref bool noItem) { if(!fail&&!effectOnly)BallisticFractures.ClearTile(i,j); }
		public override void PlaceInWorld(int i,int j,int type,Item item)=>BallisticFractures.ClearTile(i,j);
	}
}
