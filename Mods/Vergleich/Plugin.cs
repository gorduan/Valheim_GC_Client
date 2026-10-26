using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModVergleich;

/// <summary>
/// GC_Vergleich (Gorduan), Stufe 1 „Sehr Vanilla“: Zeigt neben dem Tooltip, was du gerade ausgerüstet hast. Gewinn grün, Verlust rot.
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
		Compare.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("compare", Compare.Plugin.Enabled)
			.Sub("compare_mode", Compare.Plugin.Mode)
			.Sub("compare_key", Compare.Plugin.Key)
			.Sub("compare_details", Compare.Plugin.Details);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Vergleich", Compare.Plugin.Update);
	}
}
