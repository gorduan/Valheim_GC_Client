using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Client.Compass;

internal enum NameMode
{
	Always,
	Near,
	Never,
}

internal enum IconStyle
{
	MapIcon,
	Dot,
}

/// <summary>
/// Kompass oben am Bildschirmrand: Himmelsrichtungen und Kartenmarkierungen in Blickrichtung (Kamera).
///
/// Alt + Linksklick auf eine Markierung der großen Karte macht sie zum Ziel: Sie wird auf der Karte golden und steht
/// im Kompass immer hervorgehoben, auch außerhalb der Reichweite und, liegt sie hinter dir, am Rand mit « oder ».
/// Alt + Linksklick auf das Ziel oder auf eine leere Stelle hebt es auf. Pings von Spielern (mittlere Maustaste auf
/// der Karte, Valheim zeigt sie etwa 5 Sekunden) erscheinen ebenso hervorgehoben in Blau. Ziel und Pings liegen über
/// allen anderen Markierungen, ihre Namen auf dunklem Grund; überdecken sie sich, rückt ein Name in eine Zeile unter
/// der Leiste. Andere Markierungen werden mit der Entfernung kleiner und blasser und weichen mit ihren Namen den
/// hervorgehobenen. Boss- und Raid-Leiste oben in der Mitte schieben den Kompass nach unten, statt ihn zu verdecken.
///
/// Gezeigt wird, was auch die Karte zeigt, auch Händler und andere Orte mit eigenem Symbol: Typen, die im
/// Kartenfilter aus sind, fehlen; geteilte Markierungen (Kartentisch) nur, solange die Karte sie einblendet. Das Ziel
/// wird am Charakter gespeichert (Player.m_customData), getrennt nach Welt. Ohne Karte (Welt-Einstellung „keine
/// Karte") zeigt der Kompass nur die Himmelsrichtungen. Er folgt dem HUD (Strg+F3, Zwischensequenzen) und
/// verschwindet, solange die große Karte oder die große Dungeon-Karte offen ist.
/// </summary>
internal static class Plugin
{
	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<KeyCode> FocusModifier = null!;
	internal static ConfigEntry<int> WidthPercent = null!;
	internal static ConfigEntry<int> FieldOfView = null!;
	internal static ConfigEntry<int> OffsetY = null!;
	internal static ConfigEntry<float> Scale = null!;
	internal static ConfigEntry<int> MaxDistance = null!;
	internal static ConfigEntry<NameMode> OtherNames = null!;
	internal static ConfigEntry<int> NameDistance = null!;
	internal static ConfigEntry<bool> ShowDistance = null!;
	internal static ConfigEntry<bool> ShowChecked = null!;
	internal static ConfigEntry<bool> ShowPlayers = null!;
	internal static ConfigEntry<IconStyle> Icons = null!;
	internal static bool On => Enabled.Value && ClientPlugin.Allowed;

	internal static CompassView? View;
	private static string lastError = "";

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		const string section = "11 - Compass";
		Enabled = Gc.Bind(Config, section, "Enabled", true, "Kompass oben am Bildschirm (auch im Menü GC Option).");
		FocusModifier = Gc.Bind(Config, section, "FocusModifier", KeyCode.LeftAlt, "Diese Taste halten und auf der großen Karte eine Markierung anklicken: wird zum Ziel (golden). Nochmal klicken oder auf eine leere Stelle klicken hebt das Ziel auf.");
		WidthPercent = Gc.Bind(Config, section, "WidthPercent", 50, "Breite des Kompasses in Prozent der Bildschirmbreite.", new AcceptableValueRange<int>(20, 100));
		FieldOfView = Gc.Bind(Config, section, "FieldOfView", 180, "So viel Grad zeigt die Leiste (180 = von West bis Ost, wenn du nach Norden schaust).", new AcceptableValueRange<int>(90, 360));
		OffsetY = Gc.Bind(Config, section, "OffsetY", 10, "Abstand vom oberen Bildschirmrand in Pixeln. Boss- und Raid-Leiste schieben den Kompass zusätzlich nach unten.", new AcceptableValueRange<int>(0, 600));
		Scale = Gc.Bind(Config, section, "Scale", 1f, "Größe von Symbolen und Schrift.", new AcceptableValueRange<float>(0.5f, 2f));
		MaxDistance = Gc.Bind(Config, section, "MaxDistance", 2000, "Andere Markierungen bis zu dieser Entfernung in Metern zeigen. Ziel und Pings immer.", new AcceptableValueRange<int>(250, 20000));
		OtherNames = Gc.Bind(Config, section, "OtherNames", NameMode.Near, "Namen anderer Markierungen: Always, Near (bis NameDistance) oder Never. Ziel und Pings zeigen ihren Namen immer.");
		NameDistance = Gc.Bind(Config, section, "NameDistance", 300, "Bis zu dieser Entfernung in Metern zeigen andere Markierungen ihren Namen (OtherNames = Near).", new AcceptableValueRange<int>(25, 5000));
		ShowDistance = Gc.Bind(Config, section, "ShowDistance", true, "Entfernung bei Ziel und Pings anzeigen.");
		ShowChecked = Gc.Bind(Config, section, "ShowChecked", false, "Auf der Karte abgehakte (durchgestrichene) Markierungen zeigen. Das Ziel wird immer gezeigt.");
		ShowPlayers = Gc.Bind(Config, section, "ShowPlayers", true, "Mitspieler zeigen, die auf der Karte sichtbar sind.");
		Icons = Gc.Bind(Config, section, "IconStyle", IconStyle.MapIcon, "Symbole: MapIcon (Kartensymbol) oder Dot (Punkt).");
		Enabled.SettingChanged += (_, _) => Focus.RefreshMap(); // Gold auf der Karte an- oder ausschalten
	}

	/// <summary>Ziel-Taste gehalten (für Klick und Doppelklick auf der großen Karte)?</summary>
	internal static bool FocusKeyHeld => On && !Game.m_noMap && FocusModifier.Value != KeyCode.None && Input.GetKey(FocusModifier.Value);

	/// <summary>Nach der Kamera aufrufen (GameCamera.LateUpdate), damit die Leiste nicht hinter der Drehung herläuft.</summary>
	internal static void Tick(GameCamera camera)
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		try
		{
			Player? player = Player.m_localPlayer;
			Focus.Tick(player);
			bool show = On && player != null
				&& Hud.instance != null && Hud.instance.m_rootObject != null && Hud.instance.IsVisible()
				&& !player.InIntro() && !GameCamera.InFreeFly()
				&& !(Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)
				&& !GcHooks.IsDungeonMapLargeOpen(); // GC_DungeonKarte, wenn installiert
			if (!show)
			{
				View?.Hide();
				return;
			}
			View ??= new CompassView();
			View.Draw(player!, camera);
		}
		catch (Exception e)
		{
			View?.Hide(); // halb gezeichnet wären die Teile in Standardgröße zu sehen (weißes Kästchen)
			string key = e.GetType().Name + "|" + e.TargetSite?.Name + "|" + e.Message; // Art und Stelle, nicht nur der Text
			if (key != lastError)
			{
				lastError = key; // nicht jedes Frame dieselbe Meldung
				Log.LogError($"Kompass: {e}");
			}
		}
	}
}

/// <summary>Leiste aktualisieren, sobald die Kamera für dieses Frame steht.</summary>
[HarmonyPatch(typeof(GameCamera), "LateUpdate")]
internal static class CameraPatch
{
	private static void Postfix(GameCamera __instance) => Plugin.Tick(__instance);
}

/// <summary>Alt + Linksklick auf der großen Karte: Markierung zum Ziel machen oder das Ziel aufheben (statt abhaken).</summary>
[HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
internal static class FocusClickPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Minimap __instance)
	{
		if (!Plugin.FocusKeyHeld)
		{
			return true;
		}
		__instance.HidePinTextInput(false); // wie beim normalen Klick ein offenes Namensfeld schließen
		Minimap.PinData? pin = Focus.PinAtCursor(__instance);
		if (pin != null)
		{
			Focus.Toggle(pin);
		}
		else if (Focus.Active)
		{
			Focus.Remove(); // Klick auf eine leere Stelle hebt das Ziel auf (auch einen Mitspieler, der offline ist)
		}
		else
		{
			Gc.TopLeft(GcLoc.Get("gcc_no_pin"));
		}
		return false; // kein Abhaken, solange die Taste gehalten wird
	}
}

/// <summary>Mit gehaltener Ziel-Taste öffnet ein schneller Doppelklick kein Feld für eine neue Markierung.</summary>
[HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapDblClick))]
internal static class FocusDoubleClickPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix() => !Plugin.FocusKeyHeld;
}

/// <summary>Das Ziel auf der Karte golden färben (Valheim setzt die Farben bei jedem Neuzeichnen der Markierungen).</summary>
[HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdatePins))]
internal static class MapColorPatch
{
	private static void Postfix()
	{
		Minimap.PinData? pin = Focus.Pin;
		if (!Plugin.On || pin == null)
		{
			return;
		}
		if (pin.m_iconElement != null)
		{
			pin.m_iconElement.color = CompassView.FocusColor;
		}
		if (pin.m_NamePinData != null && pin.m_NamePinData.PinNameText != null)
		{
			pin.m_NamePinData.PinNameText.color = CompassView.FocusColor;
		}
	}
}

/// <summary>
/// Das Ziel: Typ, Name und Position der Markierung (am Charakter gespeichert, je Welt) und die passende Markierung
/// der Karte. Valheim legt Markierungen manchmal neu an oder vergibt sie neu (Mitspieler-Markierungen werden der
/// Reihe nach weiterverwendet), deshalb wird jedes Frame geprüft, ob Typ und Name noch stimmen, und das Ziel sonst
/// über Typ, Name und Position wiedergefunden. Mitspieler über den Namen: Ist er offline, wartet das Ziel auf ihn.
/// </summary>
internal static class Focus
{
	private const string DataPrefix = "gc_compass_focus_";

	internal static Minimap.PinData? Pin;
	private static bool active;
	private static Minimap.PinType type;
	private static string name = "";
	private static Vector3 position;
	private static bool everFound;
	private static float searchUntil;
	private static float nextSearch;
	private static Game? game;
	private static Player? owner;
	private static bool loaded;

	internal static bool Active => active;

	internal static void Tick(Player? player)
	{
		if (!ReferenceEquals(Game.instance, game))
		{
			game = Game.instance; // neue Sitzung (anderer Charakter oder andere Welt)
			Pin = null;
			active = false;
			owner = null;
			loaded = false;
		}
		Minimap? map = Minimap.instance;
		if (player == null || map == null || ZNet.instance == null)
		{
			return;
		}
		if (!ReferenceEquals(player, owner))
		{
			owner = player;
			if (!loaded)
			{
				loaded = true;
				Load(player);
			}
			else
			{
				Save(); // nach dem Tod gibt es ein neues Spieler-Objekt: Ziel dort wieder eintragen
			}
		}
		if (!active || !Plugin.On)
		{
			return;
		}
		if (Pin != null)
		{
			if (!map.m_pins.Contains(Pin) || Pin.m_type != type || Pin.m_name != name)
			{
				Pin = null; // gelöscht, neu angelegt oder an einen anderen Mitspieler vergeben
			}
			else if (type != Minimap.PinType.Player && Utils.DistanceXZ(Pin.m_pos, position) > 1f)
			{
				position = Pin.m_pos; // Markierung verschoben (z. B. neues Bett)
				Save();
			}
		}
		if (Pin == null && Time.time >= nextSearch)
		{
			nextSearch = Time.time + 0.5f;
			Pin = Find(map);
			if (Pin != null)
			{
				everFound = true;
				map.m_pinUpdateRequired = true;
			}
			else if (type != Minimap.PinType.Player && (everFound || Time.time > searchUntil))
			{
				Clear(); // die Markierung gibt es nicht mehr
			}
		}
	}

	internal static Minimap.PinData? PinAtCursor(Minimap map)
	{
		Vector3 point = map.ScreenToWorldPoint(ZInput.pointerPosition);
		float best = map.PinInteractRadius;
		Minimap.PinData? found = null;
		foreach (Minimap.PinData pin in map.m_pins)
		{
			if (!Selectable(pin) || pin.m_uiElement == null || !pin.m_uiElement.gameObject.activeInHierarchy)
			{
				continue; // nur, was auf der Karte gerade zu sehen ist
			}
			float distance = Utils.DistanceXZ(point, pin.m_pos);
			if (distance < best)
			{
				best = distance;
				found = pin;
			}
		}
		return found;
	}

	/// <summary>Alle Markierungen mit Symbol, auch Händler und Orte (Typ None mit eigenem Symbol), aber keine Pings, Rufe oder Gebiete.</summary>
	internal static bool Selectable(Minimap.PinData pin)
	{
		switch (pin.m_type)
		{
			case Minimap.PinType.Ping:
			case Minimap.PinType.Shout:
			case Minimap.PinType.EventArea:
				return false;
			case Minimap.PinType.None:
				return pin.m_icon != null;
			default:
				return true;
		}
	}

	internal static void Toggle(Minimap.PinData pin)
	{
		if (active && Pin == pin)
		{
			Remove();
			return;
		}
		Pin = pin;
		active = true;
		everFound = true;
		type = pin.m_type;
		name = pin.m_name;
		position = pin.m_pos;
		Save();
		RefreshMap();
		Gc.TopLeft(GcLoc.Get("gcc_focus_set", CompassView.PinName(pin, true)));
	}

	internal static void Remove()
	{
		Clear();
		Gc.TopLeft(GcLoc.Get("gcc_focus_clear"));
	}

	internal static void RefreshMap()
	{
		if (Minimap.instance != null)
		{
			Minimap.instance.m_pinUpdateRequired = true; // Farben neu setzen (altes Ziel wieder weiß)
		}
	}

	private static void Clear()
	{
		active = false;
		Pin = null;
		Save();
		RefreshMap();
	}

	private static Minimap.PinData? Find(Minimap map)
	{
		foreach (Minimap.PinData pin in map.m_pins)
		{
			if (pin.m_type == type && pin.m_name == name && (type == Minimap.PinType.Player || Utils.DistanceXZ(pin.m_pos, position) < 1f))
			{
				return pin;
			}
		}
		return null;
	}

	private static string Key() => DataPrefix + ZNet.instance.GetWorldUID().ToString(CultureInfo.InvariantCulture);

	private static void Load(Player player)
	{
		active = false;
		Pin = null;
		everFound = false;
		searchUntil = Time.time + 20f; // Kartendaten und Orte kommen beim Betreten der Welt etwas später
		if (!player.m_customData.TryGetValue(Key(), out string data))
		{
			return;
		}
		string[] parts = data.Split(new[] { ';' }, 4);
		if (parts.Length == 4
			&& int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)
			&& float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
			&& float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
		{
			type = (Minimap.PinType)t;
			position = new Vector3(x, 0f, z);
			name = parts[3];
			active = true;
		}
	}

	private static void Save()
	{
		if (owner == null || ZNet.instance == null)
		{
			return;
		}
		if (!active)
		{
			owner.m_customData.Remove(Key());
			return;
		}
		owner.m_customData[Key()] = string.Join(";",
			((int)type).ToString(CultureInfo.InvariantCulture),
			position.x.ToString("R", CultureInfo.InvariantCulture),
			position.z.ToString("R", CultureInfo.InvariantCulture),
			name);
	}
}

/// <summary>Die Leiste selbst: Linie mit Strichen und Himmelsrichtungen, darüber Symbole und Namen.</summary>
internal sealed class CompassView
{
	internal static readonly Color FocusColor = new(1f, 0.8f, 0.2f, 1f);
	private static readonly Color PingColor = new(0.45f, 0.62f, 1f, 1f);
	private static readonly Color NorthColor = new(1f, 0.6f, 0.35f, 1f);
	private static readonly Color MinorColor = new(0.85f, 0.85f, 0.85f, 1f);
	private static readonly Color PlateColor = new(0f, 0f, 0f, 0.6f);
	private const float NearDistance = 50f;
	private const int MaxNormals = 80;

	// Zeilen von oben (mal Größe): Namen, Symbole, Linie, Himmelsrichtungen, zwei Ausweichzeilen für Namen
	private const float RowLabel = -12f;
	private const float RowIcon = -37f;
	private const float RowLine = -55f;
	private const float RowLetters = -69f;
	private const float RowLabel1 = -92f;
	private const float RowLabel2 = -115f;

	private sealed class Marker
	{
		public Image Icon = null!;
		public Image Plate = null!;
		public Text Label = null!;
	}

	private struct Item
	{
		public Minimap.PinData? Pin;
		public Sprite? Icon;
		public string Name;
		public Color Color;
		public float Distance;
		public float Rel;
		public bool Focus;
		public bool Faded;
		public float X;
		public float Fade;
		public Marker? Marker;
	}

	private sealed class FarFirstComparer : IComparer<Item>
	{
		public int Compare(Item a, Item b) => b.Distance.CompareTo(a.Distance);
	}

	/// <summary>Pings (weit zuerst), das Ziel zuletzt: liegt obenauf.</summary>
	private sealed class HighlightComparer : IComparer<Item>
	{
		public int Compare(Item a, Item b) => a.Focus != b.Focus ? (a.Focus ? 1 : -1) : b.Distance.CompareTo(a.Distance);
	}

	private static readonly FarFirstComparer FarFirst = new();
	private static readonly HighlightComparer HighlightOrder = new();

	private GameObject? root;
	private RectTransform rootRect = null!;
	private RectTransform normalLayer = null!;
	private RectTransform highlightLayer = null!;
	private Image band = null!;
	private Image line = null!;
	private Image center = null!;
	private Image leftEnd = null!;
	private Image rightEnd = null!;
	private readonly Text[] letters = new Text[8];
	private readonly Image[] ticks = new Image[16];
	private readonly List<Marker> normalPool = new();
	private readonly List<Marker> highlightPool = new();
	private readonly List<Item> normals = new();
	private readonly List<Item> highlights = new();
	private readonly List<Chat.WorldTextInstance> pings = new();
	private readonly List<Vector2>[] taken = { new(), new(), new() };
	private readonly List<RectTransform> blockers = new();
	private readonly Vector3[] corners = new Vector3[4];
	private Vector3 layoutKey = new(-1f, -1f, -1f);
	private float rowLabel0;
	private float rowLabel1;
	private float rowLabel2;
	private Sprite? pingSprite;
	private static bool widthWarned;

	private static readonly string[] BossParts = { "Health", "Name" };

	/// <summary>Leinenleiste von GC_Tiere: Anzeige im HUD neben der Schnellleiste und ihr Raster im Inventar (über den Namen, ohne Abhängigkeit).</summary>
	private const string LeashBarHud = "GC_LeashBar";
	private const string LeashBarGrid = "GC_LeashBarGrid";
	private RectTransform? leashBarHud;
	private RectTransform? leashBarGrid;
	private float nextLeashBarSearch;
	private static readonly Dictionary<Vector3, string> LocationNames = new();
	private static readonly Dictionary<Vector3, string> LocationScan = new();
	private static float nextLocationScan;

	public void Hide()
	{
		if (root != null && root.activeSelf)
		{
			root.SetActive(false);
		}
	}

	public void Draw(Player player, GameCamera camera)
	{
		Build();
		if (!root!.activeSelf)
		{
			root.SetActive(true);
		}
		float s = Plugin.Scale.Value;
		Layout(s);
		float half = rootRect.rect.width * 0.5f;
		if (half <= 1f)
		{
			root.SetActive(false); // nicht anordnen können: lieber nichts zeigen als Teile in Standardgröße (weißes Kästchen)
			return;
		}
		float halfFov = Mathf.Clamp(Plugin.FieldOfView.Value, 30, 360) * 0.5f;
		float heading = camera.transform.eulerAngles.y;
		rowLabel0 = RowLabel * s;
		rowLabel1 = RowLabel1 * s;
		rowLabel2 = RowLabel2 * s;
		DrawDial(heading, half, halfFov, s);
		Collect(player, heading, halfFov);
		DrawNormals(half, halfFov, s);
		DrawHighlights(half, halfFov, s);
	}

	// ------------------------------------------------------------------ Aufbau

	private void Build()
	{
		if (root != null)
		{
			return;
		}
		normalPool.Clear(); // alte Elemente sind mit der Szene verschwunden
		highlightPool.Clear();
		pingSprite = null;
		layoutKey = new Vector3(-1f, -1f, -1f);
		root = new GameObject("GC_Compass", typeof(RectTransform), typeof(CanvasGroup));
		// unter Valheims eigener HUD-Wurzel (bildschirmfüllend, verschwindet mit dem HUD). Unter Jötunns CustomGUIBack hatte
		// die Leiste im ersten Spieltest keine Breite: Sie blieb ein weißes Kästchen mit „NW“ am oberen Rand
		root.transform.SetParent(Hud.instance.m_rootObject.transform, false);
		root.transform.SetAsFirstSibling(); // hinter den anderen HUD-Teilen (Baumenü oben in der Mitte), wie vorher
		CanvasGroup group = root.GetComponent<CanvasGroup>();
		group.blocksRaycasts = false;
		group.interactable = false;
		rootRect = (RectTransform)root.transform;
		rootRect.pivot = new Vector2(0.5f, 1f);

		band = CreateImage(root.transform, "Band", Sprites.Band);
		band.preserveAspect = false; // weicher Balken über die ganze Breite
		band.color = new Color(0f, 0f, 0f, 0.35f);
		line = CreateImage(root.transform, "Line", null);
		line.color = new Color(1f, 1f, 1f, 0.85f);
		leftEnd = CreateImage(root.transform, "LeftEnd", Sprites.Triangle);
		leftEnd.rectTransform.localEulerAngles = new Vector3(0f, 0f, 180f);
		leftEnd.color = line.color;
		rightEnd = CreateImage(root.transform, "RightEnd", Sprites.Triangle);
		rightEnd.color = line.color;
		for (int i = 0; i < ticks.Length; i++)
		{
			ticks[i] = CreateImage(root.transform, "Tick", null);
		}
		center = CreateImage(root.transform, "Center", null);
		center.color = new Color(1f, 1f, 1f, 0.95f);
		string[] keys = { "n", "ne", "e", "se", "s", "sw", "w", "nw" };
		for (int i = 0; i < letters.Length; i++)
		{
			letters[i] = CreateText(root.transform, GUIManager.Instance.NorseBold);
			letters[i].text = GcLoc.Get("gcc_dir_" + keys[i]);
		}
		normalLayer = CreateLayer("Markers");
		highlightLayer = CreateLayer("Highlights"); // danach angelegt, liegt also darüber
		root.SetActive(false);
	}

	private RectTransform CreateLayer(string name)
	{
		GameObject go = new(name, typeof(RectTransform));
		go.transform.SetParent(root!.transform, false);
		RectTransform rect = (RectTransform)go.transform;
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = rect.offsetMax = Vector2.zero;
		return rect;
	}

	private static Image CreateImage(Transform parent, string name, Sprite? sprite)
	{
		GameObject go = new(name, typeof(RectTransform), typeof(Image));
		go.transform.SetParent(parent, false);
		Image image = go.GetComponent<Image>();
		image.sprite = sprite;
		image.raycastTarget = false;
		image.preserveAspect = sprite != null;
		RectTransform rect = image.rectTransform;
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		return image;
	}

	private static Text CreateText(Transform parent, Font font)
	{
		GameObject go = GUIManager.Instance.CreateText("", parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, font, 14, Color.white, true, Color.black, 300f, 30f, false);
		Text text = go.GetComponent<Text>();
		text.alignment = TextAnchor.MiddleCenter;
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		text.verticalOverflow = VerticalWrapMode.Overflow;
		text.supportRichText = true;
		text.raycastTarget = false;
		return text;
	}

	private Marker GetMarker(List<Marker> pool, RectTransform layer, int index)
	{
		while (pool.Count <= index)
		{
			Marker marker = new()
			{
				Plate = CreateImage(layer, "Plate", null),
				Label = CreateText(layer, GUIManager.Instance.AveriaSerifBold),
				Icon = CreateImage(layer, "Icon", null),
			};
			marker.Plate.color = PlateColor;
			Show(marker.Plate, false);
			Show(marker.Label, false);
			pool.Add(marker);
		}
		return pool[index];
	}

	private static void Show(Graphic graphic, bool show)
	{
		if (graphic.gameObject.activeSelf != show)
		{
			graphic.gameObject.SetActive(show);
		}
	}

	private static void HideFrom(List<Marker> pool, int start)
	{
		for (int i = start; i < pool.Count; i++)
		{
			Show(pool[i].Icon, false);
			Show(pool[i].Plate, false);
			Show(pool[i].Label, false);
		}
	}

	private static void Place(Graphic graphic, float x, float y, float width, float height)
	{
		RectTransform rect = graphic.rectTransform;
		rect.anchoredPosition = new Vector2(x, y);
		rect.sizeDelta = new Vector2(width, height);
	}

	private void Layout(float s)
	{
		Vector3 key = new(Plugin.WidthPercent.Value, s, 0f);
		if (key != layoutKey)
		{
			layoutKey = key;
			Stretch(s);
		}
		if (rootRect.rect.width <= 2f && MoveToCanvas())
		{
			Stretch(s);
		}
		float y = -BlockerOffset(Plugin.OffsetY.Value);
		if (!Mathf.Approximately(rootRect.anchoredPosition.y, y))
		{
			rootRect.anchoredPosition = new Vector2(0f, y);
		}
	}

	/// <summary>Leiste über WidthPercent der Breite des Elternbereichs, oben am Rand, 130 Einheiten hoch (mal Größe).</summary>
	private void Stretch(float s)
	{
		float w = Mathf.Clamp(Plugin.WidthPercent.Value, 10, 100) / 100f;
		rootRect.anchorMin = new Vector2(0.5f - w / 2f, 1f);
		rootRect.anchorMax = new Vector2(0.5f + w / 2f, 1f);
		rootRect.sizeDelta = new Vector2(0f, 130f * s);
	}

	/// <summary>
	/// Notlösung, falls der Elternbereich keine Breite hat: unter die oberste Zeichenfläche hängen (immer
	/// bildschirmgroß, hinter allem anderen), dort wieder über die Breite verankert, so dass auch Auflösung und
	/// GUI-Größe stimmen. Meldet sich einmal im Log mit dem Pfad, damit sich die Ursache finden lässt. Hat auch die
	/// Zeichenfläche keine Breite, bleibt die Leiste unsichtbar (Draw).
	/// </summary>
	private bool MoveToCanvas()
	{
		RectTransform parent = (RectTransform)rootRect.parent;
		Canvas? canvas = parent.GetComponentInParent<Canvas>();
		RectTransform? top = canvas != null ? (RectTransform)canvas.rootCanvas.transform : null;
		if (!widthWarned)
		{
			widthWarned = true;
			string path = parent.name;
			for (Transform t = parent.parent; t != null; t = t.parent)
			{
				path = t.name + "/" + path;
			}
			Plugin.Log.LogWarning($"Kompass: Elternbereich {path} hat keine Breite ({parent.rect}); hänge die Leiste unter die Zeichenfläche {(top != null ? top.name + " " + top.rect : "-")}.");
		}
		if (top == null || top == parent || top.rect.width <= 2f)
		{
			return false;
		}
		rootRect.SetParent(top, false);
		rootRect.SetAsFirstSibling();
		return true;
	}

	// ------------------------------------------------------------------ Boss- und Raid-Leiste

	private static Camera? CanvasCamera(Transform transform)
	{
		Canvas? canvas = transform.GetComponentInParent<Canvas>();
		canvas = canvas != null ? canvas.rootCanvas : null;
		return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
	}

	/// <summary>
	/// Abstand vom oberen Rand (Einheiten des Kompasses), damit Boss- und Raid-Leiste und die Leinenleiste den Kompass nicht
	/// verdecken (Gorduan, 25.10.2026: die Leinenleiste bleibt neben der Schnellleiste, der Kompass geht darunter).
	/// </summary>
	private float BlockerOffset(float offset)
	{
		blockers.Clear();
		Hud? hud = Hud.instance;
		if (hud != null && hud.m_eventBar != null && hud.m_eventBar.activeInHierarchy)
		{
			blockers.Add((RectTransform)hud.m_eventBar.transform);
		}
		AddLeashBars(hud);
		EnemyHud? enemies = EnemyHud.instance;
		if (enemies != null)
		{
			foreach (EnemyHud.HudData data in enemies.m_huds.Values)
			{
				if (data.m_character == null || !data.m_character.IsBoss() || data.m_gui == null || !data.m_gui.activeInHierarchy)
				{
					continue;
				}
				foreach (string part in BossParts)
				{
					if (data.m_gui.transform.Find(part) is RectTransform rect && rect.gameObject.activeInHierarchy)
					{
						blockers.Add(rect);
					}
				}
			}
		}
		if (blockers.Count == 0)
		{
			return offset;
		}
		RectTransform parent = (RectTransform)rootRect.parent;
		Camera? ownCamera = CanvasCamera(parent);
		rootRect.GetWorldCorners(corners);
		float left = RectTransformUtility.WorldToScreenPoint(ownCamera, corners[0]).x;
		float right = RectTransformUtility.WorldToScreenPoint(ownCamera, corners[2]).x;
		float result = offset;
		foreach (RectTransform blocker in blockers)
		{
			blocker.GetWorldCorners(corners);
			Camera? camera = CanvasCamera(blocker);
			Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
			Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
			if (max.x < left || min.x > right)
			{
				continue; // nicht über der Leiste
			}
			if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, min, ownCamera, out Vector2 local))
			{
				continue;
			}
			float fromTop = parent.rect.yMax - local.y; // Unterkante, vom oberen Bildschirmrand gemessen
			if (fromTop > 0f && fromTop < parent.rect.height * 0.5f)
			{
				result = Mathf.Max(result, fromTop + 6f);
			}
		}
		return result;
	}

	/// <summary>
	/// Leinenleiste als Hindernis, solange sie zu sehen ist: im Spiel die Anzeige neben der Schnellleiste (gleicher Elternteil
	/// wie Valheims Schnellleiste), im Inventar ihr Raster (unter der Inventar-Wurzel). Einmal je Sekunde gesucht.
	/// </summary>
	private void AddLeashBars(Hud? hud)
	{
		if (Time.time >= nextLeashBarSearch)
		{
			nextLeashBarSearch = Time.time + 1f;
			if (leashBarHud == null && hud != null && hud.GetComponentInChildren<HotkeyBar>(true) is HotkeyBar hotkeys && hotkeys.transform.parent != null)
			{
				leashBarHud = hotkeys.transform.parent.Find(LeashBarHud) as RectTransform;
			}
			if (leashBarGrid == null && InventoryGui.instance != null && InventoryGui.instance.m_inventoryRoot != null)
			{
				leashBarGrid = InventoryGui.instance.m_inventoryRoot.Find(LeashBarGrid) as RectTransform;
			}
		}
		if (leashBarHud != null && leashBarHud.gameObject.activeInHierarchy)
		{
			blockers.Add(leashBarHud);
		}
		if (leashBarGrid != null && leashBarGrid.gameObject.activeInHierarchy && InventoryGui.IsVisible())
		{
			blockers.Add(leashBarGrid);
		}
	}

	// ------------------------------------------------------------------ Zeichnen

	private static float Smooth(float edge0, float edge1, float x)
	{
		float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
		return t * t * (3f - 2f * t);
	}

	/// <summary>Zum Rand hin ausblenden (Teil des Sichtbereichs 0..1).</summary>
	private static float EdgeFade(float part) => 1f - Smooth(0.8f, 1f, part);

	private void DrawDial(float heading, float half, float halfFov, float s)
	{
		Place(band, 0f, (RowIcon + RowLetters) / 2f * s, half * 2f + 40f * s, 58f * s);
		Place(line, 0f, RowLine * s, half * 2f, Mathf.Max(1f, 2f * s));
		Place(leftEnd, -half - 5f * s, RowLine * s, 10f * s, 10f * s);
		Place(rightEnd, half + 5f * s, RowLine * s, 10f * s, 10f * s);
		Place(center, 0f, RowLine * s, Mathf.Max(1f, 2f * s), 16f * s);
		for (int i = 0; i < letters.Length; i++)
		{
			Text letter = letters[i];
			float rel = Mathf.DeltaAngle(heading, i * 45f);
			bool visible = Mathf.Abs(rel) <= halfFov;
			Show(letter, visible);
			if (!visible)
			{
				continue;
			}
			bool major = i % 2 == 0;
			letter.fontSize = Mathf.Max(6, Mathf.RoundToInt((major ? 20f : 14f) * s));
			Color color = i == 0 ? NorthColor : major ? Color.white : MinorColor;
			color.a = EdgeFade(Mathf.Abs(rel) / halfFov);
			letter.color = color;
			Place(letter, rel / halfFov * half, RowLetters * s, 60f * s, 26f * s);
		}
		int tick = 0;
		for (int step = 0; step < 24; step++)
		{
			if (step % 3 == 0)
			{
				continue; // alle 45° steht ein Buchstabe
			}
			Image image = ticks[tick++];
			float rel = Mathf.DeltaAngle(heading, step * 15f);
			bool visible = Mathf.Abs(rel) <= halfFov;
			Show(image, visible);
			if (!visible)
			{
				continue;
			}
			image.color = new Color(1f, 1f, 1f, 0.7f * EdgeFade(Mathf.Abs(rel) / halfFov));
			Place(image, rel / halfFov * half, (RowLine - 4f) * s, Mathf.Max(1f, 2f * s), 8f * s);
		}
	}

	private static float Rel(Vector3 from, Vector3 to, float heading)
	{
		float dx = to.x - from.x;
		float dz = to.z - from.z;
		if (dx * dx + dz * dz < 0.25f)
		{
			return 0f; // direkt am Ziel
		}
		return Mathf.DeltaAngle(heading, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg); // 0° = Norden (+z), 90° = Osten (+x)
	}

	/// <summary>Anzeigename einer Markierung. fallback: nie leer (für das Ziel).</summary>
	internal static string PinName(Minimap.PinData pin, bool fallback)
	{
		string text = pin.m_name ?? "";
		if (text.IndexOf('$') >= 0 && Localization.instance != null)
		{
			text = Localization.instance.Localize(text);
		}
		if (text.Length == 0 && pin.m_type == Minimap.PinType.None)
		{
			text = LocationName(pin.m_pos); // Händler und Orte haben auf der Karte keinen Namen
		}
		if (text.Length == 0 && fallback)
		{
			text = GcLoc.Get(pin.m_type == Minimap.PinType.Bed ? "gcc_bed" : pin.m_type == Minimap.PinType.None ? "gcc_loc" : "gcc_marker");
		}
		return text;
	}

	/// <summary>Name eines Orts mit Kartensymbol: Händler mit Valheims eigenem Namen (z. B. $npc_haldor), Opfersteine, sonst leer.</summary>
	private static string LocationName(Vector3 position)
	{
		if (Time.time >= nextLocationScan && ZoneSystem.instance != null && ZNet.instance != null)
		{
			nextLocationScan = Time.time + 5f;
			LocationScan.Clear();
			ZoneSystem.instance.GetLocationIcons(LocationScan);
			LocationNames.Clear();
			foreach (KeyValuePair<Vector3, string> location in LocationScan)
			{
				LocationNames[location.Key] = location.Value;
			}
		}
		if (!LocationNames.TryGetValue(position, out string prefab))
		{
			return "";
		}
		string lower = prefab.ToLowerInvariant();
		string token = lower.Contains("vendor") || lower.Contains("haldor") ? "$npc_haldor"
			: lower.Contains("hildir") ? "$npc_hildir"
			: lower.Contains("bogwitch") ? "$npc_bogwitch"
			: "";
		if (token.Length > 0 && Localization.instance != null)
		{
			string text = Localization.instance.Localize(token);
			if (!text.StartsWith("[", StringComparison.Ordinal))
			{
				return text; // Valheim kennt den Namen (in jeder Sprache)
			}
		}
		return lower.Contains("starttemple") ? GcLoc.Get("gcc_loc_start") : "";
	}

	private static bool Wanted(Minimap map, Minimap.PinData pin)
	{
		if (!Focus.Selectable(pin))
		{
			return false;
		}
		if (pin.m_type == Minimap.PinType.Player && !Plugin.ShowPlayers.Value)
		{
			return false;
		}
		if (pin.m_checked && !Plugin.ShowChecked.Value)
		{
			return false;
		}
		int index = (int)pin.m_type;
		if (index >= 0 && index < map.m_visibleIconTypes.Length && !map.m_visibleIconTypes[index])
		{
			return false; // im Kartenfilter ausgeblendet
		}
		return pin.m_ownerID == 0L || map.m_sharedMapDataFade > 0f; // geteilte Markierungen nur, wenn die Karte sie zeigt
	}

	private void Collect(Player player, float heading, float halfFov)
	{
		normals.Clear();
		highlights.Clear();
		Minimap? map = Minimap.instance;
		if (Game.m_noMap || map == null)
		{
			return; // ohne Karte nur die Himmelsrichtungen
		}
		Vector3 here = player.transform.position;
		Minimap.PinData? focus = Focus.Pin;
		bool dots = Plugin.Icons.Value == IconStyle.Dot;
		float maxDistance = Plugin.MaxDistance.Value;
		foreach (Minimap.PinData pin in map.m_pins)
		{
			if (pin == focus || !Wanted(map, pin))
			{
				continue;
			}
			float distance = Utils.DistanceXZ(here, pin.m_pos);
			if (distance > maxDistance)
			{
				continue;
			}
			float rel = Rel(here, pin.m_pos, heading);
			if (Mathf.Abs(rel) > halfFov)
			{
				continue; // nicht in Blickrichtung: zählt nicht für die Höchstzahl
			}
			normals.Add(new Item
			{
				Pin = pin,
				Icon = dots ? Sprites.Circle : pin.m_icon,
				Name = "",
				Color = Color.white,
				Distance = distance,
				Rel = rel,
				Faded = pin.m_checked,
			});
		}
		if (focus != null)
		{
			highlights.Add(new Item
			{
				Pin = focus,
				Icon = dots ? Sprites.Circle : focus.m_icon,
				Name = PinName(focus, true),
				Color = FocusColor,
				Distance = Utils.DistanceXZ(here, focus.m_pos),
				Rel = Rel(here, focus.m_pos, heading),
				Focus = true,
			});
		}
		if (Chat.instance != null)
		{
			pings.Clear();
			Chat.instance.GetPingWorldTexts(pings);
			if (pingSprite == null)
			{
				pingSprite = map.GetSprite(Minimap.PinType.Ping);
			}
			foreach (Chat.WorldTextInstance ping in pings)
			{
				highlights.Add(new Item
				{
					Icon = dots || pingSprite == null ? Sprites.Circle : pingSprite,
					Name = ping.m_name,
					Color = PingColor,
					Distance = Utils.DistanceXZ(here, ping.m_position),
					Rel = Rel(here, ping.m_position, heading),
				});
			}
		}
	}

	private static string FormatDistance(float meters)
	{
		return meters < 1000f
			? meters.ToString("0", CultureInfo.CurrentCulture) + " m"
			: (meters / 1000f).ToString("0.0", CultureInfo.CurrentCulture) + " km";
	}

	private bool Free(int row, float min, float max)
	{
		foreach (Vector2 range in taken[row])
		{
			if (min < range.y && max > range.x)
			{
				return false;
			}
		}
		return true;
	}

	private float LabelRow(int row) => row == 0 ? rowLabel0 : row == 1 ? rowLabel1 : rowLabel2;

	/// <summary>Andere Markierungen: weit entfernte zuerst (liegen hinten), mit der Entfernung kleiner und blasser.</summary>
	private void DrawNormals(float half, float halfFov, float s)
	{
		normals.Sort(FarFirst);
		int start = Mathf.Max(0, normals.Count - MaxNormals); // nur die nächsten zeichnen
		int used = 0;
		float far = Mathf.Max(NearDistance + 1f, Plugin.MaxDistance.Value);
		for (int i = start; i < normals.Count; i++)
		{
			Item item = normals[i];
			Marker marker = GetMarker(normalPool, normalLayer, used++);
			float t = Mathf.InverseLerp(NearDistance, far, item.Distance);
			float size = 22f * s * Mathf.Lerp(1f, 0.55f, t);
			item.Fade = Mathf.Lerp(0.95f, 0.3f, t) * (item.Faded ? 0.5f : 1f) * EdgeFade(Mathf.Abs(item.Rel) / halfFov);
			item.X = item.Rel / halfFov * half;
			item.Marker = marker;
			normals[i] = item;
			marker.Icon.sprite = item.Icon;
			marker.Icon.color = new Color(1f, 1f, 1f, item.Fade);
			Place(marker.Icon, item.X, RowIcon * s, size, size);
			Show(marker.Icon, item.Icon != null); // Namen setzt erst der Namens-Durchgang (kein An- und Ausschalten pro Frame)
		}
		HideFrom(normalPool, used);
	}

	/// <summary>Ziel und Pings: immer sichtbar (außerhalb des Sichtbereichs am Rand), Namen auf dunklem Grund, nie verdeckt.</summary>
	private void DrawHighlights(float half, float halfFov, float s)
	{
		foreach (List<Vector2> row in taken)
		{
			row.Clear();
		}
		highlights.Sort(HighlightOrder);
		int used = 0;
		for (int i = 0; i < highlights.Count; i++)
		{
			Item item = highlights[i];
			Marker marker = GetMarker(highlightPool, highlightLayer, used++);
			bool outside = Mathf.Abs(item.Rel) > halfFov;
			item.X = outside ? Mathf.Sign(item.Rel) * half : item.Rel / halfFov * half;
			item.Marker = marker;
			highlights[i] = item;
			float size = (item.Focus ? 30f : 27f) * s * (item.Focus ? 1f : 1f + 0.12f * Mathf.Sin(Time.time * 6f));
			marker.Icon.sprite = item.Icon;
			marker.Icon.color = item.Color;
			Place(marker.Icon, item.X, RowIcon * s, size, size);
			Show(marker.Icon, item.Icon != null);
		}
		HideFrom(highlightPool, used);

		// Namen: zuerst das Ziel, dann die Pings nach Nähe; wer keinen Platz in der oberen Zeile hat, rückt nach unten
		for (int pass = 0; pass < 2; pass++)
		{
			for (int i = highlights.Count - 1; i >= 0; i--)
			{
				Item item = highlights[i];
				if (item.Focus != (pass == 0) || item.Marker == null)
				{
					continue;
				}
				Text label = item.Marker.Label;
				string text = item.Name;
				if (Plugin.ShowDistance.Value)
				{
					text = (text.Length > 0 ? text + "  " : "") + "<color=#E6E6E6>" + FormatDistance(item.Distance) + "</color>";
				}
				if (Mathf.Abs(item.Rel) > halfFov)
				{
					text = item.Rel < 0f ? "« " + text : text + " »"; // liegt hinter dir: am Rand mit Richtungspfeil
				}
				label.fontSize = Mathf.Max(6, Mathf.RoundToInt(17f * s));
				label.text = text;
				label.color = item.Color;
				float width = label.preferredWidth + 14f * s;
				float height = 22f * s;
				float x = Mathf.Clamp(item.X, -half + width / 2f, half - width / 2f);
				int row = 0;
				while (row < taken.Length - 1 && !Free(row, x - width / 2f, x + width / 2f))
				{
					row++;
				}
				taken[row].Add(new Vector2(x - width / 2f, x + width / 2f));
				Place(label, x, LabelRow(row), width, height);
				Place(item.Marker.Plate, x, LabelRow(row), width, height);
				Show(label, text.Length > 0);
				Show(item.Marker.Plate, text.Length > 0);
			}
		}

		// Namen der anderen: nur in der oberen Zeile und nur, wo Platz ist (die nächsten zuerst)
		NameMode mode = Plugin.OtherNames.Value;
		float nameDistance = Plugin.NameDistance.Value;
		float far = Mathf.Max(NearDistance + 1f, Plugin.MaxDistance.Value);
		for (int i = normals.Count - 1; i >= 0; i--)
		{
			Item item = normals[i];
			if (item.Marker == null)
			{
				continue; // nicht gezeichnet (mehr als die Höchstzahl)
			}
			Text label = item.Marker.Label;
			Show(item.Marker.Plate, false);
			bool want = item.Pin != null && (mode == NameMode.Always || (mode == NameMode.Near && item.Distance <= nameDistance));
			string name = want ? PinName(item.Pin!, false) : "";
			if (name.Length == 0)
			{
				Show(label, false);
				continue;
			}
			float t = Mathf.InverseLerp(NearDistance, far, item.Distance);
			label.fontSize = Mathf.Max(6, Mathf.RoundToInt(14f * s * Mathf.Lerp(1f, 0.85f, t)));
			label.text = name;
			label.color = new Color(1f, 1f, 1f, item.Fade);
			float width = label.preferredWidth + 6f * s;
			float x = Mathf.Clamp(item.X, -half + width / 2f, half - width / 2f);
			if (!Free(0, x - width / 2f, x + width / 2f))
			{
				Show(label, false); // weicht hervorgehobenen und näheren Namen aus
				continue;
			}
			taken[0].Add(new Vector2(x - width / 2f, x + width / 2f));
			Place(label, x, rowLabel0, width, 20f * s);
			Show(label, true);
		}
	}
}

/// <summary>Einfache Grafiken ohne externe Dateien: Punkt, Pfeilspitze, weich auslaufender Balken.</summary>
internal static class Sprites
{
	private static Sprite? circle;
	private static Sprite? triangle;
	private static Sprite? band;

	public static Sprite Circle => circle ??= Make(64, 64, (x, y) =>
	{
		float dx = x + 0.5f - 32f;
		float dy = y + 0.5f - 32f;
		return Mathf.Clamp01(31f - Mathf.Sqrt(dx * dx + dy * dy));
	});

	/// <summary>Pfeilspitze nach rechts.</summary>
	public static Sprite Triangle => triangle ??= Make(32, 32, (x, y) =>
	{
		float halfHeight = (1f - (x + 0.5f) / 32f) * 16f;
		return Mathf.Clamp01(halfHeight - Mathf.Abs(y + 0.5f - 16f) + 0.5f);
	});

	public static Sprite Band => band ??= Make(128, 4, (x, y) =>
	{
		float u = x / 127f;
		return Fade(u) * Fade(1f - u);
	});

	private static float Fade(float u)
	{
		float t = Mathf.Clamp01(u / 0.25f);
		return t * t * (3f - 2f * t);
	}

	private static Sprite Make(int width, int height, Func<int, int, float> alpha)
	{
		Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
		{
			filterMode = FilterMode.Bilinear,
			wrapMode = TextureWrapMode.Clamp,
			hideFlags = HideFlags.HideAndDontSave, // bleibt über Szenenwechsel erhalten
		};
		Color32[] pixels = new Color32[width * height];
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				pixels[y * width + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha(x, y)) * 255f));
			}
		}
		texture.SetPixels32(pixels);
		texture.Apply();
		Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
		sprite.hideFlags = HideFlags.HideAndDontSave;
		return sprite;
	}
}
