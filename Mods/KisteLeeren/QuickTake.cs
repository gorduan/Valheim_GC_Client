using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using UnityEngine;

namespace GC.Client.QuickTake;

/// <summary>
/// GC_QuickTake: Kiste leeren mit Umschalt+E.
///
/// Umschalt (einstellbar, wie Valheims „alternative Benutzung") halten und eine Kiste, einen Karren oder ein Schiff
/// benutzen: alles wird genommen, ohne das Kistenfenster zu öffnen. Ohne Umschalt bleibt alles wie in Vanilla, auch
/// „E halten = Stapel ablegen". Bei verbundenen Kisten wird nur die angeschaute Kiste geleert.
///
/// Ablauf wie beim normalen Öffnen, damit im Mehrspieler nichts verdoppelt wird oder verloren geht: Vanilla
/// Container.Interact prüft Ward und Privatsphäre und fragt beim Besitzer an (RPC_RequestOpen). Der gibt die Kiste mit
/// den aktuellen Daten an uns ab. Statt das Fenster zu zeigen, wartet die Mod, bis die Kiste wirklich uns gehört,
/// lädt sie frisch und nimmt dann alles über „Alles nehmen" (InventoryGui.OnTakeAll), sodass ValheimPlusPlus fixierte
/// Items in der Kiste lässt. Andere Mods, die das Öffnen verbieten (z. B. Guilds_Vehicle), gelten auch hier.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_quicktake";
	public const string ModName = "GC_QuickTake";
	public const string Version = "1.1.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<KeyCode> Modifier = null!;

	/// <summary>Kiste und Zeitpunkt der letzten Umschalt+E-Anfrage: deren nächste Öffnen-Antwort wird zu „alles nehmen".</summary>
	internal static Container? RequestedFor;
	internal static float RequestedAt = -100f;

	/// <summary>Kiste, die uns gerade übergeben wird. Genommen wird, sobald sie uns gehört.</summary>
	internal static Container? Waiting;
	internal static float WaitingSince;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "3 - QuickTake", "Enabled", true, "Kiste mit Umschalt+E leeren, ohne sie zu öffnen (auch im Menü GC Option).");
		Modifier = Gc.Bind(Config, "3 - QuickTake", "TakeAllModifier", KeyCode.LeftShift, "Diese Taste halten und eine Kiste benutzen (E): alles nehmen statt öffnen.");
	}

	internal static void Update()
	{
		if (Waiting == null)
		{
			return;
		}
		Container container = Waiting;
		Player? player = Player.m_localPlayer;
		if (container == null || container.m_nview == null || !container.m_nview.IsValid() || player == null)
		{
			Waiting = null;
			return;
		}
		if (!container.m_nview.IsOwner())
		{
			if (Time.time - WaitingSince > 3f)
			{
				Waiting = null;
				Gc.Center(GcLoc.Get("gcq_timeout"));
			}
			return; // die Kiste wird noch übergeben
		}
		Waiting = null;
		if (container.IsInUse())
		{
			return; // inzwischen offen (z. B. als Teil einer verbundenen Ansicht): dort nehmen, nicht hier alles
		}
		container.Load(); // aktuelle Daten aus dem ZDO, falls die Übergabe gerade erst angekommen ist
		Inventory chest = container.GetInventory();
		int before = chest.NrOfItemsIncludingStacks();
		InventoryGui gui = InventoryGui.instance;
		Container? previous = gui.m_currentContainer;
		gui.m_currentContainer = container; // OnTakeAll arbeitet mit der offenen Kiste; so greift auch die Fixier-Logik
		try
		{
			gui.OnTakeAll();
		}
		finally
		{
			gui.m_currentContainer = previous;
		}
		int taken = before - chest.NrOfItemsIncludingStacks();
		Gc.TopLeft(taken > 0 ? GcLoc.Get("gcq_taken", Localization.instance.Localize(container.m_name), taken) : GcLoc.Get("gcq_nothing"));
	}
}

/// <summary>Umschalt+E: wie Öffnen anfragen, aber die Antwort wird zu „alles nehmen" (siehe OpenResponsePatch).</summary>
[HarmonyPatch(typeof(Container), nameof(Container.Interact))]
internal static class ContainerInteractPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Prefix(Container __instance, Humanoid character, bool hold, bool __runOriginal)
	{
		if (character != Player.m_localPlayer || hold)
		{
			return;
		}
		Plugin.RequestedFor = null; // jedes normale Benutzen hebt eine alte Umschalt+E-Anfrage auf
		if (!__runOriginal || !Plugin.On || Plugin.Modifier.Value == KeyCode.None || !Input.GetKey(Plugin.Modifier.Value) || Gc.TextInputActive())
		{
			return; // ein anderer Prefix hat das Öffnen schon verboten, oder keine Umschalttaste
		}
		if (character.IsRunning())
		{
			return; // Umschalt ist auch Sprinten: wer auf die Kiste zurennt und E drückt, will sie öffnen
		}
		if (__instance.GetInventory() != null && __instance.GetInventory().NrOfItems() > 0)
		{
			Plugin.RequestedFor = __instance;
			Plugin.RequestedAt = Time.time;
		}
	}
}

/// <summary>Antwort des Besitzers: bei Umschalt+E kein Fenster, sondern auf die Übergabe warten und dann alles nehmen.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.RPC_OpenResponse))]
internal static class OpenResponsePatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(Container __instance, bool granted, bool __runOriginal)
	{
		if (!Plugin.On || Plugin.RequestedFor != __instance || Time.time - Plugin.RequestedAt > 3f || Player.m_localPlayer == null)
		{
			return true;
		}
		Plugin.RequestedFor = null;
		if (!__runOriginal)
		{
			return false; // eine andere Mod hat die Antwort übernommen (z. B. die Kiste gehört zu einer offenen verbundenen Ansicht)
		}
		if (!granted)
		{
			return true; // Vanilla meldet „wird benutzt"
		}
		Plugin.Waiting = __instance;
		Plugin.WaitingSince = Time.time;
		return false;
	}
}
