using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Client.ModKompass;

/// <summary>
/// GC_Kompass (Gorduan), Stufe 1 „Sehr Vanilla“: Kompass oben am Bildschirm mit Kartenmarkierungen, Mitspielern und einem goldenen Ziel per Alt+Linksklick auf der Karte.
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
		Compass.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("compass", Compass.Plugin.Enabled)
			.Sub("compass_focus_key", Compass.Plugin.FocusModifier)
			.Sub("compass_width", Compass.Plugin.WidthPercent, 5, " %")
			.Sub("compass_fov", Compass.Plugin.FieldOfView, 15, "°")
			.Sub("compass_offset", Compass.Plugin.OffsetY, 10, " px")
			.Sub("compass_scale", Compass.Plugin.Scale, 0.1, " ×")
			.Sub("compass_range", Compass.Plugin.MaxDistance, 250, " m")
			.Sub("compass_names", Compass.Plugin.OtherNames)
			.Sub("compass_name_range", Compass.Plugin.NameDistance, 25, " m")
			.Sub("compass_distance", Compass.Plugin.ShowDistance)
			.Sub("compass_checked", Compass.Plugin.ShowChecked)
			.Sub("compass_players", Compass.Plugin.ShowPlayers)
			.Sub("compass_icons", Compass.Plugin.Icons);
	}
}
