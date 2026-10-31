using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using UnityEngine;

namespace GC.Client.AutoDoor;

/// <summary>
/// GC_AutoDoor: Türen öffnen sich beim Herangehen und schließen sich hinter einem.
///
/// Sicherheitsregeln: keine Automatik, solange ein Gegner in der Nähe ist oder ein Raid (Zufallsereignis)
/// den Spieler betrifft. Türen mit Schlüssel (Krypten), Türen, die sich nicht schließen lassen, und Türen im
/// fremden Ward werden nie automatisch bedient. Bei Gefahr schließt die Mod die Türen, die sie selbst
/// geöffnet hat, sobald niemand mehr im Durchgang steht.
/// Läuft nur beim Spieler. Das Öffnen nutzt den Vanilla-Weg (RPC „UseDoor"), alle sehen denselben Zustand.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_autodoor";
	public const string ModName = "GC_AutoDoor";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<float> OpenDistance = null!;
	internal static ConfigEntry<float> CloseDistance = null!;
	internal static ConfigEntry<float> EnemyRange = null!;
	internal static ConfigEntry<bool> BlockDuringRaid = null!;
	internal static ConfigEntry<bool> CloseWhenDanger = null!;
	internal static ConfigEntry<string> ExcludedPrefabs = null!;
	internal static ConfigEntry<KeyboardShortcut> ToggleKey = null!;

	/// <summary>Abstand, ab dem jemand „im Durchgang“ steht: dann wird nicht geschlossen.</summary>
	private const float PassageDistance = 1.2f;

	internal static readonly HashSet<Door> Doors = new();
	private static readonly HashSet<Door> openedByUs = new();
	private static readonly Dictionary<Door, Request> requests = new();
	private static HashSet<string> excluded = new();
	private static float nextTick;
	private static float nextCleanup;
	private static bool paused;
	private static bool dangerShown;

	/// <summary>Gesendeter Umschalt-Wunsch (RPC „UseDoor“) an einer Tür, bis die Antwort ihres Besitzers da ist.</summary>
	private sealed class Request
	{
		internal bool Open;     // gewünschter Zustand
		internal uint Revision; // DataRevision des ZDO beim Senden
		internal float Until;   // spätestens dann neu entscheiden
		internal bool Pause;    // nach einem Fehlversuch: zufällige Pause vor dem nächsten
	}

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "7 - AutoDoor", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		OpenDistance = Gc.Bind(Config, "7 - AutoDoor", "OpenDistance", 2.5f, "Tür öffnet sich, wenn du näher als so viele Meter bist.", new AcceptableValueRange<float>(1f, 6f));
		CloseDistance = Gc.Bind(Config, "7 - AutoDoor", "CloseDistance", 4f, "Tür schließt sich, wenn alle Spieler weiter weg sind.", new AcceptableValueRange<float>(1.5f, 10f));
		EnemyRange = Gc.Bind(Config, "7 - AutoDoor - Safety", "EnemyRange", 20f, "Keine Automatik, solange ein lebender Gegner in diesem Umkreis ist.", new AcceptableValueRange<float>(0f, 60f));
		BlockDuringRaid = Gc.Bind(Config, "7 - AutoDoor - Safety", "BlockDuringRaid", true, "Keine Automatik während eines Raids in deiner Nähe.");
		CloseWhenDanger = Gc.Bind(Config, "7 - AutoDoor - Safety", "CloseWhenDanger", true, "Bei Gefahr die von der Mod geöffneten Türen schließen, sobald niemand im Durchgang steht.");
		ExcludedPrefabs = Gc.Bind(Config, "7 - AutoDoor - Filter", "ExcludedPrefabs", "", "Kommagetrennte Prefab-Namen von Türen, die nie automatisch bedient werden (z. B. Tore).");
		ToggleKey = Gc.Bind(Config, "7 - AutoDoor", "ToggleKey", new KeyboardShortcut(KeyCode.None), "Optionale Taste zum Pausieren der Automatik.");
		excluded = Gc.ParseList(ExcludedPrefabs.Value);
		ExcludedPrefabs.SettingChanged += (_, _) => excluded = Gc.ParseList(ExcludedPrefabs.Value);
	}

	internal static void Update()
	{
		if (Time.time >= nextCleanup)
		{
			// auch, wenn die Mod aus oder gesperrt ist: DoorAwakePatch trägt weiter ein, Doors wuchs sonst mit jeder geladenen Tür (Review 27.10.2026)
			nextCleanup = Time.time + 5f;
			Forget();
		}
		Player player = Player.m_localPlayer;
		if (!On || Gc.IsHeadless || player == null)
		{
			return;
		}
		if (Gc.Down(ToggleKey))
		{
			paused = !paused;
			Gc.Center(GcLoc.Get(paused ? "gcad_paused" : "gcad_resumed"));
		}
		if (paused || Time.time < nextTick)
		{
			return;
		}
		nextTick = Time.time + 0.15f;
		Forget();
		if (player.IsTeleporting())
		{
			return; // beim Teleport nichts öffnen, die eigenen hat TeleportPatch schon geschlossen (Review 27.10.2026)
		}

		bool danger = InDanger(player);
		if (danger && !dangerShown)
		{
			dangerShown = true;
		}
		else if (!danger)
		{
			dangerShown = false;
		}

		Vector3 position = player.transform.position;
		List<Player> players = Player.GetAllPlayers();

		foreach (Door door in Doors)
		{
			if (!Usable(door))
			{
				continue;
			}
			ZDO zdo = door.m_nview.GetZDO();
			if (Waiting(door, zdo))
			{
				continue;
			}
			int state = zdo.GetInt(ZDOVars.s_state);
			if (state == 0)
			{
				openedByUs.Remove(door); // zu (von uns, von Hand oder ein Öffnen schlug fehl): nicht mehr unsere offene Tür
			}
			float distance = Vector3.Distance(position, door.transform.position);
			if (state == 0 && !danger && distance < OpenDistance.Value && door.CanInteract() && PrivateArea.CheckAccess(door.transform.position, 0f, false))
			{
				Vector3 dir = (position - door.transform.position).normalized;
				Send(door, zdo, true, dir);
				openedByUs.Add(door);
				continue;
			}
			if (state != 0 && openedByUs.Contains(door) && door.CanInteract())
			{
				float nearest = players.Count == 0 ? distance : players.Min(p => Vector3.Distance(p.transform.position, door.transform.position));
				bool clear = danger && CloseWhenDanger.Value ? nearest > PassageDistance : nearest > CloseDistance.Value;
				if (clear)
				{
					Send(door, zdo, false, Vector3.zero); // bleibt in openedByUs, bis sie wirklich zu ist (sonst nächster Versuch)
				}
			}
		}
	}

	/// <summary>Zerstörte Türen vergessen (Zone entladen, abgerissen).</summary>
	private static void Forget()
	{
		Doors.RemoveWhere(door => door == null);
		openedByUs.RemoveWhere(door => door == null);
		if (requests.Count > 0)
		{
			foreach (Door door in requests.Keys.Where(door => door == null).ToList())
			{
				requests.Remove(door);
			}
		}
	}

	/// <summary>Umschalt-Wunsch wie Vanilla (Door.Open: RPC „UseDoor“ an den Besitzer) und merken, bis seine Antwort da ist.</summary>
	private static void Send(Door door, ZDO zdo, bool open, Vector3 direction)
	{
		requests[door] = new Request { Open = open, Revision = zdo.DataRevision, Until = Time.time + 1f };
		door.Open(direction);
	}

	/// <summary>
	/// Wartet die Tür noch auf die Antwort zu unserem letzten Wunsch? „UseDoor“ schaltet beim Besitzer um; ein zweiter Wunsch, bevor
	/// dessen Antwort da ist, schaltete sie zurück (Review 27.10.2026: Tür flatterte bei fremdem Besitzer oder zwei Spielern mit der
	/// Mod, weil die eigene ZDO-Kopie noch „zu“ zeigte und alle 0,15 s neu gesendet wurde). Antwort heißt: die DataRevision des ZDO
	/// ändert sich, höchstens 1 s. Ist die Tür danach nicht wie gewünscht (zwei Spieler schalteten zugleich), folgt eine zufällige
	/// Pause, damit nicht beide wieder im Gleichtakt senden.
	/// </summary>
	private static bool Waiting(Door door, ZDO zdo)
	{
		if (!requests.TryGetValue(door, out Request request))
		{
			return false;
		}
		if (zdo.DataRevision == request.Revision && Time.time < request.Until)
		{
			return true;
		}
		bool open = zdo.GetInt(ZDOVars.s_state) != 0;
		if (!request.Pause && open != request.Open)
		{
			request.Pause = true;
			request.Revision = zdo.DataRevision;
			request.Until = Time.time + Random.Range(0.3f, 1f);
			return true;
		}
		requests.Remove(door);
		return false;
	}

	/// <summary>
	/// Vor Teleport, Tod und Abmelden (Review 27.10.2026): die selbst geöffneten Türen schließen. Danach verschwinden sie lokal (Zone
	/// entladen), die Mod vergaß sie, und sie blieben offen. Steht ein anderer Spieler im Durchgang, bleibt die Tür für ihn offen.
	/// Gilt auch bei Pause oder abgeschalteter Mod: Es sind Türen, die sie selbst geöffnet hat.
	/// </summary>
	internal static void CloseOwnDoors()
	{
		if (openedByUs.Count == 0)
		{
			return;
		}
		Player? me = Player.m_localPlayer;
		List<Player> players = Player.GetAllPlayers();
		foreach (Door door in openedByUs)
		{
			if (door == null || door.m_nview == null || !door.m_nview.IsValid())
			{
				continue;
			}
			ZDO zdo = door.m_nview.GetZDO();
			if (zdo.GetInt(ZDOVars.s_state) == 0 || (requests.TryGetValue(door, out Request pending) && !pending.Open && zdo.DataRevision == pending.Revision))
			{
				continue; // schon zu, oder das Schließen ist schon unterwegs
			}
			if (players.Any(p => p != null && p != me && Vector3.Distance(p.transform.position, door.transform.position) <= PassageDistance))
			{
				continue;
			}
			Send(door, zdo, false, Vector3.zero);
		}
	}

	private static bool Usable(Door door)
	{
		if (door.m_nview == null || !door.m_nview.IsValid() || door.m_keyItem != null || door.m_canNotBeClosed)
		{
			return false;
		}
		return excluded.Count == 0 || !excluded.Contains(Gc.PrefabName(door.gameObject));
	}

	private static bool InDanger(Player player)
	{
		Vector3 position = player.transform.position;
		if (BlockDuringRaid.Value && RandEventSystem.instance != null)
		{
			RandomEvent? active = RandEventSystem.instance.GetActiveEvent();
			if (active != null && Utils.DistanceXZ(position, active.m_pos) < active.m_eventRange)
			{
				return true;
			}
		}
		float range = EnemyRange.Value;
		if (range <= 0f)
		{
			return false;
		}
		foreach (Character character in Character.GetAllCharacters())
		{
			if (character == null || character.IsDead() || character.IsPlayer() || character.IsTamed())
			{
				continue;
			}
			if (Vector3.Distance(position, character.transform.position) > range)
			{
				continue;
			}
			// Review 27.10.2026: Nur feindliche Wesen (BaseAI.IsEnemy beachtet die Fraktionen, Dverger z. B. erst aufgebracht), die
			// alarmiert oder aufgebracht sind. Vorher zählte jedes ungezähmte Wesen (Rehe, Hasen, Tiere im Zähmgehege) und die
			// Automatik blieb dauerhaft aus. Tiere mit AnimalAI fliehen nur und greifen nie an.
			BaseAI? ai = character.GetBaseAI();
			if (ai == null || ai is AnimalAI || !BaseAI.IsEnemy(player, character))
			{
				continue;
			}
			if (ai.IsAlerted() || ai.IsAggravated())
			{
				return true;
			}
		}
		return false;
	}
}

[HarmonyPatch(typeof(Door), nameof(Door.Awake))]
internal static class DoorAwakePatch
{
	private static void Postfix(Door __instance)
	{
		if (__instance.m_nview != null && __instance.m_nview.GetZDO() != null)
		{
			Plugin.Doors.Add(__instance);
		}
	}
}

/// <summary>Teleport (Portal, Bett, Mods): vorher die selbst geöffneten Türen schließen (Review 27.10.2026).</summary>
[HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
internal static class TeleportPatch
{
	private static void Postfix(Player __instance, bool __result)
	{
		if (__result && __instance == Player.m_localPlayer)
		{
			Plugin.CloseOwnDoors();
		}
	}
}

/// <summary>Tod: vorher die selbst geöffneten Türen schließen, nach dem Erwachen am Bett wären sie vergessen (Review 27.10.2026).</summary>
[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
internal static class DeathPatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer)
		{
			Plugin.CloseOwnDoors();
		}
	}
}

/// <summary>Abmelden und Spiel beenden (Game.Logout und OnApplicationQuit laufen über Game.Shutdown): vorher schließen (Review 27.10.2026).</summary>
[HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
internal static class ShutdownPatch
{
	private static void Prefix(Game __instance)
	{
		if (!__instance.m_shuttingDown)
		{
			Plugin.CloseOwnDoors();
		}
	}
}
