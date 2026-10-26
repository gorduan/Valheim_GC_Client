using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModVernichten;

/// <summary>
/// GC_Vernichten (Gorduan), Stufe 2 „Vanilla“: Vernichtet Items über ein Feld im Inventar oder per Taste, mit Rückfrage.
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
		Trash.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("trash", Trash.Plugin.Enabled, () => Trash.Plugin.Blocked, "GC_Trash")
			.Sub("trash_confirm", Trash.Plugin.Confirm)
			.Sub("trash_button", Trash.Plugin.ShowButton)
			.Sub("trash_key", Trash.Plugin.TrashKey);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Vernichten", Trash.Plugin.Update);
	}
}
