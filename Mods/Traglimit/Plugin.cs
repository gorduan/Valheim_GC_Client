using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModTraglimit;

/// <summary>
/// GC_Traglimit (Gorduan), Stufe 2 „Vanilla“: Nimmt beim Aufheben und beim Strg+Klick in Kisten nur so viel, wie du noch tragen kannst.
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
		CarryLimit.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("carrylimit", CarryLimit.Plugin.Enabled, () => CarryLimit.Plugin.Blocked, "GC_CarryLimit")
			.Sub("carrylimit_mode", CarryLimit.Plugin.Mode)
			.Sub("carrylimit_key", CarryLimit.Plugin.Key)
			.Sub("carrylimit_auto", CarryLimit.Plugin.AutoPickupPartial)
			.Sub("carrylimit_chest", CarryLimit.Plugin.ChestCtrlClick);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Traglimit", CarryLimit.Plugin.Update);
	}
}
