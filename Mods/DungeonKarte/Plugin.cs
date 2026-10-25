using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModDungeonKarte;

/// <summary>
/// GC_DungeonKarte (Gorduan), Stufe 3 „Vanilla+“: Karte in Dungeons, klein in der Ecke und groß mit der Kartentaste.
/// Früher Teil von GC_Client. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		GC.Client.ClientPlugin.Log = Logger;
		GC.Client.ClientPlugin.Module = Module;
		Harmony harmony = new(GcMod.Guid);
		DungeonMap.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcHooks.DungeonMapLargeOpen = () => DungeonMap.Plugin.View != null && DungeonMap.Plugin.View.LargeOpen;
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("dungeonmap", DungeonMap.Plugin.Enabled, () => DungeonMap.Plugin.Blocked, "GC_DungeonMap")
			.Sub("dungeonmap_size", DungeonMap.Plugin.SmallSize, 20, " px")
			.Sub("dungeonmap_zoom", DungeonMap.Plugin.SmallZoom, 0.5)
			.Sub("dungeonmap_minimap", DungeonMap.Plugin.HideVanillaMinimap);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Dungeon-Karte", DungeonMap.Plugin.Update);
	}
}
