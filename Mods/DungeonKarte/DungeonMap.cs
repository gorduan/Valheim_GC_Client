using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using GC.Client;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Client.DungeonMap;

/// <summary>
/// GC_DungeonMap: Karte für Dungeons (Grabkammern, Krypten, Höhlen, Minen), die sich Raum für Raum aufdeckt.
///
/// Dungeons liegen in Valheim als Innenräume oberhalb von 3000 m (Character.InInterior) und bestehen aus Räumen
/// (Klasse Room mit Größe m_size), die der DungeonGenerator zusammensetzt. Betritt der Spieler einen Raum, gilt
/// er als erkundet und wird gezeichnet, mit seinen Durchgängen als Punkte. Im Dungeon ersetzt die Karte die
/// kleine Minimap oben rechts. Die Karten-Taste (M) zeigt sie groß. Erkundete Räume werden am Charakter
/// gespeichert (Player.m_customData), getrennt nach Welt und Dungeon. Andere Stockwerke erscheinen blass.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_dungeonmap";
	public const string ModName = "GC_DungeonMap";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked && ClientPlugin.Allowed;
	internal static ConfigEntry<float> SmallSize = null!;
	internal static ConfigEntry<float> SmallZoom = null!;
	internal static ConfigEntry<Vector2> SmallOffset = null!;
	internal static ConfigEntry<bool> HideVanillaMinimap = null!;

	internal static MapView? View;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "9 - DungeonMap", "Enabled", true, "Funktion ein- oder ausschalten (auch im Menü GC Option).");
		SmallSize = Gc.Bind(Config, "9 - DungeonMap - Interface", "SmallMapSize", 240f, "Größe der kleinen Dungeon-Karte in Pixeln.", new AcceptableValueRange<float>(120f, 500f));
		SmallZoom = Gc.Bind(Config, "9 - DungeonMap - Interface", "SmallMapPixelsPerMeter", 3f, "Maßstab der kleinen Karte (Pixel pro Meter).", new AcceptableValueRange<float>(1f, 10f));
		SmallOffset = Gc.Bind(Config, "9 - DungeonMap - Interface", "SmallMapOffset", new Vector2(-24f, -24f), "Abstand der kleinen Karte von der oberen rechten Bildschirmecke.");
		HideVanillaMinimap = Gc.Bind(Config, "9 - DungeonMap - Interface", "HideVanillaMinimapInDungeon", true, "Im Dungeon die normale Minimap ausblenden.");
	}

	internal static void Update()
	{
		Player player = Player.m_localPlayer;
		if (player == null || GUIManager.CustomGUIBack == null)
		{
			// Ausloggen baut das HUD neu: die alte Karte samt ihren (dann zerstörten) Fenstern vergessen. Sonst griff Hide
			// nach dem ersten Ausloggen in jedem Bild ins Leere (Unity-NRE im Log von Gorduans Profil Default, 17.10.2026).
			View?.Hide();
			View = null;
			return;
		}
		if (!On || Gc.IsHeadless || Game.m_noMap)
		{
			View?.Hide(); // Welt-Einstellung „keine Karte" respektieren
			return;
		}
		View ??= new MapView();
		View.Tick(player);
	}
}

/// <summary>Karten-Taste im Dungeon: eigene große Karte statt Weltkarte.</summary>
[HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
internal static class MapModePatch
{
	private static bool Prefix(Minimap.MapMode mode)
	{
		if (!Plugin.On || Game.m_noMap || mode != Minimap.MapMode.Large || Player.m_localPlayer == null || !Player.m_localPlayer.InInterior() || Plugin.View == null)
		{
			return true;
		}
		Plugin.View.ToggleLarge();
		return false;
	}
}

internal sealed class DungeonData
{
	public string Key = "";
	public Vector3 Origin;
	public List<Room> Rooms = new();
	public Dictionary<Room, string> RoomKeys = new();
	public HashSet<string> Explored = new();
}

internal sealed class MapView
{
	private const string DataPrefix = "gc_dmap_";
	private static readonly Color RoomColor = new(0.82f, 0.72f, 0.52f, 0.95f);
	private static readonly Color CurrentColor = new(1f, 0.85f, 0.45f, 1f);
	private static readonly Color OtherFloorColor = new(0.82f, 0.72f, 0.52f, 0.25f);
	private static readonly Color DoorColor = new(0.25f, 0.15f, 0.07f, 1f);

	private GameObject? small;
	private RectTransform? smallContent;
	private GameObject? large;
	private RectTransform? largeContent;
	private Image? smallPlayer;
	private Image? largePlayer;
	private readonly Dictionary<Room, Image> smallRooms = new();
	private readonly Dictionary<Room, Image> largeRooms = new();
	private DungeonData? current;
	private float nextScan;
	private float nextTick;
	private bool minimapHidden;
	private bool dirty = true;

	/// <summary>Große Dungeon-Karte offen (der Kompass blendet sich dann aus).</summary>
	public bool LargeOpen => large != null && large.activeSelf;

	public void Hide()
	{
		// Unitys != null erkennt auch zerstörte Fenster (?. prüft nur C#-null)
		if (small != null)
		{
			small.SetActive(false);
		}
		if (large != null)
		{
			large.SetActive(false);
		}
		RestoreMinimap();
	}

	public void ToggleLarge()
	{
		Build();
		bool show = !large!.activeSelf;
		large.SetActive(show);
		dirty = true;
	}

	public void Tick(Player player)
	{
		if (!player.InInterior())
		{
			if (current != null)
			{
				Save(player);
				current = null;
			}
			Hide();
			return;
		}
		Build();
		if (large!.activeSelf && Input.GetKeyDown(KeyCode.Escape))
		{
			large.SetActive(false);
		}
		if (Plugin.HideVanillaMinimap.Value && Minimap.instance != null && Minimap.instance.m_smallRoot != null && Minimap.instance.m_smallRoot.activeSelf)
		{
			Minimap.instance.m_smallRoot.SetActive(false);
			minimapHidden = true;
		}
		small!.SetActive(true);

		if (Time.time >= nextScan || current == null)
		{
			nextScan = Time.time + 3f;
			Scan(player);
		}
		if (current == null)
		{
			return;
		}
		if (Time.time >= nextTick)
		{
			nextTick = Time.time + 0.25f;
			Explore(player);
		}
		Draw(player);
	}

	private void RestoreMinimap()
	{
		if (minimapHidden && Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Small)
		{
			Minimap.instance.m_smallRoot.SetActive(true);
		}
		minimapHidden = false;
	}

	private void Scan(Player player)
	{
		Vector3 position = player.transform.position;
		DungeonGenerator? generator = UnityEngine.Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None)
			.Where(g => g != null && g.transform.position.y > 2000f)
			.OrderBy(g => Utils.DistanceXZ(g.transform.position, position))
			.FirstOrDefault();
		if (generator == null || Utils.DistanceXZ(generator.transform.position, position) > 400f)
		{
			return;
		}
		Vector3 origin = generator.transform.position;
		string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "world";
		string key = $"{world}_{Mathf.RoundToInt(origin.x)}_{Mathf.RoundToInt(origin.z)}";
		if (current == null || current.Key != key)
		{
			if (current != null)
			{
				Save(player);
			}
			current = new DungeonData { Key = key, Origin = origin };
			Load(player);
			ClearImages();
		}
		List<Room> rooms = UnityEngine.Object.FindObjectsByType<Room>(FindObjectsSortMode.None)
			.Where(r => r != null && r.transform.position.y > 2000f && Utils.DistanceXZ(r.transform.position, origin) < 400f)
			.ToList();
		if (rooms.Count != current.Rooms.Count)
		{
			current.Rooms = rooms;
			current.RoomKeys = rooms.ToDictionary(r => r, r => RoomKey(r, origin));
			dirty = true;
		}
	}

	private static string RoomKey(Room room, Vector3 origin)
	{
		Vector3 p = room.transform.position - origin;
		return $"{Mathf.RoundToInt(p.x)}:{Mathf.RoundToInt(p.y)}:{Mathf.RoundToInt(p.z)}";
	}

	private static bool Inside(Room room, Vector3 point)
	{
		Vector3 local = room.transform.InverseTransformPoint(point);
		Vector3 half = new(room.m_size.x * 0.5f + 0.5f, room.m_size.y * 0.5f + 1f, room.m_size.z * 0.5f + 0.5f);
		return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
	}

	private void Explore(Player player)
	{
		Vector3 point = player.transform.position + Vector3.up;
		foreach (Room room in current!.Rooms)
		{
			if (room != null && Inside(room, point) && current.Explored.Add(current.RoomKeys[room]))
			{
				dirty = true;
			}
		}
	}

	private void Load(Player player)
	{
		if (player.m_customData.TryGetValue(DataPrefix + current!.Key, out string data))
		{
			foreach (string part in data.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
			{
				current.Explored.Add(part);
			}
		}
	}

	public void Save(Player player)
	{
		if (current == null || current.Explored.Count == 0)
		{
			return;
		}
		player.m_customData[DataPrefix + current.Key] = string.Join(",", current.Explored);
	}

	private void ClearImages()
	{
		foreach (Image image in smallRooms.Values.Concat(largeRooms.Values))
		{
			if (image != null)
			{
				UnityEngine.Object.Destroy(image.gameObject);
			}
		}
		smallRooms.Clear();
		largeRooms.Clear();
		dirty = true;
	}

	private void Draw(Player player)
	{
		DungeonData data = current!;
		float playerY = player.transform.position.y;
		Room? here = data.Rooms.FirstOrDefault(r => r != null && Inside(r, player.transform.position + Vector3.up));

		if (dirty)
		{
			dirty = false;
			foreach (Room room in data.Rooms)
			{
				if (room == null || !data.RoomKeys.TryGetValue(room, out string key) || !data.Explored.Contains(key))
				{
					continue;
				}
				if (!smallRooms.ContainsKey(room))
				{
					smallRooms[room] = CreateRoom(smallContent!, room);
				}
				if (!largeRooms.ContainsKey(room))
				{
					largeRooms[room] = CreateRoom(largeContent!, room);
				}
			}
			FitLarge(data);
		}

		float zoom = Plugin.SmallZoom.Value;
		Place(smallRooms, zoom, data, here, playerY);
		smallContent!.anchoredPosition = -MapPoint(player.transform.position, data.Origin) * zoom;
		PlacePlayer(smallPlayer!, player, Vector2.zero);

		if (large!.activeSelf)
		{
			float largeZoom = largeContent!.localScale.x;
			Place(largeRooms, 1f, data, here, playerY);
			PlacePlayer(largePlayer!, player, largeContent.anchoredPosition + MapPoint(player.transform.position, data.Origin) * largeZoom);
		}
	}

	private static Vector2 MapPoint(Vector3 world, Vector3 origin) => new(world.x - origin.x, world.z - origin.z);

	private static void Place(Dictionary<Room, Image> images, float zoom, DungeonData data, Room? here, float playerY)
	{
		foreach (KeyValuePair<Room, Image> pair in images)
		{
			Room room = pair.Key;
			Image image = pair.Value;
			if (room == null || image == null)
			{
				continue;
			}
			RectTransform rect = image.rectTransform;
			rect.anchoredPosition = MapPoint(room.transform.position, data.Origin) * zoom;
			rect.sizeDelta = new Vector2(room.m_size.x, room.m_size.z) * zoom;
			rect.localEulerAngles = new Vector3(0f, 0f, -room.transform.eulerAngles.y);
			bool sameFloor = Mathf.Abs(room.transform.position.y - playerY) < room.m_size.y * 0.5f + 3f;
			image.color = room == here ? CurrentColor : sameFloor ? RoomColor : OtherFloorColor;
			foreach (Transform door in rect)
			{
				((RectTransform)door).sizeDelta = Vector2.one * Mathf.Max(3f, zoom * 1.5f);
			}
		}
	}

	private static Image CreateRoom(RectTransform parent, Room room)
	{
		GameObject go = new("Room", typeof(RectTransform), typeof(Image));
		go.transform.SetParent(parent, false);
		Image image = go.GetComponent<Image>();
		image.raycastTarget = false;
		RectTransform rect = image.rectTransform;
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		// Durchgänge des Raums als dunkle Punkte
		foreach (RoomConnection connection in room.GetConnections())
		{
			if (connection == null)
			{
				continue;
			}
			Vector3 local = room.transform.InverseTransformPoint(connection.transform.position);
			GameObject dot = new("Door", typeof(RectTransform), typeof(Image));
			dot.transform.SetParent(go.transform, false);
			Image dotImage = dot.GetComponent<Image>();
			dotImage.color = DoorColor;
			dotImage.raycastTarget = false;
			RectTransform dotRect = dotImage.rectTransform;
			dotRect.anchorMin = dotRect.anchorMax = new Vector2(
				Mathf.Clamp01(0.5f + local.x / Mathf.Max(1f, room.m_size.x)),
				Mathf.Clamp01(0.5f + local.z / Mathf.Max(1f, room.m_size.z)));
			dotRect.anchoredPosition = Vector2.zero;
		}
		return image;
	}

	private void FitLarge(DungeonData data)
	{
		if (largeRooms.Count == 0)
		{
			return;
		}
		Vector2 min = new(float.MaxValue, float.MaxValue);
		Vector2 max = new(float.MinValue, float.MinValue);
		foreach (Room room in largeRooms.Keys)
		{
			if (room == null)
			{
				continue;
			}
			Vector2 p = MapPoint(room.transform.position, data.Origin);
			float r = Mathf.Max(room.m_size.x, room.m_size.z) * 0.75f;
			min = Vector2.Min(min, p - Vector2.one * r);
			max = Vector2.Max(max, p + Vector2.one * r);
		}
		RectTransform frame = (RectTransform)large!.transform;
		Vector2 size = frame.rect.size - new Vector2(80f, 120f);
		Vector2 extent = max - min;
		float zoom = Mathf.Clamp(Mathf.Min(size.x / Mathf.Max(1f, extent.x), size.y / Mathf.Max(1f, extent.y)), 1f, 12f);
		largeContent!.localScale = Vector3.one * zoom;
		largeContent.anchoredPosition = -((min + max) * 0.5f) * zoom;
	}

	private static void PlacePlayer(Image marker, Player player, Vector2 position)
	{
		marker.rectTransform.anchoredPosition = position;
		marker.rectTransform.localEulerAngles = new Vector3(0f, 0f, -player.transform.eulerAngles.y);
		marker.transform.SetAsLastSibling();
	}

	private void Build()
	{
		if (small != null && large != null)
		{
			return;
		}
		// klein oben rechts: unter Valheims HUD-Wurzel (bildschirmfüllend), hinter den anderen HUD-Teilen. Unter Jötunns
		// CustomGUIBack hatte der Kompass im Spieltest keine Breite; eine Verankerung an der Ecke säße dort falsch
		Transform smallParent = Hud.instance != null && Hud.instance.m_rootObject != null ? Hud.instance.m_rootObject.transform : GUIManager.CustomGUIBack.transform;
		small = CreateFrame("GC_DungeonMap_Small", smallParent, new Vector2(1f, 1f), Plugin.SmallOffset.Value, Vector2.one * Plugin.SmallSize.Value, out smallContent, out smallPlayer);
		small.transform.SetAsFirstSibling();
		large = CreateFrame("GC_DungeonMap_Large", GUIManager.CustomGUIFront.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 760f), out largeContent, out largePlayer);
		Text title = GcUi.CreateLabel(large.transform, GcLoc.Get("gcdm_title"), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(20f, -60f), new Vector2(-20f, -10f), 30, TextAnchor.MiddleCenter, GUIManager.Instance.ValheimOrange, true);
		title.raycastTarget = false;
		large.SetActive(false);
		small.SetActive(false);
	}

	private static GameObject CreateFrame(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size, out RectTransform content, out Image player)
	{
		GameObject frame = new(name, typeof(RectTransform), typeof(Image), typeof(RectMask2D));
		frame.transform.SetParent(parent, false);
		RectTransform rect = (RectTransform)frame.transform;
		rect.anchorMin = rect.anchorMax = anchor;
		rect.pivot = anchor;
		rect.anchoredPosition = position;
		rect.sizeDelta = size;
		Image background = frame.GetComponent<Image>();
		background.color = new Color(0.08f, 0.06f, 0.04f, 0.85f);
		background.raycastTarget = false;
		Sprite? sprite = GUIManager.Instance.GetSprite("panel_interior_bkg_128");
		if (sprite != null)
		{
			background.sprite = sprite;
			background.type = Image.Type.Sliced;
		}

		GameObject contentGo = new("Content", typeof(RectTransform));
		contentGo.transform.SetParent(frame.transform, false);
		content = (RectTransform)contentGo.transform;
		content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f);
		content.pivot = new Vector2(0.5f, 0.5f);
		content.sizeDelta = Vector2.zero;

		GameObject playerGo = new("Player", typeof(RectTransform), typeof(Image));
		playerGo.transform.SetParent(frame.transform, false);
		player = playerGo.GetComponent<Image>();
		player.sprite = Arrow();
		player.color = Color.white;
		player.raycastTarget = false;
		RectTransform playerRect = player.rectTransform;
		playerRect.anchorMin = playerRect.anchorMax = new Vector2(0.5f, 0.5f);
		playerRect.sizeDelta = new Vector2(18f, 18f);
		return frame;
	}

	private static Sprite? arrow;

	private static Sprite Arrow()
	{
		if (arrow != null)
		{
			return arrow;
		}
		const int size = 32;
		Texture2D tex = new(size, size, TextureFormat.RGBA32, false);
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float half = (size - y) * 0.45f;
				bool inside = y > 2 && Mathf.Abs(x - size / 2f) < (size - y) * 0.5f && !(y < 10 && Mathf.Abs(x - size / 2f) < half * 0.45f);
				tex.SetPixel(x, y, inside ? new Color(1f, 0.3f, 0.2f, 1f) : new Color(0f, 0f, 0f, 0f));
			}
		}
		tex.Apply();
		arrow = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
		return arrow;
	}
}

/// <summary>Beim Speichern des Charakters die aktuelle Dungeon-Karte mitschreiben.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Save))]
internal static class PlayerSavePatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer && Plugin.View != null && __instance.InInterior())
		{
			Plugin.View.Save(__instance);
		}
	}
}
