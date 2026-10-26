namespace GC.Client.Compare;

/// <summary>
/// Wann eine Funktion greift (aus GC_Traglimit übernommen, damit der Ausrüstungs-Vergleich allein läuft). Eigener Namespace
/// und internal: Beide Mods liegen in GC_Client.dll, eine zweite öffentliche GC.Client.CarryLimit.Activation bricht das
/// Zusammenführen ab (26.10.2026). Die cfg speichert nur die Namen der Werte.
/// </summary>
internal enum Activation
{
	Hold,
	Toggle,
	Always
}
