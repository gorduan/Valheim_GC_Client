using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using GC.Client;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Client.Sort;

public enum SortMode
{
	Type,
	Name,
	Weight,
	Value
}

/// <summary>
/// GC_Sort: Buttons „Sortieren" am Spielerinventar und an offenen Kisten.
/// Fixierte Items (GC_ItemLock) bleiben auf ihrem Platz, die Schnellleiste (oberste Reihe) optional auch.
/// Gleiche Stapel werden zusammengeführt, Items mit Zusatzdaten (z. B. EpicLoot-Magie) nie.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_sort";
	public const string ModName = "GC_Sort";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<SortMode> Mode = null!;
	internal static ConfigEntry<bool> SkipHotbar = null!;
	internal static ConfigEntry<bool> MergeStacks = null!;
	internal static ConfigEntry<KeyboardShortcut> SortPlayerKey = null!;
	internal static ConfigEntry<KeyboardShortcut> SortContainerKey = null!;
	internal static ConfigEntry<Vector2> PlayerButtonOffset = null!;
	internal static ConfigEntry<Vector2> ContainerButtonOffset = null!;

	private static Button? playerButton;
	private static Button? containerButton;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "2 - Sort", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		Mode = Gc.Bind(Config, "2 - Sort", "SortMode", SortMode.Type, "Sortierung: Type (Kategorie), Name, Weight (schwerste zuerst), Value (wertvollste zuerst).");
		SkipHotbar = Gc.Bind(Config, "2 - Sort", "SkipHotbar", true, "Oberste Reihe des Spielerinventars (Schnellleiste 1-8) nicht sortieren.");
		MergeStacks = Gc.Bind(Config, "2 - Sort", "MergeStacks", true, "Angebrochene Stapel gleicher Items zusammenführen.");
		SortPlayerKey = Gc.Bind(Config, "2 - Sort - Interface", "SortInventoryKey", new KeyboardShortcut(KeyCode.None), "Taste zum Sortieren des Spielerinventars (bei offenem Inventar). Leer = nur Button.");
		SortContainerKey = Gc.Bind(Config, "2 - Sort - Interface", "SortContainerKey", new KeyboardShortcut(KeyCode.None), "Taste zum Sortieren der offenen Kiste. Leer = nur Button.");
		PlayerButtonOffset = Gc.Bind(Config, "2 - Sort - Interface", "InventoryButtonOffset", new Vector2(-126f, 6f), "Position des Inventar-Buttons relativ zur oberen rechten Ecke des Spielerinventars.");
		ContainerButtonOffset = Gc.Bind(Config, "2 - Sort - Interface", "ContainerButtonOffset", new Vector2(0f, 6f), "Position des Kisten-Buttons relativ zur oberen rechten Ecke des Kistenfensters.");
	}

	internal static void Update()
	{
		if (Gc.IsHeadless || InventoryGui.instance == null || Player.m_localPlayer == null)
		{
			return;
		}
		if (!On)
		{
			if (playerButton != null)
			{
				playerButton.gameObject.SetActive(false); // im Mod-Menü abgeschaltet
			}
			if (containerButton != null)
			{
				containerButton.gameObject.SetActive(false);
			}
			return;
		}
		EnsureButtons();
		if (!InventoryGui.IsVisible())
		{
			return;
		}
		if (Gc.Down(SortPlayerKey))
		{
			SortPlayer();
		}
		if (Gc.Down(SortContainerKey))
		{
			SortContainer();
		}
	}

	private static void EnsureButtons()
	{
		InventoryGui gui = InventoryGui.instance;
		if (gui.m_takeAllButton == null)
		{
			return;
		}
		if (playerButton != null)
		{
			playerButton.gameObject.SetActive(true);
		}
		if (containerButton != null)
		{
			containerButton.gameObject.SetActive(true);
		}
		if (playerButton == null && gui.m_player != null)
		{
			playerButton = GcUi.CloneButton(gui.m_takeAllButton, gui.m_player, "GC_SortInventory", GcLoc.Get("gcs_button"),
				new Vector2(1f, 1f), new Vector2(1f, 0f), PlayerButtonOffset.Value, new Vector2(120f, 32f), SortPlayer);
		}
		if (containerButton == null && gui.m_container != null)
		{
			containerButton = GcUi.CloneButton(gui.m_takeAllButton, gui.m_container, "GC_SortContainer", GcLoc.Get("gcs_button"),
				new Vector2(1f, 1f), new Vector2(1f, 0f), ContainerButtonOffset.Value, new Vector2(120f, 32f), SortContainer);
		}
	}

	private static void SortPlayer()
	{
		Player player = Player.m_localPlayer;
		InventoryGui.instance.SetupDragItem(null, null, 1);
		Sorter.Sort(player.GetInventory(), SkipHotbar.Value ? 1 : 0);
	}

	private static void SortContainer()
	{
		Container? container = InventoryGui.instance.m_currentContainer;
		if (container == null || !container.IsOwner() || !Gc.LinkedViewHealthy())
		{
			return;
		}
		InventoryGui.instance.SetupDragItem(null, null, 1);
		Sorter.Sort(container.GetInventory(), 0);
	}
}

internal static class Sorter
{
	/// <summary>
	/// Sortiert in zwei Schritten: erst wird ein Plan berechnet (welches Item welche Menge und welchen Platz bekommt),
	/// und nur wenn alles passt, wird das Inventar verändert. Bricht der Plan ab, bleibt alles unverändert.
	/// </summary>
	public static void Sort(Inventory inventory, int firstRow)
	{
		int width = inventory.GetWidth();
		int height = inventory.GetHeight();
		List<ItemDrop.ItemData> all = inventory.m_inventory;

		List<ItemDrop.ItemData> fixedItems = all.Where(item => item.m_gridPos.y < firstRow || Gc.IsLocked(item)).ToList();
		List<ItemDrop.ItemData> movable = all.Where(item => !fixedItems.Contains(item)).ToList();
		if (movable.Count == 0)
		{
			return;
		}

		List<(ItemDrop.ItemData Item, int Stack)> plan = Plugin.MergeStacks.Value ? Merge(movable) : movable.Select(item => (item, item.m_stack)).ToList();
		plan = Order(plan).ToList();
		if (!SameAmounts(movable, plan))
		{
			// Review 27.10.2026: vor dem Schreiben prüfen, sonst ginge beim Zusammenführen still etwas verloren
			Plugin.Log.LogWarning("Sortieren abgebrochen: Der Plan enthält nicht genau die Mengen des Inventars. Inventar bleibt unverändert.");
			return;
		}

		HashSet<(int, int)> occupied = new(fixedItems.Select(item => (item.m_gridPos.x, item.m_gridPos.y)));
		List<Vector2i> free = new();
		for (int y = firstRow; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				if (!occupied.Contains((x, y)))
				{
					free.Add(new Vector2i(x, y));
				}
			}
		}
		if (free.Count < plan.Count)
		{
			Plugin.Log.LogWarning("Sortieren abgebrochen: nicht genug freie Plätze. Inventar bleibt unverändert.");
			return;
		}

		all.Clear();
		all.AddRange(fixedItems);
		for (int i = 0; i < plan.Count; i++)
		{
			plan[i].Item.m_stack = plan[i].Stack;
			plan[i].Item.m_gridPos = free[i];
			all.Add(plan[i].Item);
		}
		inventory.Changed();
	}

	/// <summary>
	/// Berechnet zusammengelegte Stapel, ohne die Items zu verändern. Jedes Item wird höchstens bis zu seiner eigenen Stapelgröße
	/// gefüllt, größte zuerst (Review 27.10.2026: vorher nach der Stapelgröße des ersten Items; war die Summe größer als Anzahl ×
	/// Stapelgröße, etwa bei übergroßen Stapeln nach geänderter Stapelgröße, verschwand der Rest still). Passt die Summe nicht in die
	/// Stapel der Gruppe, bleibt die Gruppe, wie sie ist.
	/// </summary>
	private static List<(ItemDrop.ItemData Item, int Stack)> Merge(List<ItemDrop.ItemData> items)
	{
		List<(ItemDrop.ItemData, int)> result = new();
		foreach (IGrouping<string, ItemDrop.ItemData> group in items.GroupBy(StackKey))
		{
			List<ItemDrop.ItemData> list = group.ToList();
			long total = list.Sum(item => (long)item.m_stack);
			long room = list.Sum(item => (long)item.m_shared.m_maxStackSize);
			if (list.Count == 1 || room < total || list.Any(item => item.m_shared.m_maxStackSize <= 1 || item.m_customData.Count > 0 || item.m_equipped))
			{
				result.AddRange(list.Select(item => (item, item.m_stack)));
				continue;
			}
			foreach (ItemDrop.ItemData item in list.OrderByDescending(item => item.m_shared.m_maxStackSize))
			{
				if (total <= 0)
				{
					break;
				}
				int stack = (int)Math.Min(item.m_shared.m_maxStackSize, total);
				total -= stack;
				result.Add((item, stack));
			}
		}
		return result;
	}

	/// <summary>Enthält der Plan je Stapel-Schlüssel genau die Menge von vorher und jedes Item höchstens einmal?</summary>
	private static bool SameAmounts(List<ItemDrop.ItemData> items, List<(ItemDrop.ItemData Item, int Stack)> plan)
	{
		Dictionary<string, long> left = new();
		foreach (ItemDrop.ItemData item in items)
		{
			string key = StackKey(item);
			left[key] = (left.TryGetValue(key, out long sum) ? sum : 0) + item.m_stack;
		}
		HashSet<ItemDrop.ItemData> seen = new();
		foreach ((ItemDrop.ItemData item, int stack) in plan)
		{
			string key = StackKey(item);
			if (stack < 0 || !seen.Add(item) || !left.ContainsKey(key))
			{
				return false;
			}
			left[key] -= stack;
		}
		return left.Values.All(sum => sum == 0);
	}

	private static string StackKey(ItemDrop.ItemData item)
	{
		return $"{item.m_shared.m_name}|{item.m_quality}|{item.m_variant}|{item.m_worldLevel}|{item.m_crafterID}";
	}

	private static IEnumerable<(ItemDrop.ItemData Item, int Stack)> Order(List<(ItemDrop.ItemData Item, int Stack)> items)
	{
		Func<(ItemDrop.ItemData Item, int Stack), string> localName = entry => Localization.instance.Localize(entry.Item.m_shared.m_name);
		return Plugin.Mode.Value switch
		{
			SortMode.Name => items.OrderBy(localName).ThenByDescending(e => e.Item.m_quality).ThenByDescending(e => e.Stack),
			SortMode.Weight => items.OrderByDescending(e => e.Item.m_shared.m_weight * e.Stack).ThenBy(localName),
			SortMode.Value => items.OrderByDescending(e => e.Item.m_shared.m_value * e.Stack).ThenBy(localName),
			_ => items.OrderBy(e => TypeRank(e.Item)).ThenBy(localName).ThenByDescending(e => e.Item.m_quality).ThenByDescending(e => e.Stack),
		};
	}

	private static int TypeRank(ItemDrop.ItemData item)
	{
		return item.m_shared.m_itemType switch
		{
			ItemDrop.ItemData.ItemType.OneHandedWeapon => 0,
			ItemDrop.ItemData.ItemType.TwoHandedWeapon => 1,
			ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft => 1,
			ItemDrop.ItemData.ItemType.Bow => 2,
			ItemDrop.ItemData.ItemType.Shield => 3,
			ItemDrop.ItemData.ItemType.Tool => 4,
			ItemDrop.ItemData.ItemType.Torch => 5,
			ItemDrop.ItemData.ItemType.Helmet => 6,
			ItemDrop.ItemData.ItemType.Chest => 7,
			ItemDrop.ItemData.ItemType.Legs => 8,
			ItemDrop.ItemData.ItemType.Shoulder => 9,
			ItemDrop.ItemData.ItemType.Utility => 10,
			ItemDrop.ItemData.ItemType.Trinket => 10,
			ItemDrop.ItemData.ItemType.Ammo => 11,
			ItemDrop.ItemData.ItemType.AmmoNonEquipable => 11,
			ItemDrop.ItemData.ItemType.Consumable => 12,
			ItemDrop.ItemData.ItemType.Fish => 13,
			ItemDrop.ItemData.ItemType.Material => 14,
			ItemDrop.ItemData.ItemType.Trophy => 15,
			ItemDrop.ItemData.ItemType.Misc => 16,
			_ => 17,
		};
	}
}
