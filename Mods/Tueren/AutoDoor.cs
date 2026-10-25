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

	internal static readonly HashSet<Door> Doors = new();
	private static readonly HashSet<Door> openedByUs = new();
	private static HashSet<string> excluded = new();
	private static float nextTick;
	private static bool paused;
	private static bool dangerShown;

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
		Doors.RemoveWhere(door => door == null);
		openedByUs.RemoveWhere(door => door == null);

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
			int state = door.m_nview.GetZDO().GetInt(ZDOVars.s_state);
			float distance = Vector3.Distance(position, door.transform.position);
			if (state == 0 && !danger && distance < OpenDistance.Value && door.CanInteract() && PrivateArea.CheckAccess(door.transform.position, 0f, false))
			{
				Vector3 dir = (position - door.transform.position).normalized;
				door.Open(dir);
				openedByUs.Add(door);
				continue;
			}
			if (state != 0 && openedByUs.Contains(door) && door.CanInteract())
			{
				float nearest = players.Count == 0 ? distance : players.Min(p => Vector3.Distance(p.transform.position, door.transform.position));
				bool clear = danger && CloseWhenDanger.Value ? nearest > 1.2f : nearest > CloseDistance.Value;
				if (clear)
				{
					door.Open(Vector3.zero);
					openedByUs.Remove(door);
				}
			}
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
			if (BaseAI.IsEnemy(player, character))
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
