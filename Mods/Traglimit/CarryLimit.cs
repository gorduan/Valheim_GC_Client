using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using UnityEngine;

namespace GC.Client.CarryLimit;

public enum Activation
{
	Hold,
	Toggle,
	Always
}

/// <summary>
/// GC_CarryLimit: Nur so viel von einem Stapel nehmen, wie man noch tragen kann.
///
/// Vanilla hebt beim Drücken von E immer den ganzen Stapel auf (Humanoid.Pickup ruft Inventory.AddItem ohne
/// Gewichtsprüfung) und ist danach überladen. Das automatische Aufheben überspringt zu schwere Stapel ganz
/// (Player.AutoPickup prüft GetTotalWeight + Stapelgewicht &gt; GetMaxCarryWeight).
/// Mit dieser Mod wird der Stapel geteilt: der tragbare Teil landet im Inventar, der Rest bleibt liegen.
///  * Boden: Umschalt+E (Modus Halten, Taste einstellbar), oder immer bzw. per Taste umgeschaltet.
///  * Kiste: Strg+Linksklick verschiebt in Vanilla den ganzen Stapel ins Inventar. Mit dieser Mod nur so viel,
///    wie du tragen kannst, der Rest bleibt in der Kiste.
///  * Automatisches Aufheben nimmt Teilstapel nur in den Modi Umschalten und Immer (im Modus Halten würde sonst
///    jedes Rennen mit Umschalt Teilstapel aufheben).
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_carrylimit";
	public const string ModName = "GC_CarryLimit";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<Activation> Mode = null!;
	internal static ConfigEntry<KeyboardShortcut> Key = null!;
	internal static ConfigEntry<bool> AutoPickupPartial = null!;
	internal static ConfigEntry<bool> ChestCtrlClick = null!;
	private static bool toggledOn;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "4 - CarryLimit", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		Mode = Gc.Bind(Config, "4 - CarryLimit", "Activation", Activation.Hold, "Hold = nur solange die Taste gehalten wird, Toggle = Taste schaltet an/aus, Always = immer aktiv.");
		Key = Gc.Bind(Config, "4 - CarryLimit", "Key", new KeyboardShortcut(KeyCode.LeftShift), "Taste für Hold bzw. Toggle (Standard Umschalt: Umschalt+E hebt nur den tragbaren Teil auf).");
		AutoPickupPartial = Gc.Bind(Config, "4 - CarryLimit", "AutoPickupPartial", true, "In den Modi Toggle und Always: auch das automatische Aufheben nimmt Teilstapel, statt zu schwere Stapel liegen zu lassen.");
		ChestCtrlClick = Gc.Bind(Config, "4 - CarryLimit", "ChestCtrlClick", true, "Strg+Linksklick in einer Kiste nimmt nur so viel vom Stapel, wie du tragen kannst.");
	}

	internal static void Update()
	{
		if (Mode.Value == Activation.Toggle && On && Player.m_localPlayer != null && Gc.Down(Key))
		{
			toggledOn = !toggledOn;
			Gc.Center(GcLoc.Get(toggledOn ? "gccl_on" : "gccl_off"));
		}
	}

	internal static bool Active
	{
		get
		{
			if (!On)
			{
				return false;
			}
			return Mode.Value switch
			{
				Activation.Always => true,
				Activation.Toggle => toggledOn,
				_ => !Gc.TextInputActive() && Key.Value.MainKey != KeyCode.None && Key.Value.IsPressed(),
			};
		}
	}

	/// <summary>Wie viele Stück eines Items passen noch unter das Traglimit (ohne überladen zu sein).</summary>
	internal static int Affordable(Player player, ItemDrop.ItemData item)
	{
		float unit = item.m_shared.m_weight;
		if (unit <= 0.0001f)
		{
			return item.m_stack;
		}
		float free = player.GetMaxCarryWeight() - player.GetInventory().GetTotalWeight();
		return Mathf.Clamp(Mathf.FloorToInt(free / unit + 0.0001f), 0, item.m_stack);
	}

	/// <summary>Wie viele Stück passen platzmäßig noch ins Inventar (gleiche Rechnung wie Inventory.CanAddItem).</summary>
	internal static int Room(Inventory inventory, ItemDrop.ItemData item)
	{
		int free = inventory.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel);
		return free + inventory.GetEmptySlots() * Mathf.Max(1, item.m_shared.m_maxStackSize);
	}

	/// <summary>
	/// Nimmt n Stück vom Boden-Item ins Inventar, der Rest bleibt liegen. Gezählt wird, was wirklich im Inventar
	/// angekommen ist, und genau das wird vom Bodenstapel abgezogen. So kann nichts doppelt entstehen, auch wenn
	/// Inventory.AddItem nur einen Teil unterbringt.
	/// </summary>
	internal static bool TakePartial(Player player, ItemDrop drop, int amount)
	{
		Inventory inventory = player.GetInventory();
		amount = Mathf.Min(amount, Room(inventory, drop.m_itemData), drop.m_itemData.m_stack);
		if (amount <= 0)
		{
			Gc.Center("$msg_noroom");
			return false;
		}
		ItemDrop.ItemData clone = drop.m_itemData.Clone();
		clone.m_stack = amount;
		int before = inventory.NrOfItemsIncludingStacks();
		inventory.AddItem(clone);
		int added = inventory.NrOfItemsIncludingStacks() - before;
		if (added <= 0)
		{
			return false;
		}
		int left = drop.m_itemData.m_stack - added;
		if (left <= 0)
		{
			ZNetScene.instance.Destroy(drop.gameObject);
		}
		else
		{
			drop.SetStack(left);
		}
		player.m_pickupEffects.Create(player.transform.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());
		player.ShowPickupMessage(clone, added);
		if (left > 0)
		{
			Gc.TopLeft(GcLoc.Get("gccl_partial", left, Localization.instance.Localize(drop.m_itemData.m_shared.m_name)));
		}
		return true;
	}
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
internal static class PickupPatch
{
	private static bool Prefix(Humanoid __instance, GameObject go, bool autoPickupDelay, ref bool __result, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false; // eine andere Mod (z. B. GC_CartCollect) hat das Aufheben schon erledigt
		}
		if (__instance != Player.m_localPlayer || !Plugin.Active || go == null)
		{
			return true;
		}
		Player player = (Player)__instance;
		ItemDrop? drop = go.GetComponent<ItemDrop>();
		if (drop == null || !drop.CanPickup(autoPickupDelay) || player.IsTeleporting())
		{
			return true;
		}
		drop.Load();
		ItemDrop.ItemData item = drop.m_itemData;
		if (item.m_shared.m_questItem || item.m_stack <= 1 && Plugin.Affordable(player, item) >= 1)
		{
			return true;
		}
		int affordable = Plugin.Affordable(player, item);
		if (affordable >= item.m_stack)
		{
			return true;
		}
		if (affordable <= 0)
		{
			Gc.Center(GcLoc.Get("gccl_too_heavy"));
			__result = false;
			return false;
		}
		__result = Plugin.TakePartial(player, drop, affordable);
		return false;
	}
}

/// <summary>Automatisches Aufheben: zu schwere Stapel teilweise aufnehmen, statt sie zu ignorieren.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.AutoPickup))]
internal static class AutoPickupPatch
{
	private static readonly Collider[] Hits = new Collider[64];
	private static float nextCheck;

	private static void Postfix(Player __instance)
	{
		if (__instance != Player.m_localPlayer || !Plugin.AutoPickupPartial.Value || Plugin.Mode.Value == Activation.Hold || !Plugin.Active || !Player.m_enableAutoPickup || __instance.IsTeleporting())
		{
			return;
		}
		if (Time.time < nextCheck)
		{
			return;
		}
		nextCheck = Time.time + 0.25f;
		Vector3 center = __instance.transform.position + Vector3.up;
		int count = Physics.OverlapSphereNonAlloc(center, __instance.m_autoPickupRange, Hits, __instance.m_autoPickupMask);
		for (int i = 0; i < count; i++)
		{
			Rigidbody? body = Hits[i].attachedRigidbody;
			ItemDrop? drop = body != null ? body.GetComponent<ItemDrop>() : null;
			if (drop == null || !drop.m_autoPickup || drop.IsPiece() || !drop.CanPickup() || drop.InTar())
			{
				continue;
			}
			drop.Load();
			ItemDrop.ItemData item = drop.m_itemData;
			if (item.m_shared.m_questItem || item.GetWeight() + __instance.GetInventory().GetTotalWeight() <= __instance.GetMaxCarryWeight())
			{
				continue; // Vanilla nimmt diesen Stapel ohnehin ganz.
			}
			int affordable = Plugin.Affordable(__instance, item);
			if (affordable > 0 && __instance.GetInventory().CanAddItem(item, affordable))
			{
				Plugin.TakePartial(__instance, drop, affordable);
				return;
			}
		}
	}
}

/// <summary>
/// Strg+Linksklick auf ein Item in der Kiste: Vanilla verschiebt den ganzen Stapel ins Inventar (auch wenn man danach
/// überladen ist). Hier nur so viel, wie man tragen kann und wie Platz hat; der Rest bleibt in der Kiste.
/// </summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class ChestCtrlClickPatch
{
	[HarmonyPriority(Priority.High)]
	private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, InventoryGrid.Modifier mod, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return false; // eine andere Mod hat den Klick abgefangen (z. B. verbundene Kisten schließen gerade)
		}
		if (!Plugin.On || !Plugin.ChestCtrlClick.Value || mod != InventoryGrid.Modifier.Move || item == null || grid == null || __instance.m_dragGo != null)
		{
			return true;
		}
		Player player = Player.m_localPlayer;
		Container? container = __instance.m_currentContainer;
		if (player == null || container == null || grid.GetInventory() != container.GetInventory() || item.m_shared.m_questItem)
		{
			return true; // nur aus der Kiste ins Inventar
		}
		int affordable = Plugin.Affordable(player, item);
		int amount = Mathf.Min(affordable, Plugin.Room(player.GetInventory(), item));
		if (amount >= item.m_stack)
		{
			return true; // passt ganz: wie Vanilla
		}
		if (amount <= 0)
		{
			Gc.Center(affordable <= 0 ? GcLoc.Get("gccl_too_heavy") : "$msg_noroom");
			return false;
		}
		Inventory from = grid.GetInventory();
		Inventory to = player.GetInventory();
		ItemDrop.ItemData copy = item.Clone();
		copy.m_stack = amount;
		int before = to.NrOfItemsIncludingStacks();
		to.AddItem(copy);
		int added = to.NrOfItemsIncludingStacks() - before;
		if (added <= 0)
		{
			return false;
		}
		from.RemoveItem(item, added); // genau das, was angekommen ist: nichts entsteht doppelt
		__instance.m_moveItemEffects.Create(__instance.transform.position, Quaternion.identity);
		Gc.TopLeft(GcLoc.Get("gccl_chest_partial", added, Localization.instance.Localize(item.m_shared.m_name)));
		return false;
	}
}
