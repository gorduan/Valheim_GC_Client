using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using UnityEngine;

namespace GC.Client.MassHarvest;

/// <summary>
/// GC_MassHarvest: E gedrückt halten sammelt alles Aufsammelbare um den Cursor ein.
///
/// Vanilla kann schon „E halten und mit dem Cursor drüberfahren": Player.Update ruft bei gehaltener Taste
/// jedes Bild Interact(hold: true) auf, Player.Interact wirkt aber höchstens alle 0,2 s (m_lastHoverInteractTime), und
/// Pickable.Interact ignoriert das hold-Flag. Diese Mod erweitert das: jede dieser Aktionen sammelt zusätzlich alles im
/// Umkreis des Ziels ein.
/// Ursprünglich nur Feldfrüchte (Pickables, die eine Pflanze hervorbringt: Plant.m_grownPrefabs). Gorduan (18.10.2026): „für
/// alles, was man im Spiel aufsammeln kann“, also auch Büsche, wilde Pflanzen, Pilze, Steine, Feuerstein, Äste, Schätze
/// (alle Pickables), Lager-Beute (PickableItem) und liegende Gegenstände (ItemDrop; Valheim selbst nimmt die bei
/// gehaltenem E nicht, ItemDrop.Interact bricht bei repeat ab). Abschaltbar: dann wieder nur Feldfrüchte.
/// Immer nur dieselbe Sorte wie das Anvisierte (Gorduan, 18.10.2026: „E auf Gold sammelt nur Gold“): Pflanzen, Steine usw. nach
/// ihrem Objekt, Lager-Beute und liegende Gegenstände nach dem Item. Die frühere Einstellung „Nur dieselbe Sorte“ entfällt.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_massharvest";
	public const string ModName = "GC_MassHarvest";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<float> Radius = null!;
	internal static ConfigEntry<int> MaxPerTick = null!;
	internal static ConfigEntry<bool> RequireHold = null!;
	internal static ConfigEntry<bool> Everything = null!;

	internal static readonly HashSet<string> Crops = new();

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "6 - MassHarvest", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		Radius = Gc.Bind(Config, "6 - MassHarvest", "Radius", 2.5f, "Ernte-Radius um die anvisierte Pflanze in Metern.", new AcceptableValueRange<float>(0.5f, 8f));
		MaxPerTick = Gc.Bind(Config, "6 - MassHarvest", "MaxPerTick", 12, "Höchstens so viele Pflanzen pro Ernte-Schritt (alle 0,2 s).", new AcceptableValueRange<int>(1, 50));
		RequireHold = Gc.Bind(Config, "6 - MassHarvest", "RequireHold", true, "Nur beim Gedrückthalten ernten (ein einzelner Druck erntet wie Vanilla nur eine Pflanze).");
		Everything = Gc.Bind(Config, "6 - MassHarvest", "Everything", true, "Alles Aufsammelbare im Umkreis, nicht nur Feldfrüchte: Büsche, wilde Pflanzen, Pilze, Steine, Feuerstein, Äste, Schätze und liegende Gegenstände.");
	}
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class ZNetSceneAwakePatch
{
	[HarmonyPriority(Priority.Last)] // nach anderen Mods: PlantEverything legt seine Setzlinge im eigenen Postfix an
	private static void Postfix(ZNetScene __instance)
	{
		Plugin.Crops.Clear();
		foreach (GameObject prefab in __instance.m_prefabs)
		{
			Plant? plant = prefab != null ? prefab.GetComponent<Plant>() : null;
			if (plant == null)
			{
				continue;
			}
			foreach (GameObject grown in plant.m_grownPrefabs)
			{
				if (grown != null && grown.GetComponent<Pickable>() != null)
				{
					Plugin.Crops.Add(grown.name);
				}
			}
		}
		Plugin.Log.LogInfo($"{Plugin.Crops.Count} Feldfrucht-Arten für die Massenernte erkannt.");
	}
}

[HarmonyPatch(typeof(Player), nameof(Player.Interact))]
internal static class InteractPatch
{
	private static readonly Collider[] Hits = new Collider[128];

	/// <summary>
	/// Angefragte Objekte (Instanz-ID) mit Zeitpunkt. Gehört ein Objekt einem anderen Spieler, kommt das Gepflückt-Sein erst mit
	/// dessen Antwort zurück; bis dahin (höchstens RequestMemory) nicht erneut anfragen (Review 27.10.2026).
	/// </summary>
	private static readonly Dictionary<int, float> Requested = new();
	private static readonly List<int> Expired = new();
	private const float RequestMemory = 1f;
	private static int lastFrame = -1;

	private static void Postfix(Player __instance, GameObject go, bool hold)
	{
		if (!Plugin.On || __instance != Player.m_localPlayer || go == null || (Plugin.RequireHold.Value && !hold))
		{
			return;
		}
		// Review 27.10.2026: nur, wenn Valheim eben selbst interagiert hat. Bei gehaltenem E kommt Interact jedes Bild, Valheim bricht
		// innerhalb von 0,2 s aber ab (m_lastHoverInteractTime), der Postfix lief trotzdem: bis zu MaxPerTick Anfragen je Bild.
		if (__instance.m_lastHoverInteractTime != Time.time || lastFrame == Time.frameCount)
		{
			return;
		}
		lastFrame = Time.frameCount;
		Component? target = Collectible(go, out string kind);
		if (target == null)
		{
			return;
		}
		Forget();
		Requested[target.GetInstanceID()] = Time.time; // das Ziel selbst nimmt Valheim
		int harvested = 0;
		// dieselben Ebenen wie Valheims Zeigen auf Objekte (Player.m_interactMask), ohne Auslöser-Bereiche (SDK: Overlap-Abfragen)
		int count = Physics.OverlapSphereNonAlloc(target.transform.position, Plugin.Radius.Value, Hits, __instance.m_interactMask, QueryTriggerInteraction.Ignore);
		for (int i = 0; i < count && harvested < Plugin.MaxPerTick.Value; i++)
		{
			if (Hits[i] == null)
			{
				continue;
			}
			Component? other = Collectible(Hits[i].gameObject, out string otherKind);
			if (other == null || otherKind != kind || Requested.ContainsKey(other.GetInstanceID()) || !Ready(other))
			{
				continue;
			}
			Requested[other.GetInstanceID()] = Time.time;
			Collect(__instance, other);
			harvested++;
		}
	}

	/// <summary>Anfragen, die älter als RequestMemory sind, vergessen: Kam keine Antwort, darf neu gefragt werden.</summary>
	private static void Forget()
	{
		foreach (KeyValuePair<int, float> entry in Requested)
		{
			if (Time.time - entry.Value >= RequestMemory)
			{
				Expired.Add(entry.Key);
			}
		}
		foreach (int id in Expired)
		{
			Requested.Remove(id);
		}
		Expired.Clear();
	}

	/// <summary>
	/// Was an diesem Objekt aufzusammeln ist (Pickable, PickableItem, ItemDrop) und seine Sorte; null, wenn nichts. Die Sorte
	/// trägt die Art vorn, damit ein Busch nicht zu liegenden Beeren passt.
	/// </summary>
	private static Component? Collectible(GameObject go, out string kind)
	{
		kind = "";
		Pickable? pickable = go.GetComponentInParent<Pickable>();
		if (pickable != null)
		{
			string name = Utils.GetPrefabName(pickable.gameObject);
			kind = "P:" + name;
			return Plugin.Everything.Value || Plugin.Crops.Contains(name) ? pickable : null;
		}
		if (!Plugin.Everything.Value)
		{
			return null;
		}
		PickableItem? loot = go.GetComponentInParent<PickableItem>();
		if (loot != null)
		{
			kind = "L:" + (loot.m_itemPrefab != null ? loot.m_itemPrefab.name : Utils.GetPrefabName(loot.gameObject)); // zufällige Beute: das gewürfelte Item
			return loot;
		}
		ItemDrop? item = go.GetComponentInParent<ItemDrop>();
		if (item != null && item.m_itemData != null)
		{
			kind = "I:" + Utils.GetPrefabName(item.gameObject);
			return item;
		}
		return null;
	}

	/// <summary>Jetzt aufsammelbar? Gepflückte Pflanzen, als Bauteil platzierte und im Teer klebende Gegenstände nicht.</summary>
	private static bool Ready(Component other)
	{
		ZNetView? view = other.GetComponent<ZNetView>();
		if (view == null || !view.IsValid())
		{
			return false;
		}
		return other switch
		{
			Pickable pickable => !pickable.GetPicked() && pickable.CanBePicked(),
			PickableItem loot => !loot.m_picked,
			ItemDrop item => !item.IsPiece() && !item.InTar(),
			_ => false,
		};
	}

	private static void Collect(Player player, Component other)
	{
		switch (other)
		{
			case Pickable pickable:
				pickable.Interact(player, false, false);
				break;
			case PickableItem loot:
				loot.Interact(player, false, false);
				break;
			case ItemDrop item:
				item.Pickup(player); // wie ein Druck auf E (Valheim prüft Platz, GC_Client das Traglimit)
				break;
		}
	}
}
