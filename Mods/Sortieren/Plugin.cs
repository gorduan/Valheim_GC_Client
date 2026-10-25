using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModSortieren;

/// <summary>
/// GC_Sortieren (Gorduan), Stufe 2 „Vanilla“: Sortiert Inventar und offene Kisten per Knopf oder Taste. Fixierte Items und die Schnellleiste bleiben, gleiche Stapel werden zusammengeführt.
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
		Sort.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("sort", Sort.Plugin.Enabled, () => Sort.Plugin.Blocked, "GC_Sort")
			.Sub("sort_mode", Sort.Plugin.Mode)
			.Sub("sort_hotbar", Sort.Plugin.SkipHotbar)
			.Sub("sort_merge", Sort.Plugin.MergeStacks)
			.Sub("sort_key_player", Sort.Plugin.SortPlayerKey)
			.Sub("sort_key_container", Sort.Plugin.SortContainerKey);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Sortieren", Sort.Plugin.Update);
	}
}
