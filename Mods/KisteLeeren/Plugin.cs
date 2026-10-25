using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModKisteLeeren;

/// <summary>
/// GC_KisteLeeren (Gorduan), Stufe 2 „Vanilla“: Umschalt+E an einer Kiste nimmt alles heraus, ohne sie zu öffnen.
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
		QuickTake.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("quicktake", QuickTake.Plugin.Enabled, () => QuickTake.Plugin.Blocked, "GC_QuickTake")
			.Sub("quicktake_key", QuickTake.Plugin.Modifier);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Kiste leeren", QuickTake.Plugin.Update);
	}
}
