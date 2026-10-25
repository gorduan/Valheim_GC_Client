using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Activation = GC.Client.CarryLimit.Activation;
using ItemData = ItemDrop.ItemData;
using ItemType = ItemDrop.ItemData.ItemType;

namespace GC.Client.Compare;

/// <summary>
/// Ausrüstungs-Vergleich (Gorduan, 18.10.2026): Zeigt man im Inventar, in einer Kiste oder beim Händler auf eine Waffe, einen Schild,
/// ein Kleidungsstück, Schmuck oder Munition, erscheint neben Valheims Tooltip ein zweites Feld „Ausgerüstet“ mit dem, was beim
/// Anlegen ersetzt würde. Oben darin stehen die Unterschiede, Gewinn grün und Verlust rot (wie World of Warcraft und Diablo IV,
/// Recherche in Mod-Ideen-Klassifizierung.md, Teil 31), darunter der vollständige Tooltip der ausgerüsteten Items.
///
/// Verglichen wird die ganze Ausrüstung vorher und nachher nach Valheims Regeln (Humanoid.EquipItem): Ein Zweihänder ersetzt
/// Einhandwaffe und Schild zusammen, eine Fackel neben einer Einhandwaffe ersetzt nichts. Weggesteckte Waffen (Taste R) gelten als
/// ausgerüstet. Die Werte kommen aus den Spiel-Methoden (GetArmor, GetDamage, GetBaseBlockPower …); EpicLoot rechnet Schaden und
/// Blocken dort nur für das angelegte Item mit Magie, für das gezeigte Item holt EpicLootLink dieselbe Rechnung „als wäre es angelegt“.
/// Valheims eigener Tooltip bleibt unverändert. EpicLoots eigener Vergleich (Links-Strg) wird ausgeblendet, solange unser Feld steht.
/// Eigene Mod nach Gorduans Wunsch; HoverCompare (MIT) diente nur zum Lesen.
/// </summary>
internal static class Plugin
{
	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<Activation> Mode = null!;
	internal static ConfigEntry<KeyboardShortcut> Key = null!;
	internal static ConfigEntry<bool> Details = null!;
	private static bool toggledOn = true;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		const string section = "13 - Compare";
		Enabled = Gc.Bind(Config, section, "Enabled", true, "Ausrüstungs-Vergleich ein- oder ausschalten (auch im Menü GC Option).");
		Mode = Gc.Bind(Config, section, "Activation", Activation.Always, "Always = immer, Hold = nur solange die Taste gehalten wird, Toggle = Taste schaltet an/aus.");
		Key = Gc.Bind(Config, section, "Key", new KeyboardShortcut(KeyCode.LeftShift), "Taste für Hold bzw. Toggle.");
		Details = Gc.Bind(Config, section, "Details", true, "Im Feld Ausgerüstet unter den Unterschieden auch den vollständigen Tooltip der ausgerüsteten Items zeigen.");
	}

	internal static void Update()
	{
		// nur bei offenem Inventar oder Händler: Umschalt ist sonst Rennen und würde bei jedem Lossprinten umschalten
		bool open = InventoryGui.IsVisible() || StoreGui.IsVisible();
		if (Enabled.Value && Mode.Value == Activation.Toggle && open && !Typing() && Key.Value.MainKey != KeyCode.None && Key.Value.IsDown())
		{
			toggledOn = !toggledOn;
			Gc.Center(GcLoc.Get(toggledOn ? "gccmp_on" : "gccmp_off"));
		}
		Panel.Watch();
	}

	/// <summary>Soll der Vergleich jetzt erscheinen? Anders als Gc.Held auch beim Händler (dort zählt Gc.TextInputActive den Laden mit).</summary>
	internal static bool Wanted => Enabled.Value && Mode.Value switch
	{
		Activation.Hold => Key.Value.MainKey != KeyCode.None && Key.Value.IsPressed(),
		Activation.Toggle => toggledOn,
		_ => true,
	};

	private static bool Typing() => global::Console.IsVisible() || TextInput.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus());
}

/// <summary>Welches Item zum gerade gezeigten Tooltip gehört: Inventar und Kisten (InventoryGrid) oder Händler (StoreGui).</summary>
internal static class Hover
{
	private static UITooltip? gridTooltip;
	private static ItemData? gridItem;
	private static int gridFrame = -10;
	internal static readonly Dictionary<UITooltip, ItemData> Shop = new();

	internal static void Grid(UITooltip tooltip, ItemData item)
	{
		gridTooltip = tooltip;
		gridItem = item;
		gridFrame = Time.frameCount;
	}

	internal static ItemData? Of(UITooltip current)
	{
		if (ReferenceEquals(current, gridTooltip))
		{
			// InventoryGrid.UpdateGui baut den Tooltip jedes Bild nur für das Feld unter dem Mauszeiger: alt = nicht mehr darüber
			return Time.frameCount - gridFrame <= 1 ? gridItem : null;
		}
		return Shop.TryGetValue(current, out ItemData item) ? item : null;
	}
}

[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.CreateItemTooltip))]
internal static class GridTooltipPatch
{
	// Läuft auch, wenn EpicLoot das Original per Prefix ersetzt (Harmony ruft Postfixe trotzdem)
	private static void Postfix(ItemData item, UITooltip tooltip)
	{
		if (item != null && tooltip != null)
		{
			Hover.Grid(tooltip, item);
		}
	}
}

[HarmonyPatch(typeof(StoreGui), nameof(StoreGui.FillList))]
internal static class ShopListPatch
{
	internal static bool Filling;
	internal static List<Trader.TradeItem>? Listed;

	private static void Prefix()
	{
		Filling = true;
		Listed = null;
	}

	/// <summary>
	/// FillList legt je Angebot ein Listen-Element in derselben Reihenfolge wie GetAvailableItems an. Zur Sicherheit nur, wenn
	/// der Tooltip-Titel zum Angebot passt. Gekaufte Items bekommen die Weltstufe (Inventory.AddItem): die Kopie auch.
	/// </summary>
	[HarmonyPriority(Priority.First)]
	private static void Postfix(StoreGui __instance)
	{
		Filling = false;
		Hover.Shop.Clear();
		List<Trader.TradeItem>? listed = Listed;
		Listed = null;
		if (listed == null)
		{
			return;
		}
		int count = Mathf.Min(listed.Count, __instance.m_itemList.Count);
		for (int i = 0; i < count; i++)
		{
			Trader.TradeItem trade = listed[i];
			GameObject element = __instance.m_itemList[i];
			UITooltip? tooltip = element != null ? element.GetComponent<UITooltip>() : null;
			if (tooltip != null && trade.m_prefab != null && string.IsNullOrEmpty(trade.m_tooltip) // eigener Händlertext: kein Item-Tooltip
				&& tooltip.m_topic == trade.m_prefab.m_itemData.m_shared.m_name)
			{
				ItemData offer = trade.m_prefab.m_itemData.Clone();
				offer.m_worldLevel = Game.m_worldLevel;
				Hover.Shop[tooltip] = offer;
			}
		}
	}
}

[HarmonyPatch(typeof(Trader), nameof(Trader.GetAvailableItems))]
internal static class ShopItemsPatch
{
	[HarmonyPriority(Priority.Last)] // nach Mods, die die Angebotsliste ändern
	private static void Postfix(List<Trader.TradeItem> __result)
	{
		if (ShopListPatch.Filling && ShopListPatch.Listed == null)
		{
			ShopListPatch.Listed = __result;
		}
	}
}

[HarmonyPatch(typeof(UITooltip), nameof(UITooltip.LateUpdate))]
internal static class TooltipLatePatch
{
	// Läuft für jedes Element mit Tooltip; nur der gerade gezeigte zählt. Nach Valheims Code: der Tooltip steht schon am Mauszeiger.
	// Nach EpicLoot: Seit 0.15.0 setzt und bemisst EpicLoot seinen Kasten im selben LateUpdate (PlaceTooltip, Priorität Last).
	[HarmonyAfter("randyknapp.mods.epicloot")]
	private static void Postfix(UITooltip __instance)
	{
		if (ReferenceEquals(__instance, UITooltip.m_current))
		{
			Panel.Refresh(__instance);
		}
	}
}

[HarmonyPatch(typeof(UITooltip), nameof(UITooltip.HideTooltip))]
internal static class TooltipHidePatch
{
	private static void Postfix() => Panel.Hide();
}

/// <summary>
/// Das zweite Feld: eine eigene Instanz von Valheims Tooltip-Vorlage (Kinder „Topic“ und „Text“, Kind 0 = sichtbarer Kasten) neben
/// dem gezeigten Tooltip. Text nur neu, wenn sich Item oder Ausrüstung ändern (und alle 2 s für Haltbarkeit und Skill-Werte),
/// Lage jedes Bild. Fehler nur einmal ins Log, weil der Patch in jedem LateUpdate läuft.
/// </summary>
internal static class Panel
{
	private static GameObject? root;
	private static GameObject? source;
	private static TMP_Text? topic;
	private static TMP_Text? body;
	private static int hash;
	private static bool hasContent;
	private static int shownFrame = -10;
	private static bool reported;
	private static GameObject? laidOut;
	private static readonly Vector3[] Corners = new Vector3[4];

	internal static void Refresh(UITooltip current)
	{
		try
		{
			if (!Show(current))
			{
				Hide();
			}
		}
		catch (Exception e)
		{
			hash = 0; // nächstes Bild neu bauen, nicht den Text des vorigen Items zeigen
			hasContent = false;
			Hide();
			if (!reported)
			{
				reported = true;
				Plugin.Log.LogWarning($"Ausrüstungs-Vergleich: {e}");
			}
		}
	}

	internal static void Hide()
	{
		if (root != null && root.activeSelf)
		{
			root.SetActive(false);
			EpicLootLink.RestoreComparison();
		}
	}

	/// <summary>Aus ClientPlugin.Update: Feld weg, wenn kein Tooltip es mehr nachführt (Inventar zu, Element zerstört).</summary>
	internal static void Watch()
	{
		if (root != null && root.activeSelf && (!Plugin.Enabled.Value || Time.frameCount - shownFrame > 2))
		{
			root.SetActive(false);
			EpicLootLink.RestoreComparison();
		}
	}

	private static bool Show(UITooltip current)
	{
		GameObject? main = UITooltip.m_tooltip;
		Player? player = Player.m_localPlayer;
		if (main == null || !main.activeInHierarchy || player == null || !Plugin.Wanted)
		{
			laidOut = null; // wird der Tooltip sichtbar, legt Place ihn einmal selbst aus
			return false;
		}
		ItemData? item = Hover.Of(current);
		if (item == null || !Comparison.Possible(item) || !Ensure(current, main))
		{
			return false;
		}
		int now = Comparison.Hash(player, item);
		if (now != hash)
		{
			hash = 0;
			hasContent = false;
			hasContent = Comparison.Build(player, item, out string title, out string text);
			hash = now;
			if (hasContent)
			{
				root!.SetActive(true); // vor dem Text: inaktive Layouts rechnen nicht
				topic!.text = Localization.instance.Localize(title);
				body!.text = Localization.instance.Localize(text);
				if (root.transform.GetChild(0) is RectTransform box)
				{
					LayoutRebuilder.ForceRebuildLayoutImmediate(box);
				}
			}
		}
		if (!hasContent)
		{
			return false;
		}
		if (!root!.activeSelf)
		{
			root.SetActive(true);
		}
		Order(main);
		Place(main);
		EpicLootLink.HideComparison();
		shownFrame = Time.frameCount;
		return true;
	}

	/// <summary>Feld anlegen (je Tooltip-Vorlage eins) und unter denselben Canvas wie Valheims Tooltip hängen.</summary>
	private static bool Ensure(UITooltip current, GameObject main)
	{
		GameObject prefab = current.m_tooltipPrefab;
		if (prefab == null)
		{
			return false;
		}
		Transform parent = main.transform.parent;
		if (root == null || source != prefab)
		{
			if (root != null)
			{
				UnityEngine.Object.Destroy(root);
			}
			root = UnityEngine.Object.Instantiate(prefab, parent, false);
			root.name = "GC_CompareTooltip";
			source = prefab;
			hash = 0;
			foreach (Graphic graphic in root.GetComponentsInChildren<Graphic>(true))
			{
				graphic.raycastTarget = false; // das Feld darf keine Mausziele verdecken
			}
			CanvasGroup group = root.GetComponent<CanvasGroup>();
			if (group == null)
			{
				group = root.AddComponent<CanvasGroup>();
			}
			group.blocksRaycasts = false;
			group.interactable = false;
			topic = Child(root.transform, "Topic");
			body = Child(root.transform, "Text");
			root.SetActive(false);
		}
		else if (root.transform.parent != parent)
		{
			root.transform.SetParent(parent, false);
		}
		return topic != null && body != null && root.transform.childCount > 0;
	}

	private static TMP_Text? Child(Transform parent, string name)
	{
		Transform child = global::Utils.FindChild(parent, name);
		return child != null ? child.GetComponent<TMP_Text>() : null;
	}

	/// <summary>Unter Valheims Tooltip zeichnen, damit der bei Überlappung oben liegt.</summary>
	private static void Order(GameObject main)
	{
		Transform own = root!.transform;
		if (own.parent != main.transform.parent)
		{
			return;
		}
		int index = main.transform.GetSiblingIndex();
		if (own.GetSiblingIndex() > index)
		{
			own.SetSiblingIndex(index);
		}
	}

	/// <summary>
	/// Neben den sichtbaren Kasten des Tooltips (Kind 0; mit EpicLoot dessen Scroll-Ansicht), oben bündig, auf der vom Mauszeiger
	/// abgewandten Seite; passt es dort nicht, auf die andere Seite des Zeigers. Höher als der Bildschirm: verkleinert. Der Canvas
	/// ist Screen Space Overlay (Valheim setzt den Tooltip auf ZInput.pointerPosition), Weltkoordinaten sind also Pixel.
	/// </summary>
	private static void Place(GameObject main)
	{
		Transform own = root!.transform;
		if (main.transform.childCount == 0 || main.transform.GetChild(0) is not RectTransform mainBox || own.GetChild(0) is not RectTransform box)
		{
			return;
		}
		if (!ReferenceEquals(main, laidOut))
		{
			laidOut = main; // inaktiv legt Unity nicht aus: Valheims Kasten hat im ersten sichtbaren Bild noch die alte Größe
			LayoutRebuilder.ForceRebuildLayoutImmediate(mainBox);
		}
		mainBox.GetWorldCorners(Corners);
		float left = Corners[0].x;
		float right = Corners[2].x;
		float top = Corners[1].y;
		own.localScale = Vector3.one;
		box.GetWorldCorners(Corners);
		float width = Corners[2].x - Corners[0].x;
		float height = Corners[1].y - Corners[0].y;
		if (width < 1f || height < 1f)
		{
			return;
		}
		float unit = own.parent != null ? own.parent.lossyScale.x : 1f;
		float gap = 6f * unit;
		float room = Screen.height - 2f * gap;
		if (height > room)
		{
			float scale = Mathf.Max(0.5f, room / height);
			own.localScale = new Vector3(scale, scale, 1f);
			width *= scale;
			height *= scale;
		}
		float pointer = ZInput.IsMouseActive() ? ZInput.pointerPosition.x : (left + right) / 2f;
		float pad = 16f * unit;
		float x;
		if ((left + right) / 2f >= pointer)
		{
			x = right + gap;
			if (x + width > Screen.width)
			{
				x = Mathf.Min(left, pointer - pad) - gap - width;
			}
		}
		else
		{
			x = left - gap - width;
			if (x < 0f)
			{
				x = Mathf.Max(right, pointer + pad) + gap;
			}
		}
		x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - width));
		float y = Mathf.Clamp(top, Mathf.Min(height, Screen.height), Screen.height);
		box.GetWorldCorners(Corners);
		own.position += new Vector3(x - Corners[1].x, y - Corners[1].y, 0f);
	}
}

/// <summary>Was alles angelegt ist, als feste Plätze (wie die Felder in Humanoid).</summary>
internal sealed class Loadout
{
	internal const int Right = 0, Left = 1, Chest = 2, Legs = 3, Helmet = 4, Shoulder = 5, Utility = 6, Trinket = 7, Ammo = 8;
	internal readonly ItemData?[] Slots = new ItemData?[9];

	/// <summary>Waffen weggesteckt (Taste R): Valheim merkt sie in m_hiddenRightItem/m_hiddenLeftItem, für den Vergleich sind sie angelegt.</summary>
	internal bool Hidden;

	internal static Loadout Of(Player player)
	{
		Loadout loadout = new();
		ItemData?[] s = loadout.Slots;
		s[Right] = player.m_rightItem;
		s[Left] = player.m_leftItem;
		s[Chest] = player.m_chestItem;
		s[Legs] = player.m_legItem;
		s[Helmet] = player.m_helmetItem;
		s[Shoulder] = player.m_shoulderItem;
		s[Utility] = player.m_utilityItem;
		s[Trinket] = player.m_trinketItem;
		s[Ammo] = player.m_ammoItem;
		if (s[Right] == null && s[Left] == null && (player.m_hiddenRightItem != null || player.m_hiddenLeftItem != null))
		{
			s[Right] = player.m_hiddenRightItem;
			s[Left] = player.m_hiddenLeftItem;
			loadout.Hidden = true;
		}
		return loadout;
	}

	internal bool Holds(ItemData item) => Array.IndexOf(Slots, item) >= 0;

	internal static bool IsHandItem(ItemType type) => type is ItemType.Tool or ItemType.Torch or ItemType.OneHandedWeapon or ItemType.Shield
		or ItemType.Bow or ItemType.TwoHandedWeapon or ItemType.TwoHandedWeaponLeft;

	/// <summary>
	/// Belegung nach dem Anlegen, Regel für Regel wie Humanoid.EquipItem; was dabei abgelegt würde, kommt in displaced.
	/// Sind die Waffen weggesteckt, vergisst Valheim beim Anlegen eines Hand-Items beide versteckten (m_hidden… = null).
	/// null: nicht anlegbar.
	/// </summary>
	internal Loadout? With(ItemData item, List<ItemData> displaced)
	{
		Loadout next = new();
		Array.Copy(Slots, next.Slots, Slots.Length);
		ItemData?[] s = next.Slots;
		void Drop(int slot)
		{
			if (s[slot] is ItemData old)
			{
				displaced.Add(old);
				s[slot] = null;
			}
		}
		bool Is(int slot, ItemType type) => s[slot] is ItemData held && held.m_shared.m_itemType == type;

		ItemType itemType = item.m_shared.m_itemType;
		if (Hidden && IsHandItem(itemType))
		{
			Drop(Right);
			Drop(Left);
		}
		switch (itemType)
		{
			case ItemType.Tool:
				Drop(Right);
				Drop(Left);
				s[Right] = item;
				break;
			case ItemType.Torch:
				if (s[Right] != null && s[Left] == null && Is(Right, ItemType.OneHandedWeapon))
				{
					s[Left] = item; // Fackel in die freie linke Hand, nichts wird ersetzt
				}
				else
				{
					Drop(Right);
					if (s[Left] != null && !Is(Left, ItemType.Shield))
					{
						Drop(Left);
					}
					s[Right] = item;
				}
				break;
			case ItemType.OneHandedWeapon:
				if (Is(Right, ItemType.Torch) && s[Left] == null)
				{
					s[Left] = s[Right]; // Fackel wandert nach links
					s[Right] = null;
				}
				Drop(Right);
				if (s[Left] != null && !Is(Left, ItemType.Shield) && !Is(Left, ItemType.Torch))
				{
					Drop(Left);
				}
				s[Right] = item;
				break;
			case ItemType.Shield:
				Drop(Left);
				if (s[Right] != null && !Is(Right, ItemType.OneHandedWeapon) && !Is(Right, ItemType.Torch))
				{
					Drop(Right);
				}
				s[Left] = item;
				break;
			case ItemType.Bow:
				Drop(Left);
				Drop(Right);
				s[Left] = item;
				break;
			case ItemType.TwoHandedWeapon:
				Drop(Left);
				Drop(Right);
				s[Right] = item;
				break;
			case ItemType.TwoHandedWeaponLeft:
				Drop(Left);
				Drop(Right);
				s[Left] = item;
				break;
			case ItemType.Chest:
				Drop(Chest);
				s[Chest] = item;
				break;
			case ItemType.Legs:
				Drop(Legs);
				s[Legs] = item;
				break;
			case ItemType.Helmet:
				Drop(Helmet);
				s[Helmet] = item;
				break;
			case ItemType.Shoulder:
				Drop(Shoulder);
				s[Shoulder] = item;
				break;
			case ItemType.Utility:
				Drop(Utility);
				s[Utility] = item;
				break;
			case ItemType.Trinket:
				Drop(Trinket);
				s[Trinket] = item;
				break;
			case ItemType.Ammo:
				Drop(Ammo);
				s[Ammo] = item;
				break;
			default:
				return null;
		}
		return next;
	}
}

/// <summary>Inhalt des Feldes: Unterschiede der ganzen Ausrüstung vorher/nachher, dann die Tooltips der ersetzten Items.</summary>
internal static class Comparison
{
	private const string Good = "#8CE68C";
	private const string Bad = "#F27C7C";
	private const string Neutral = "#BFBFBF";

	/// <summary>Plätze, über die Valheim Ausrüstungs-Modifikatoren (Player.GetEquipmentModifier) und Setteile (GetSetCount) zählt.</summary>
	private static readonly int[] WornSlots = { Loadout.Right, Loadout.Left, Loadout.Chest, Loadout.Legs, Loadout.Helmet, Loadout.Shoulder, Loadout.Utility, Loadout.Trinket };

	/// <summary>Plätze für die Eitr-Regeneration (Player.GetEquipmentEitrRegenModifier, ohne Schmuckstück).</summary>
	private static readonly int[] EitrSlots = { Loadout.Right, Loadout.Left, Loadout.Chest, Loadout.Legs, Loadout.Helmet, Loadout.Shoulder, Loadout.Utility };

	/// <summary>Rüstung und Resistenzen: nur diese vier (Player.GetBodyArmor, Player.ApplyArmorDamageMods).</summary>
	private static readonly int[] ArmorSlots = { Loadout.Chest, Loadout.Legs, Loadout.Helmet, Loadout.Shoulder };

	private static readonly (HitData.DamageType Type, string Name)[] ResistTypes =
	{
		(HitData.DamageType.Blunt, "$inventory_blunt"),
		(HitData.DamageType.Slash, "$inventory_slash"),
		(HitData.DamageType.Pierce, "$inventory_pierce"),
		(HitData.DamageType.Fire, "$inventory_fire"),
		(HitData.DamageType.Frost, "$inventory_frost"),
		(HitData.DamageType.Lightning, "$inventory_lightning"),
		(HitData.DamageType.Poison, "$inventory_poison"),
		(HitData.DamageType.Spirit, "$inventory_spirit"),
	};

	private static FieldInfo?[]? modifierFields;
	private static Player? viewer;
	private static readonly FieldInfo? EitrField = typeof(ItemData.SharedData).GetField(nameof(ItemData.SharedData.m_eitrRegenModifier));

	internal static bool Possible(ItemData item) => item.IsEquipable() && item.m_shared.m_itemType != ItemType.Tool; // Hammer, Hacke, Kultivator: nichts zu vergleichen

	/// <summary>Kennung für Item und Ausrüstung; ändert sie sich, wird der Text neu gebaut. Alle 2 s ohnehin (Haltbarkeit, Skill-Werte).</summary>
	internal static int Hash(Player player, ItemData item)
	{
		unchecked
		{
			int h = Mix(17, item);
			h = Mix(h, player.m_rightItem);
			h = Mix(h, player.m_leftItem);
			h = Mix(h, player.m_chestItem);
			h = Mix(h, player.m_legItem);
			h = Mix(h, player.m_helmetItem);
			h = Mix(h, player.m_shoulderItem);
			h = Mix(h, player.m_utilityItem);
			h = Mix(h, player.m_trinketItem);
			h = Mix(h, player.m_ammoItem);
			h = Mix(h, player.m_hiddenRightItem);
			h = Mix(h, player.m_hiddenLeftItem);
			h = h * 31 + (Plugin.Details.Value ? 1 : 2);
			h = h * 31 + (int)(Time.unscaledTime / 2f);
			h = h * 31 + (Localization.instance?.GetSelectedLanguage()?.GetHashCode() ?? 0);
			return h == 0 ? 1 : h;
		}
	}

	private static int Mix(int h, ItemData? item) => unchecked(h * 31 + (item == null ? 0 : RuntimeHelpers.GetHashCode(item) ^ (item.m_quality << 24) ^ (int)item.m_durability));

	internal static bool Build(Player player, ItemData item, out string title, out string text)
	{
		title = "";
		text = "";
		Loadout before = Loadout.Of(player);
		if (item.m_equipped || before.Holds(item))
		{
			return false; // schon angelegt (auch in Zusatzplätzen anderer Mods) oder weggesteckt
		}
		viewer = player;
		List<ItemData> displaced = new();
		Loadout? after = before.With(item, displaced);
		if (after == null || displaced.Count == 0)
		{
			return false; // ersetzt nichts: kein Vergleich (wie in Rollenspielen bei leerem Platz)
		}

		StringBuilder changes = new();
		Changes(changes, player, item, before, after, displaced);
		StringBuilder sb = new();
		sb.Append("<color=yellow>").Append(GcLoc.Get("gccmp_changes")).Append("</color>");
		if (changes.Length == 0)
		{
			Line(sb, Neutral, GcLoc.Get("gccmp_none"));
		}
		else
		{
			sb.Append(changes);
		}
		if (Plugin.Details.Value)
		{
			foreach (ItemData old in displaced)
			{
				sb.Append("\n\n");
				if (displaced.Count > 1)
				{
					sb.Append("<color=orange><b>").Append(EpicLootLink.Name(old)).Append("</b></color>\n");
				}
				sb.Append(old.GetTooltip());
			}
		}
		text = sb.ToString();

		bool hidden = before.Hidden && Loadout.IsHandItem(item.m_shared.m_itemType);
		StringBuilder names = new();
		foreach (ItemData old in displaced)
		{
			names.Append(names.Length > 0 ? " + " : "").Append(EpicLootLink.Name(old));
		}
		title = $"<color=#A8A8A8><i>{GcLoc.Get(hidden ? "gccmp_equipped_hidden" : "gccmp_equipped")}:</i></color> {names}";
		return true;
	}

	private static void Changes(StringBuilder sb, Player player, ItemData item, Loadout before, Loadout after, List<ItemData> displaced)
	{
		Number(sb, "$item_armor", Armor(before), Armor(after), true);
		Resistances(sb, player, before, after);

		ItemData? weaponBefore = Weapon(player, before);
		ItemData? weaponAfter = Weapon(player, after);
		if (!ReferenceEquals(weaponBefore, weaponAfter))
		{
			Attack(sb, weaponBefore, weaponAfter);
		}
		ItemData? ammoBefore = before.Slots[Loadout.Ammo];
		ItemData? ammoAfter = after.Slots[Loadout.Ammo];
		if (!ReferenceEquals(ammoBefore, ammoAfter))
		{
			Damage(sb, ammoBefore, ammoAfter);
			Number(sb, "$item_knockback", ammoBefore?.m_shared.m_attackForce ?? 0f, ammoAfter?.m_shared.m_attackForce ?? 0f, true);
		}
		// Geblockt wird mit der linken Hand, sonst mit der Waffe (Humanoid.GetCurrentBlocker)
		ItemData? blockBefore = before.Slots[Loadout.Left] ?? weaponBefore;
		ItemData? blockAfter = after.Slots[Loadout.Left] ?? weaponAfter;
		if (!ReferenceEquals(blockBefore, blockAfter))
		{
			Block(sb, blockBefore, blockAfter);
		}

		Modifiers(sb, before, after);
		if (item.m_shared.m_itemType != ItemType.Ammo)
		{
			float weight = 0f;
			foreach (ItemData old in displaced)
			{
				weight += old.GetWeight();
			}
			Number(sb, "$item_weight", weight, item.GetWeight(), false);
			if (displaced.Count == 1 && item.m_shared.m_useDurability && displaced[0].m_shared.m_useDurability)
			{
				Number(sb, "$item_durability", displaced[0].GetMaxDurability(), item.GetMaxDurability(), true);
			}
		}
		Sets(sb, before, after);
		Effects(sb, before, after);
	}

	/// <summary>Angriffswaffe wie Humanoid.GetCurrentWeapon: rechts, sonst links (keine Fackel), sonst Fäuste.</summary>
	private static ItemData? Weapon(Player player, Loadout loadout)
	{
		ItemData? right = loadout.Slots[Loadout.Right];
		ItemData? left = loadout.Slots[Loadout.Left];
		if (right != null && right.IsWeapon())
		{
			return right;
		}
		if (left != null && left.IsWeapon() && left.m_shared.m_itemType != ItemType.Torch)
		{
			return left;
		}
		return player.m_unarmedWeapon != null ? player.m_unarmedWeapon.m_itemData : null;
	}

	private static float Armor(Loadout loadout)
	{
		float armor = 0f;
		foreach (int slot in ArmorSlots)
		{
			if (loadout.Slots[slot] is ItemData worn)
			{
				armor += worn.GetArmor();
			}
		}
		return armor;
	}

	/// <summary>Resistenzen wie Player.ApplyArmorDamageMods: die beste je Schadensart zählt (HitData.DamageModifiers.Apply).</summary>
	private static void Resistances(StringBuilder sb, Player player, Loadout before, Loadout after)
	{
		HitData.DamageModifiers was = Resist(player, before);
		HitData.DamageModifiers will = Resist(player, after);
		foreach ((HitData.DamageType type, string name) in ResistTypes)
		{
			HitData.DamageModifier a = was.GetModifier(type);
			HitData.DamageModifier b = will.GetModifier(type);
			if (a != b)
			{
				Line(sb, Factor(b) < Factor(a) ? Good : Bad, $"{GcLoc.Get("gccmp_resist", name)}: {ModifierName(a)} » {ModifierName(b)}", labelPlain: true);
			}
		}
	}

	private static HitData.DamageModifiers Resist(Player player, Loadout loadout)
	{
		HitData.DamageModifiers mods = player.m_damageModifiers.Clone();
		foreach (int slot in ArmorSlots)
		{
			if (loadout.Slots[slot] is ItemData worn && worn.m_shared.m_damageModifiers != null)
			{
				mods.Apply(worn.m_shared.m_damageModifiers);
			}
		}
		return mods;
	}

	/// <summary>Schadensfaktor je Stufe (HitData.ApplyModifier): kleiner ist besser.</summary>
	private static float Factor(HitData.DamageModifier mod) => mod switch
	{
		HitData.DamageModifier.Immune or HitData.DamageModifier.Ignore => 0f,
		HitData.DamageModifier.VeryResistant => 0.25f,
		HitData.DamageModifier.Resistant => 0.5f,
		HitData.DamageModifier.SlightlyResistant => 0.75f,
		HitData.DamageModifier.SlightlyWeak => 1.25f,
		HitData.DamageModifier.Weak => 1.5f,
		HitData.DamageModifier.VeryWeak => 2f,
		_ => 1f,
	};

	private static string ModifierName(HitData.DamageModifier mod) => mod switch
	{
		HitData.DamageModifier.Immune or HitData.DamageModifier.Ignore => "$inventory_immune",
		HitData.DamageModifier.VeryResistant => "$inventory_veryresistant",
		HitData.DamageModifier.Resistant => "$inventory_resistant",
		HitData.DamageModifier.SlightlyResistant => "$inventory_slightlyresistant",
		HitData.DamageModifier.SlightlyWeak => "$inventory_slightlyweak",
		HitData.DamageModifier.Weak => "$inventory_weak",
		HitData.DamageModifier.VeryWeak => "$inventory_veryweak",
		_ => GcLoc.Get("gccmp_res_normal"),
	};

	private static void Attack(StringBuilder sb, ItemData? a, ItemData? b)
	{
		if (Damage(sb, a, b) >= 2)
		{
			Number(sb, GcLoc.Get("gccmp_total"), Total(a), Total(b), true);
		}
		Number(sb, "$item_staminause", a?.m_shared.m_attack.m_attackStamina ?? 0f, b?.m_shared.m_attack.m_attackStamina ?? 0f, false);
		Number(sb, "$item_eitruse", a?.m_shared.m_attack.m_attackEitr ?? 0f, b?.m_shared.m_attack.m_attackEitr ?? 0f, false);
		Number(sb, "$item_healthuse", a?.m_shared.m_attack.m_attackHealth ?? 0f, b?.m_shared.m_attack.m_attackHealth ?? 0f, false);
		Number(sb, "$item_knockback", a?.m_shared.m_attackForce ?? 0f, b?.m_shared.m_attackForce ?? 0f, true);
		Multiplier(sb, "$item_backstab", a?.m_shared.m_backstabBonus ?? 1f, b?.m_shared.m_backstabBonus ?? 1f);
	}

	/// <summary>Schaden je Art (mit Holz und Erz, die Valheims Tooltip nicht zeigt); Rückgabe: Anzahl geänderter Arten.</summary>
	private static int Damage(StringBuilder sb, ItemData? a, ItemData? b)
	{
		HitData.DamageTypes x = DamageOf(a);
		HitData.DamageTypes y = DamageOf(b);
		int lines = 0;
		lines += Number(sb, "$inventory_damage", x.m_damage, y.m_damage, true) ? 1 : 0;
		lines += Number(sb, "$inventory_blunt", x.m_blunt, y.m_blunt, true) ? 1 : 0;
		lines += Number(sb, "$inventory_slash", x.m_slash, y.m_slash, true) ? 1 : 0;
		lines += Number(sb, "$inventory_pierce", x.m_pierce, y.m_pierce, true) ? 1 : 0;
		lines += Number(sb, "$inventory_fire", x.m_fire, y.m_fire, true) ? 1 : 0;
		lines += Number(sb, "$inventory_frost", x.m_frost, y.m_frost, true) ? 1 : 0;
		lines += Number(sb, "$inventory_lightning", x.m_lightning, y.m_lightning, true) ? 1 : 0;
		lines += Number(sb, "$inventory_poison", x.m_poison, y.m_poison, true) ? 1 : 0;
		lines += Number(sb, "$inventory_spirit", x.m_spirit, y.m_spirit, true) ? 1 : 0;
		Number(sb, "$inventory_chop", x.m_chop, y.m_chop, true);
		Number(sb, "$inventory_pickaxe", x.m_pickaxe, y.m_pickaxe, true);
		return lines;
	}

	private static float Total(ItemData? item)
	{
		if (item == null)
		{
			return 0f;
		}
		HitData.DamageTypes d = DamageOf(item);
		return d.m_damage + d.m_blunt + d.m_slash + d.m_pierce + d.m_fire + d.m_frost + d.m_lightning + d.m_poison + d.m_spirit;
	}

	/// <summary>Schaden wie im Kampf: angelegt rechnet EpicLoot selbst, sonst „als wäre es angelegt“ (ohne EpicLoot: Valheims Wert).</summary>
	private static HitData.DamageTypes DamageOf(ItemData? item)
	{
		if (item == null)
		{
			return default;
		}
		HitData.DamageTypes damage = item.GetDamage();
		if (viewer != null && !viewer.IsItemEquiped(item))
		{
			EpicLootLink.AddDamage(viewer, item, ref damage);
		}
		return damage;
	}

	private static void Block(StringBuilder sb, ItemData? a, ItemData? b)
	{
		Number(sb, "$item_blockarmor", BlockOf(a, false), BlockOf(b, false), true);
		Number(sb, "$item_blockforce", BlockOf(a, true), BlockOf(b, true), true);
		Multiplier(sb, "$item_parrybonus", a?.m_shared.m_timedBlockBonus ?? 1f, b?.m_shared.m_timedBlockBonus ?? 1f);
	}

	/// <summary>Blockwert bzw. Blockkraft: EpicLoot rechnet sie nur für den aktuellen Blocker mit Magie, für andere holen wir es nach.</summary>
	private static float BlockOf(ItemData? item, bool force)
	{
		if (item == null)
		{
			return 0f;
		}
		float value = force ? item.GetDeflectionForce() : item.GetBaseBlockPower();
		if (viewer != null && viewer.GetCurrentBlocker() != item)
		{
			value = EpicLootLink.Block(viewer, item, value, force);
		}
		return value;
	}

	/// <summary>
	/// Ausrüstungs-Modifikatoren mit Valheims Liste (Player.s_equipmentModifierSources/-Tooltips): Tempo, Ausdauer für Basis-Items,
	/// Hitzeschutz, Ausdauer für Springen/Angriff/Blocken/Ausweichen/Schwimmen/Schleichen/Rennen, max. Adrenalin. Ausdauer-Verbrauch:
	/// weniger ist besser. Dazu die Eitr-Regeneration.
	/// </summary>
	private static void Modifiers(StringBuilder sb, Loadout before, Loadout after)
	{
		string[] sources = Player.s_equipmentModifierSources;
		string[] names = Player.s_equipmentModifierTooltips;
		modifierFields ??= Array.ConvertAll(sources, name => typeof(ItemData.SharedData).GetField(name, BindingFlags.Instance | BindingFlags.Public));
		for (int i = 0; i < modifierFields.Length && i < names.Length; i++)
		{
			FieldInfo? field = modifierFields[i];
			if (field == null || field.FieldType != typeof(float))
			{
				continue;
			}
			float a = Sum(before, field, WornSlots);
			float b = Sum(after, field, WornSlots);
			bool higherIsBetter = !field.Name.Contains("Stamina");
			if (field.Name == "m_maxAdrenaline")
			{
				Number(sb, names[i], a, b, higherIsBetter, neutral: true); // mehr Adrenalin nötig, bis der Effekt kommt: Abwägung
			}
			else
			{
				Percent(sb, names[i], a, b, higherIsBetter);
			}
		}
		if (EitrField != null)
		{
			Percent(sb, "$item_eitrregen_modifier", Sum(before, EitrField, EitrSlots), Sum(after, EitrField, EitrSlots), true);
		}
	}

	private static float Sum(Loadout loadout, FieldInfo field, int[] slots)
	{
		float sum = 0f;
		foreach (int slot in slots)
		{
			if (loadout.Slots[slot] is ItemData worn && field.GetValue(worn.m_shared) is float value)
			{
				sum += value;
			}
		}
		return sum;
	}

	/// <summary>Setboni wie Humanoid.HaveSetEffect/GetSetCount: wird einer aktiv, entfällt er, oder ändert sich die Zahl der Teile?</summary>
	private static void Sets(StringBuilder sb, Loadout before, Loadout after)
	{
		List<string> done = new();
		foreach (Loadout loadout in new[] { after, before })
		{
			foreach (int slot in WornSlots)
			{
				if (loadout.Slots[slot] is not ItemData worn || worn.m_shared.m_setStatusEffect == null || worn.m_shared.m_setSize <= 1
					|| string.IsNullOrEmpty(worn.m_shared.m_setName) || done.Contains(worn.m_shared.m_setName))
				{
					continue;
				}
				string set = worn.m_shared.m_setName;
				done.Add(set);
				int size = worn.m_shared.m_setSize;
				int was = Count(before, set);
				int will = Count(after, set);
				string name = EffectName(worn.m_shared.m_setStatusEffect);
				if (was == will)
				{
					continue;
				}
				if (was >= size && will < size)
				{
					Line(sb, Bad, GcLoc.Get("gccmp_set_lost", name));
				}
				else if (was < size && will >= size)
				{
					Line(sb, Good, GcLoc.Get("gccmp_set_gained", name));
				}
				else if (will < size)
				{
					Line(sb, will > was ? Good : Bad, GcLoc.Get("gccmp_set_parts", name, was, will, size));
				}
			}
		}
	}

	private static int Count(Loadout loadout, string set)
	{
		int count = 0;
		foreach (int slot in WornSlots)
		{
			if (loadout.Slots[slot] is ItemData worn && worn.m_shared.m_setName == set)
			{
				count++;
			}
		}
		return count;
	}

	/// <summary>Effekte beim Tragen (m_equipStatusEffect) und bei vollem Adrenalin (m_fullAdrenalineSE), die neu dazukommen oder wegfallen.</summary>
	private static void Effects(StringBuilder sb, Loadout before, Loadout after)
	{
		Dictionary<string, StatusEffect> was = EffectsOf(before);
		Dictionary<string, StatusEffect> will = EffectsOf(after);
		foreach (KeyValuePair<string, StatusEffect> effect in was)
		{
			if (!will.ContainsKey(effect.Key))
			{
				Line(sb, Bad, GcLoc.Get("gccmp_effect_lost", EffectName(effect.Value)));
			}
		}
		foreach (KeyValuePair<string, StatusEffect> effect in will)
		{
			if (!was.ContainsKey(effect.Key))
			{
				Line(sb, Good, GcLoc.Get("gccmp_effect_gained", EffectName(effect.Value)));
			}
		}
	}

	private static Dictionary<string, StatusEffect> EffectsOf(Loadout loadout)
	{
		Dictionary<string, StatusEffect> effects = new();
		foreach (int slot in WornSlots)
		{
			if (loadout.Slots[slot] is not ItemData worn)
			{
				continue;
			}
			foreach (StatusEffect effect in new[] { worn.m_shared.m_equipStatusEffect, worn.m_shared.m_fullAdrenalineSE })
			{
				if (effect != null)
				{
					effects[effect.name] = effect;
				}
			}
		}
		return effects;
	}

	private static string EffectName(StatusEffect effect) => string.IsNullOrEmpty(effect.m_name) ? effect.name : effect.m_name;

	/// <summary>Zahl ändert sich: „Rüstung: +6“. Rückgabe: Zeile geschrieben.</summary>
	private static bool Number(StringBuilder sb, string label, float before, float after, bool higherIsBetter, bool neutral = false)
	{
		float delta = after - before;
		if (Mathf.Abs(delta) < 0.05f)
		{
			return false;
		}
		Line(sb, neutral ? Neutral : (delta > 0f) == higherIsBetter ? Good : Bad, $"{label}: {Signed(delta)}", labelPlain: true);
		return true;
	}

	/// <summary>Anteil (0,05 = 5 %), Anzeige wie Valheims Tooltip („+5%“).</summary>
	private static void Percent(StringBuilder sb, string label, float before, float after, bool higherIsBetter)
	{
		float delta = (after - before) * 100f;
		if (Mathf.Abs(delta) < 0.5f)
		{
			return;
		}
		Line(sb, (delta > 0f) == higherIsBetter ? Good : Bad, $"{label}: {Signed(delta)}%", labelPlain: true);
	}

	/// <summary>Faktor wie Hinterhalt oder Parade („3x » 2x“), mehr ist besser.</summary>
	private static void Multiplier(StringBuilder sb, string label, float before, float after)
	{
		if (Mathf.Abs(after - before) < 0.05f)
		{
			return;
		}
		Line(sb, after > before ? Good : Bad, $"{label}: {before:0.##}x » {after:0.##}x", labelPlain: true);
	}

	private static string Signed(float value) => (value > 0f ? "+" : "-") + Mathf.Abs(value).ToString("0.#");

	/// <summary>Eine Zeile in Gewinn-/Verlustfarbe; mit labelPlain nur der Teil nach dem Doppelpunkt farbig, wie Valheims Werte.</summary>
	private static void Line(StringBuilder sb, string color, string text, bool labelPlain = false)
	{
		sb.Append('\n');
		int colon = labelPlain ? text.LastIndexOf(": ", StringComparison.Ordinal) : -1;
		if (colon > 0)
		{
			sb.Append(text, 0, colon + 2).Append("<color=").Append(color).Append('>').Append(text, colon + 2, text.Length - colon - 2).Append("</color>");
		}
		else
		{
			sb.Append("<color=").Append(color).Append('>').Append(text).Append("</color>");
		}
	}
}

/// <summary>Weiche Anbindung an EpicLoot (nur per Reflexion, die Mod läuft auch ohne EpicLoot).</summary>
internal static class EpicLootLink
{
	private const string Guid = "randyknapp.mods.epicloot";
	private static Assembly? assembly;
	private static bool searched;
	private static MethodInfo? decorate;
	private static FieldInfo? comparison;
	private static MethodInfo? magicDamage;
	private static MethodInfo? coinBonus;
	private static MethodInfo? shardDamage;
	private static MethodInfo? blockPower;
	private static MethodInfo? blockForce;
	private static GameObject? hiddenComparison;
	private static bool membersSearched;

	private static void Search()
	{
		if (!searched)
		{
			searched = true;
			if (Chainloader.PluginInfos.TryGetValue(Guid, out PluginInfo info) && info.Instance != null)
			{
				assembly = info.Instance.GetType().Assembly;
			}
		}
		if (!membersSearched && assembly != null)
		{
			membersSearched = true;
			decorate = assembly.GetType("EpicLoot.ItemDataExtensions")?.GetMethod("GetDecoratedName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemData), typeof(string) }, null);
			comparison = assembly.GetType("EpicLoot.PatchOnHoverFix")?.GetField("ComparisonTT", BindingFlags.Public | BindingFlags.Static);
			Type? damage = assembly.GetType("EpicLoot.MagicItemEffects.ModifyDamage");
			magicDamage = damage?.GetMethod("ApplyMagicDamageModifiers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
			// seit EpicLoot 0.15.0 mit 4. Parameter: Bonus der Münz-Waffe (CoinHoarder), wie beim angelegten Item
			coinBonus = assembly.GetType("EpicLoot.MagicItemEffects.CoinHoarder")?.GetMethod("GetDamageBonus", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player) }, null);
			shardDamage = damage?.GetMethod("ApplyShardWeaponModifiers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
			Type? block = assembly.GetType("EpicLoot.MagicItemEffects.ModifyBlock");
			blockPower = block?.GetMethod("GetBlockPowerValue", BindingFlags.Public | BindingFlags.Static);
			blockForce = block?.GetMethod("GetDeflectionForceValue", BindingFlags.Public | BindingFlags.Static);
		}
	}

	/// <summary>
	/// EpicLoots Schadensrechnung (ModifyDamage.ApplyMagicDamageModifiers, mit Stellvertreter-Logik für das ersetzte Item, und
	/// die Splitter-Effekte) für ein nicht angelegtes Item, als wäre es angelegt. EpicLoots GetDamage-Patch rechnet nur angelegte.
	/// </summary>
	internal static void AddDamage(Player player, ItemData item, ref HitData.DamageTypes damage)
	{
		Search();
		if (magicDamage == null)
		{
			return;
		}
		try
		{
			object?[] args = magicDamage.GetParameters().Length == 4
				? new object?[] { player, item, damage, coinBonus?.Invoke(null, new object[] { player }) ?? 0f }
				: new object?[] { player, item, damage };
			magicDamage.Invoke(null, args);
			damage = (HitData.DamageTypes)args[2]!;
			if (shardDamage != null)
			{
				object?[] shard = { item, damage };
				shardDamage.Invoke(null, shard);
				damage = (HitData.DamageTypes)shard[1]!;
			}
		}
		catch (Exception e)
		{
			magicDamage = null; // andere EpicLoot-Fassung: ab jetzt ohne
			Plugin.Log.LogWarning($"Ausrüstungs-Vergleich: EpicLoots Schadensrechnung passt nicht zu dieser EpicLoot-Fassung, Vergleich ohne magischen Schaden ({e.GetBaseException().Message}).");
		}
	}

	/// <summary>EpicLoots Blockrechnung (ModifyBlock.GetBlockPowerValue/GetDeflectionForceValue, ohne Parade) für ein Item, das gerade nicht blockt.</summary>
	internal static float Block(Player player, ItemData item, float value, bool force)
	{
		Search();
		MethodInfo? method = force ? blockForce : blockPower;
		if (method == null)
		{
			return value;
		}
		try
		{
			return method.Invoke(null, new object?[] { player, item, value, false }) is float result ? result : value;
		}
		catch (Exception)
		{
			blockPower = blockForce = null;
			return value;
		}
	}

	/// <summary>Name wie in EpicLoots Tooltip (Seltenheitsfarbe, magischer Name), sonst Valheims Name.</summary>
	internal static string Name(ItemData item)
	{
		Search();
		if (decorate != null)
		{
			try
			{
				if (decorate.Invoke(null, new object?[] { item, null }) is string name && name.Length > 0)
				{
					return name;
				}
			}
			catch (Exception)
			{
				decorate = null; // andere EpicLoot-Fassung: ab jetzt Valheims Name
			}
		}
		return item.m_shared.m_name;
	}

	/// <summary>EpicLoots eigenes Feld „Aktuell ausgerüstet“ (bei gehaltenem Links-Strg) zeigt dasselbe ohne Unterschiede: solange unseres steht, aus.</summary>
	internal static void HideComparison()
	{
		Search();
		if (comparison?.GetValue(null) is GameObject other && other != null && other.activeSelf)
		{
			other.SetActive(false);
			hiddenComparison = other;
		}
	}

	/// <summary>Unser Feld ist weg: EpicLoots Feld wieder zeigen, falls wir es ausgeblendet hatten und es noch steht.</summary>
	internal static void RestoreComparison()
	{
		if (hiddenComparison != null)
		{
			hiddenComparison.SetActive(true);
		}
		hiddenComparison = null;
	}
}
