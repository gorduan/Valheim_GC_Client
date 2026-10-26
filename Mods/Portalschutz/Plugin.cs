using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModPortalschutz;

/// <summary>
/// GC_Portalschutz (Gorduan), Stufe 1 „Sehr Vanilla“: Fragt nach, bevor ein Portal umbenannt wird, und merkt sich den vorherigen Namen.
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
		PortalGuard.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcHooks.PortalGuardActive = () => PortalGuard.Plugin.On;
		GcHooks.PortalRename = PortalGuard.Plugin.Rename;
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("portalguard", PortalGuard.Plugin.Enabled, () => PortalGuard.Plugin.Blocked, "GC_PortalGuard")
			.Sub("portalguard_confirm", PortalGuard.Plugin.Confirm)
			.Sub("portalguard_key", PortalGuard.Plugin.RestoreModifier);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Portalschutz", PortalGuard.Plugin.Update);
	}
}
