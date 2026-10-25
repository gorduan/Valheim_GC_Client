using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using UnityEngine;

namespace GC.Client.CartCollect;

/// <summary>
/// GC_CartCollect: Aufgesammeltes Material wandert direkt in einen nahen Karren oder ein nahes Schiff.
///
/// Greift beim Aufheben (E und automatisches Aufheben, beides läuft über Humanoid.Pickup). Ziel ist das nächste
/// Fahrzeug im Umkreis, dessen Ladefläche das Item bereits enthält und noch Platz hat. Nur Fahrzeuge, deren
/// Daten dieser Spieler gerade besitzt (z. B. den gezogenen Karren oder das gesteuerte Schiff), damit nichts
/// doppelt gespeichert wird. Fixierte Kisten (GC_ItemLock) und fremde Fahrzeuge (Guilds_Vehicle) sind tabu.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_cartcollect";
	public const string ModName = "GC_CartCollect";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<float> Range = null!;
	internal static ConfigEntry<bool> OnlyMatching = null!;
	internal static ConfigEntry<bool> IncludeShips = null!;
	internal static ConfigEntry<bool> OnlyWhilePulling = null!;
	internal static ConfigEntry<KeyboardShortcut> ToggleKey = null!;
	internal static bool paused;
	internal static MethodInfo? canUseCargo;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "5 - CartCollect", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		Range = Gc.Bind(Config, "5 - CartCollect", "Range", 10f, "Suchradius in Metern um den Spieler.", new AcceptableValueRange<float>(2f, 30f));
		OnlyMatching = Gc.Bind(Config, "5 - CartCollect", "OnlyMatchingItems", true, "Nur Items, die im Fahrzeug schon liegen (Holz zu Holz, Stein zu Stein). Aus = alles, was Platz hat.");
		IncludeShips = Gc.Bind(Config, "5 - CartCollect", "IncludeShips", true, "Auch Schiffe mit Laderaum befüllen.");
		OnlyWhilePulling = Gc.Bind(Config, "5 - CartCollect", "OnlyWhilePulling", false, "Karren nur befüllen, während du ihn ziehst.");
		ToggleKey = Gc.Bind(Config, "5 - CartCollect", "PauseKey", new KeyboardShortcut(KeyCode.None), "Optionale Taste, um die Funktion kurz an- und auszuschalten.");
		if (Chainloader.PluginInfos.ContainsKey(GcKeys.GuildsVehicleGuid))
		{
			Type? api = AccessTools.TypeByName("GuildVehicle.VehicleAPI");
			canUseCargo = api == null ? null : AccessTools.Method(api, "CanUseCargo", new[] { typeof(ZNetView), typeof(Player) });
		}
	}

	internal static void Update()
	{
		if (On && Player.m_localPlayer != null && Gc.Down(ToggleKey))
		{
			paused = !paused;
			Gc.Center(GcLoc.Get(paused ? "gcc_paused" : "gcc_resumed"));
		}
	}

	private static readonly Collider[] Hits = new Collider[64];

	internal static Container? FindTarget(Player player, ItemDrop.ItemData item)
	{
		int mask = LayerMask.GetMask("vehicle", "piece", "item");
		int count = Physics.OverlapSphereNonAlloc(player.transform.position, Range.Value, Hits, mask);
		Container? best = null;
		float bestDistance = float.MaxValue;
		for (int i = 0; i < count; i++)
		{
			Container? container = Hits[i].GetComponentInParent<Container>();
			if (container == null || container == best)
			{
				continue;
			}
			Vagon? cart = container.GetComponentInParent<Vagon>();
			Ship? ship = container.GetComponentInParent<Ship>();
			if (cart == null && (ship == null || !IncludeShips.Value))
			{
				continue;
			}
			if (cart != null && OnlyWhilePulling.Value && !cart.IsAttached(player))
			{
				continue;
			}
			ZNetView nview = container.m_nview;
			if (nview == null || !nview.IsValid() || !nview.IsOwner() || container.IsInUse() || Gc.IsAutoLocked(container))
			{
				continue;
			}
			ZNetView? vehicle = cart != null ? cart.GetComponent<ZNetView>() : ship!.GetComponent<ZNetView>();
			if (canUseCargo != null && vehicle != null && !(bool)canUseCargo.Invoke(null, new object[] { vehicle, player }))
			{
				continue;
			}
			Inventory inventory = container.GetInventory();
			if (inventory == null || !inventory.CanAddItem(item))
			{
				continue;
			}
			if (OnlyMatching.Value && !inventory.ContainsItemByName(item.m_shared.m_name))
			{
				continue;
			}
			float distance = Vector3.Distance(player.transform.position, container.transform.position);
			if (distance < bestDistance)
			{
				best = container;
				bestDistance = distance;
			}
		}
		return best;
	}
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
internal static class PickupPatch
{
	[HarmonyPriority(Priority.High)]
	private static bool Prefix(Humanoid __instance, GameObject go, bool autoPickupDelay, ref bool __result)
	{
		if (!Plugin.On || Plugin.paused || __instance != Player.m_localPlayer || go == null)
		{
			return true;
		}
		Player player = (Player)__instance;
		ItemDrop? drop = go.GetComponent<ItemDrop>();
		if (drop == null || player.IsTeleporting() || !drop.CanPickup(autoPickupDelay))
		{
			return true;
		}
		drop.Load();
		ItemDrop.ItemData item = drop.m_itemData;
		if (item.m_shared.m_questItem || item.m_shared.m_maxStackSize <= 1 || item.m_customData.Count > 0)
		{
			return true; // Ausrüstung, Questitems und Items mit Zusatzdaten immer ins eigene Inventar
		}
		Container? target = Plugin.FindTarget(player, item);
		if (target == null)
		{
			return true;
		}
		int stack = item.m_stack;
		string name = item.m_shared.m_name;
		if (!target.GetInventory().AddItem(item.Clone()))
		{
			return true;
		}
		ZNetScene.instance.Destroy(go);
		player.m_pickupEffects.Create(player.transform.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());
		Gc.TopLeft(GcLoc.Get("gcc_stored", stack, Localization.instance.Localize(name), Localization.instance.Localize(target.m_name)));
		__result = true;
		return false;
	}
}
