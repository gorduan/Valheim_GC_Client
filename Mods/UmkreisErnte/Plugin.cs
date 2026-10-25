using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModUmkreisErnte;

/// <summary>
/// GC_UmkreisErnte (Gorduan), Stufe 3 „Vanilla+“: Erntet alle Pflanzen und alles Aufsammelbare gleicher Sorte im Umkreis auf einmal.
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
		MassHarvest.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("massharvest", MassHarvest.Plugin.Enabled, () => MassHarvest.Plugin.Blocked, "GC_MassHarvest")
			.Sub("massharvest_radius", MassHarvest.Plugin.Radius, 0.5, " m")
			.Sub("massharvest_max", MassHarvest.Plugin.MaxPerTick)
			.Sub("massharvest_all", MassHarvest.Plugin.Everything)
			.Sub("massharvest_hold", MassHarvest.Plugin.RequireHold);
	}
}
