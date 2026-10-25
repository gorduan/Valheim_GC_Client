using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using GC.Client;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Client.Trash;

/// <summary>
/// GC_Trash: Items vernichten.
///  * Button „Vernichten" am Spielerinventar: Item mit der Maus aufnehmen, dann auf den Button klicken.
///  * Entf-Taste über einem Item im Inventar oder in einer offenen Kiste.
/// Ausgerüstete und fixierte Items (GC_ItemLock) werden nie vernichtet. Standardmäßig mit Rückfrage.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_trash";
	public const string ModName = "GC_Trash";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<bool> Confirm = null!;
	internal static ConfigEntry<bool> ShowButton = null!;
	internal static ConfigEntry<KeyboardShortcut> TrashKey = null!;
	internal static ConfigEntry<Vector2> ButtonOffset = null!;

	private static Button? button;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "1 - Trash", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		Confirm = Gc.Bind(Config, "1 - Trash", "Confirm", true, "Vor dem Vernichten nachfragen.");
		ShowButton = Gc.Bind(Config, "1 - Trash - Interface", "ShowButton", true, "Button 'Vernichten' am Spielerinventar anzeigen.");
		TrashKey = Gc.Bind(Config, "1 - Trash - Interface", "TrashKey", new KeyboardShortcut(KeyCode.Delete), "Taste, die das Item unter dem Mauszeiger vernichtet (Inventar oder Kiste).");
		ButtonOffset = Gc.Bind(Config, "1 - Trash - Interface", "ButtonOffset", new Vector2(0f, 6f), "Position des Buttons relativ zur oberen rechten Ecke des Spielerinventars (x nach rechts, y nach oben).");
	}

	internal static void Update()
	{
		if (Gc.IsHeadless || InventoryGui.instance == null || Player.m_localPlayer == null)
		{
			return;
		}
		if (!On)
		{
			if (button != null)
			{
				button.gameObject.SetActive(false); // im Mod-Menü abgeschaltet
			}
			return;
		}
		EnsureButton();
		if (!InventoryGui.IsVisible())
		{
			return;
		}
		if (Gc.Down(TrashKey))
		{
			TrashHovered();
		}
	}

	private static void EnsureButton()
	{
		if (button != null)
		{
			button.gameObject.SetActive(ShowButton.Value);
			return;
		}
		InventoryGui gui = InventoryGui.instance;
		if (gui.m_takeAllButton == null || gui.m_player == null)
		{
			return;
		}
		button = GcUi.CloneButton(gui.m_takeAllButton, gui.m_player, "GC_TrashButton", GcLoc.Get("gct_button"),
			new Vector2(1f, 1f), new Vector2(1f, 0f), ButtonOffset.Value, new Vector2(120f, 32f), OnButton);
	}

	private static void OnButton()
	{
		InventoryGui gui = InventoryGui.instance;
		if (gui.m_dragItem == null || gui.m_dragInventory == null)
		{
			Gc.Center(GcLoc.Get("gct_hint"));
			return;
		}
		Request(gui.m_dragInventory, gui.m_dragItem, true);
	}

	private static void TrashHovered()
	{
		InventoryGui gui = InventoryGui.instance;
		foreach (InventoryGrid? grid in new[] { gui.m_playerGrid, gui.m_containerGrid })
		{
			if (grid == null || !grid.isActiveAndEnabled)
			{
				continue;
			}
			InventoryElement? element = grid.GetHoveredElement();
			if (element == null)
			{
				continue;
			}
			ItemDrop.ItemData? item = grid.GetItem(element.Position);
			if (item != null)
			{
				Request(grid.GetInventory(), item, false);
			}
			return;
		}
	}

	private static void Request(Inventory inventory, ItemDrop.ItemData item, bool fromDrag)
	{
		Player player = Player.m_localPlayer;
		if (Gc.IsLocked(item))
		{
			Gc.Center(GcLoc.Get("gct_locked"));
			return;
		}
		if (item.m_equipped || player.IsItemEquiped(item))
		{
			Gc.Center(GcLoc.Get("gct_equipped"));
			return;
		}
		Container? container = InventoryGui.instance.m_currentContainer;
		if (inventory != player.GetInventory() && (container == null || container.GetInventory() != inventory || !container.IsOwner() || !Gc.LinkedViewHealthy()))
		{
			return;
		}

		int amount = fromDrag ? InventoryGui.instance.m_dragAmount : item.m_stack;
		amount = Mathf.Clamp(amount, 1, item.m_stack);
		string name = Localization.instance.Localize(item.m_shared.m_name);
		if (!Confirm.Value)
		{
			Execute(inventory, item, amount);
			return;
		}
		UnifiedPopup.Push(new YesNoPopup(GcLoc.Get("gct_confirm_title"), GcLoc.Get("gct_confirm_text", amount, name),
			() =>
			{
				UnifiedPopup.Pop();
				Execute(inventory, item, amount);
			},
			UnifiedPopup.Pop, false));
	}

	private static void Execute(Inventory inventory, ItemDrop.ItemData item, int amount)
	{
		if (!inventory.ContainsItem(item))
		{
			return;
		}
		InventoryGui gui = InventoryGui.instance;
		if (gui.m_dragItem == item)
		{
			gui.SetupDragItem(null, null, 1);
		}
		string name = Localization.instance.Localize(item.m_shared.m_name);
		if (amount >= item.m_stack)
		{
			inventory.RemoveItem(item);
		}
		else
		{
			inventory.RemoveItem(item, amount);
		}
		inventory.Changed();
		Gc.TopLeft(GcLoc.Get("gct_done", amount, name));
		Log.LogInfo($"{amount}x {item.m_shared.m_name} vernichtet.");
	}
}
