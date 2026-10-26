using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModEinsammeln;

/// <summary>
/// GC_Einsammeln (Gorduan), Stufe 3 „Vanilla+“: Sammelt herumliegende Items beim Ziehen des Karrens oder auf dem Schiff automatisch ein.
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
		CartCollect.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("cartcollect", CartCollect.Plugin.Enabled, () => CartCollect.Plugin.Blocked, "GC_CartCollect")
			.Sub("cartcollect_range", CartCollect.Plugin.Range, 1, " m")
			.Sub("cartcollect_matching", CartCollect.Plugin.OnlyMatching)
			.Sub("cartcollect_ships", CartCollect.Plugin.IncludeShips)
			.Sub("cartcollect_pulling", CartCollect.Plugin.OnlyWhilePulling)
			.Sub("cartcollect_key", CartCollect.Plugin.ToggleKey);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Einsammeln", CartCollect.Plugin.Update);
	}
}
