using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using UnityEngine;

namespace GC.Client.PortalGuard;

/// <summary>
/// GC_PortalGuard: Sicherheitsnetz für Portalnamen.
///
/// Vanilla (TeleportWorld.RPC_SetTag) trennt die Portalverbindung schon dann, wenn jemand den Umbenennen-Dialog
/// mit dem unveränderten Namen bestätigt, weil sich dabei der gespeicherte Autor ändert. Außerdem reicht ein
/// versehentliches E + Enter, um den Namen zu überschreiben oder zu leeren. Diese Mod:
///  * ignoriert Bestätigungen ohne Namensänderung (die Verbindung bleibt bestehen),
///  * fragt vor jeder echten Umbenennung nach („von X in Y umbenennen?"),
///  * erlaubt leere Namen wie Vanilla (in Valheim verbinden sich auch Portale ohne Namen),
///  * merkt sich den vorherigen Namen im Portal und stellt ihn mit Umschalt+E wieder her (wie Valheims
///    „alternative Benutzung"; E allein öffnet weiter den Umbenennen-Dialog),
///  * schreibt jede Umbenennung ins Log des Besitzers (Server-Log, wenn niemand in der Nähe ist).
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_portalguard";
	public const string ModName = "GC_PortalGuard";
	public const string Version = "1.0.0";

	internal const string PrevKey = "gc_portal_prev";
	internal const string PrevTimeKey = "gc_portal_prev_time";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<bool> Confirm = null!;
	internal static ConfigEntry<KeyCode> RestoreModifier = null!;
	internal static bool Bypass;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "8 - PortalGuard", "Enabled", true, "Schutz beim Umbenennen von Portalen (auch im Menü GC Option).");
		Confirm = Gc.Bind(Config, "8 - PortalGuard", "ConfirmRename", true, "Vor dem Umbenennen eines benannten Portals nachfragen.");
		RestoreModifier = Gc.Bind(Config, "8 - PortalGuard", "RestoreModifier", KeyCode.LeftShift, "Diese Taste halten und ein Portal benutzen (E): vorherigen Namen wiederherstellen statt umbenennen.");
	}

	internal static void Update()
	{
	}

	internal static bool RestoreHeld => On && RestoreModifier.Value != KeyCode.None && Input.GetKey(RestoreModifier.Value) && !Gc.TextInputActive();

	internal static void AskRestore(TeleportWorld portal)
	{
		string previous = portal.m_nview.GetZDO().GetString(PrevKey);
		string current = portal.GetText();
		UnifiedPopup.Push(new YesNoPopup(GcLoc.Get("gcpg_restore_title"), GcLoc.Get("gcpg_restore_text", current, previous),
			() =>
			{
				UnifiedPopup.Pop();
				Rename(portal, previous);
			},
			UnifiedPopup.Pop, false));
	}

	internal static void Rename(TeleportWorld portal, string text)
	{
		if (portal == null)
		{
			return;
		}
		Bypass = true;
		try
		{
			portal.SetText(text);
		}
		finally
		{
			Bypass = false;
		}
	}
}

/// <summary>
/// Umschalt+E: statt des Umbenennen-Dialogs die Rückfrage zum Wiederherstellen, wenn kein anderer Prefix das Benutzen
/// verboten hat (z. B. Guilds_Vehicle bei einem beanspruchten Portal).
/// </summary>
[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
internal static class RestoreInteractPatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(TeleportWorld __instance, Humanoid human, bool hold, bool __runOriginal, ref bool __result)
	{
		if (hold || human != Player.m_localPlayer || !Plugin.RestoreHeld)
		{
			return true;
		}
		if (!__runOriginal)
		{
			return false; // Meldung kam schon von der Mod, die das Benutzen verbietet
		}
		if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !PrivateArea.CheckAccess(__instance.transform.position, 0f, false))
		{
			return true; // fremder Ward: Vanilla prüft selbst noch einmal und lässt den Ward aufleuchten
		}
		if (string.IsNullOrEmpty(__instance.m_nview.GetZDO().GetString(Plugin.PrevKey)))
		{
			Gc.Center(GcLoc.Get("gcpg_no_previous"));
			__result = true;
			return false;
		}
		Plugin.AskRestore(__instance);
		__result = true;
		return false;
	}
}

/// <summary>Client: Umbenennen prüfen, bevor es an den Besitzer geht.</summary>
[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.SetText))]
internal static class SetTextPatch
{
	private static bool Prefix(TeleportWorld __instance, string text)
	{
		if (!Plugin.On || Plugin.Bypass || __instance.m_nview == null || !__instance.m_nview.IsValid())
		{
			return true;
		}
		string current = __instance.GetText() ?? "";
		string wanted = text ?? "";
		if (wanted == current)
		{
			return false; // nichts geändert: keine Neuverbindung auslösen
		}
		if (!Plugin.Confirm.Value || current.Length == 0)
		{
			return true;
		}
		TeleportWorld portal = __instance;
		UnifiedPopup.Push(new YesNoPopup(GcLoc.Get("gcpg_confirm_title"), GcLoc.Get("gcpg_confirm_text", current, wanted),
			() =>
			{
				UnifiedPopup.Pop();
				Plugin.Rename(portal, wanted);
			},
			UnifiedPopup.Pop, false));
		return false;
	}
}

/// <summary>Besitzer: gleiche Namen nicht neu verbinden, vorherigen Namen merken, protokollieren.</summary>
[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.RPC_SetTag))]
internal static class SetTagPatch
{
	private static bool Prefix(TeleportWorld __instance, string tag, string authorId)
	{
		if (!Plugin.On || __instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
		{
			return true;
		}
		__instance.GetTagSignature(out string tagRaw, out string oldAuthor);
		if (tagRaw == tag)
		{
			return false; // Vanilla würde hier nur wegen des Autors die Verbindung trennen
		}
		ZDO zdo = __instance.m_nview.GetZDO();
		if (!string.IsNullOrEmpty(tagRaw))
		{
			zdo.Set(Plugin.PrevKey, tagRaw);
			zdo.Set(Plugin.PrevTimeKey, DateTime.UtcNow.Ticks);
		}
		Plugin.Log.LogInfo($"Portal bei {__instance.transform.position:F0} umbenannt: '{tagRaw}' -> '{tag}' (von {authorId}, vorher {oldAuthor}).");
		return true;
	}
}

[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText))]
internal static class HoverPatch
{
	private static void Postfix(TeleportWorld __instance, ref string __result)
	{
		if (!Plugin.On || __instance.m_nview == null || !__instance.m_nview.IsValid())
		{
			return;
		}
		string previous = __instance.m_nview.GetZDO().GetString(Plugin.PrevKey);
		if (!string.IsNullOrEmpty(previous))
		{
			// Mit Guilds_Vehicle öffnet Umschalt+E am Portal dessen Gilden-Menü: dann über die Liste beim Umbenennen
			bool guilds = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(GcKeys.GuildsVehicleGuid);
			if (!guilds && Plugin.RestoreModifier.Value == KeyCode.None)
			{
				return; // ohne Taste gibt es den Weg am Portal nicht, nur die Liste beim Umbenennen
			}
			// angezeigt wird genau die eingestellte Taste (RestoreHeld prüft sie direkt, nicht Valheims Belegung)
			string key = Plugin.RestoreModifier.Value.ToString();
			__result += "\n" + (guilds
				? GcLoc.Get("gcpg_hover_previous_list", previous.RemoveRichTextTags())
				: GcLoc.Get("gcpg_hover_previous", previous.RemoveRichTextTags(), key + " + " + Localization.instance.Localize("$KEY_Use")));
		}
	}
}
