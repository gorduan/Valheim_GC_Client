using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModTueren;

/// <summary>
/// GC_Tueren (Gorduan), Stufe 3 „Vanilla+“: Türen öffnen sich vor dir und schließen hinter dir. Bei Gefahr und während eines Raids bleiben sie zu.
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
		AutoDoor.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("autodoor", AutoDoor.Plugin.Enabled, () => AutoDoor.Plugin.Blocked, "GC_AutoDoor")
			.Sub("autodoor_open", AutoDoor.Plugin.OpenDistance, 0.5, " m")
			.Sub("autodoor_close", AutoDoor.Plugin.CloseDistance, 0.5, " m")
			.Sub("autodoor_enemy", AutoDoor.Plugin.EnemyRange, 5, " m")
			.Sub("autodoor_raid", AutoDoor.Plugin.BlockDuringRaid)
			.Sub("autodoor_danger", AutoDoor.Plugin.CloseWhenDanger)
			.Sub("autodoor_key", AutoDoor.Plugin.ToggleKey);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Türen", AutoDoor.Plugin.Update);
	}
}
