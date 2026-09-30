// Streamer.bot: Action "LB" -> Sub-Action "Run C# Code" (Execute Code)
// Nötige Referenz: Newtonsoft.Json.dll (im Streamer.bot-Ordner) -> im Code-Editor unter "References" hinzufügen
using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public class E { public string user, uid, side, rid, red; public int amt; }
public class B { public string title = "", status = "none", winner = ""; public long endsAt; public List<E> e = new List<E>(); }

public class CPHInline
{
    long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

    B Load()
    {
        var s = CPH.GetGlobalVar<string>("lb", true);
        return string.IsNullOrEmpty(s) ? new B() : JsonConvert.DeserializeObject<B>(s);
    }

    Dictionary<string, int[]> Board()
    {
        var s = CPH.GetGlobalVar<string>("lb_board", true);
        return string.IsNullOrEmpty(s) ? new Dictionary<string, int[]>() : JsonConvert.DeserializeObject<Dictionary<string, int[]>>(s);
    }

    void Save(B b)
    {
        CPH.SetGlobalVar("lb", JsonConvert.SerializeObject(b), true);
        Push(b);
    }

    void Push(B b)
    {
        var o = new Dictionary<string, object> {
            {"lb", 1}, {"title", b.title}, {"status", b.status}, {"endsAt", b.endsAt}, {"winner", b.winner}
        };
        int tot = b.e.Sum(x => x.amt);
        foreach (var s in new[] { "ja", "nein" })
        {
            var g = b.e.Where(x => x.side == s).GroupBy(x => x.user)
                .Select(x => new { n = x.Key, a = x.Sum(y => y.amt) }).OrderByDescending(x => x.a).ToList();
            int sum = g.Sum(x => x.a);
            // Quote = Gesamttopf / Einsatz dieser Seite (nur Anzeige, keine Auszahlung)
            o[s] = new { n = g.Count, sum = sum, q = sum > 0 ? Math.Round((double)tot / sum, 2) : 0.0, top = g.Take(3).ToList() };
        }
        o["board"] = Board().OrderByDescending(kv => kv.Value[0]).ThenBy(kv => kv.Value[1]).Take(5)
            .Select(kv => new { n = kv.Key, w = kv.Value[0], l = kv.Value[1] }).ToList();
        CPH.WebsocketBroadcastJson(JsonConvert.SerializeObject(o));
    }

    void Refund(E x) { CPH.TwitchRedemptionCancel(x.rid, x.red); CPH.Wait(150); }   // Punkte zurück
    void Consume(E x) { CPH.TwitchRedemptionFulfill(x.rid, x.red); CPH.Wait(150); } // Punkte verbraucht

    public bool Execute()
    {
        var b = Load();
        if (args.ContainsKey("redemptionId")) return Redeem(b);

        string raw = (args.ContainsKey("rawInput") ? args["rawInput"].ToString() : "").Trim();
        string[] p = raw.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
        string cmd = p.Length > 0 ? p[0].ToLower() : "sync";

        switch (cmd)
        {
            case "start":
                // !wette start 3d Wird der Streamer das Baumhaus in 3 Tagen fertig bauen?
                if (b.status == "open" || b.status == "locked") { CPH.SendMessage("Es läuft schon eine Wette. Erst !wette ende ja|nein oder !wette abbruch."); return true; }
                var m = System.Text.RegularExpressions.Regex.Match(raw, @"^start\s+(\d+)([mhd])\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) { CPH.SendMessage("Format: !wette start 3d Titel der Wette (m = Minuten, h = Stunden, d = Tage)"); return true; }
                int n = int.Parse(m.Groups[1].Value);
                char u = char.ToLower(m.Groups[2].Value[0]);
                double min = u == 'm' ? n : u == 'h' ? n * 60 : n * 1440;
                b = new B { title = m.Groups[3].Value, status = "open", endsAt = Now() + (long)(min * 60000) };
                Save(b);
                CPH.SendMessage("Neue Wette: " + b.title + " – Einsatz über die Wette-Belohnungen (JA/NEIN)!");
                break;

            case "ende":
            case "end":
                string w = p.Length > 1 ? p[1].ToLower() : "";
                if (w == "yes") w = "ja"; if (w == "no") w = "nein";
                if ((b.status != "open" && b.status != "locked") || (w != "ja" && w != "nein")) { CPH.SendMessage("Format: !wette ende ja  oder  !wette ende nein"); return true; }
                // Gewinner: Einsatz zurück. Verlierer: Einsatz verfällt.
                foreach (var x in b.e) { if (x.side == w) Refund(x); else Consume(x); }
                var bd = Board();
                foreach (var grp in b.e.GroupBy(x => x.user))
                {
                    if (!bd.ContainsKey(grp.Key)) bd[grp.Key] = new[] { 0, 0 };
                    if (grp.First().side == w) bd[grp.Key][0]++; else bd[grp.Key][1]++;
                }
                CPH.SetGlobalVar("lb_board", JsonConvert.SerializeObject(bd), true);
                int pot = b.e.Sum(x => x.amt), ws = b.e.Where(x => x.side == w).Sum(x => x.amt);
                b.status = "resolved"; b.winner = w;
                Save(b);
                CPH.SendMessage("Wette beendet: " + w.ToUpper() + " gewinnt" + (ws > 0 ? " (Quote " + Math.Round((double)pot / ws, 2) + "x)" : "") + "! Gewinner bekommen ihre Einsätze zurück.");
                break;

            case "abbruch":
            case "cancel":
                foreach (var x in b.e) Refund(x);
                b.status = "cancelled";
                Save(b);
                CPH.SendMessage("Wette abgebrochen – alle Einsätze wurden zurückerstattet.");
                break;

            case "boardreset": // Rangliste löschen
                CPH.SetGlobalVar("lb_board", "{}", true);
                Push(b);
                break;

            case "reset": // Overlay ausblenden / Zustand leeren
                Save(new B());
                break;

            default: // "sync"
                Push(b);
                break;
        }
        return true;
    }

    bool Redeem(B b)
    {
        string rid = args["rewardId"].ToString(), red = args["redemptionId"].ToString();
        string name = args["rewardName"].ToString().ToUpper();
        string user = args["user"].ToString(), uid = args["userId"].ToString();
        int cost = Convert.ToInt32(args["rewardCost"]);
        string side = System.Text.RegularExpressions.Regex.IsMatch(name, @"\b(NEIN|NO)\b") ? "nein" : "ja"; // reward name containing NEIN or NO = NO side

        if (b.status == "open" && Now() >= b.endsAt) { b.status = "locked"; Save(b); }

        if (b.status != "open")
        {
            CPH.TwitchRedemptionCancel(rid, red);
            CPH.SendMessage("@" + user + " aktuell läuft keine offene Wette – Punkte zurück.");
            return true;
        }
        var prev = b.e.FirstOrDefault(x => x.uid == uid);
        if (prev != null && prev.side != side)
        {
            CPH.TwitchRedemptionCancel(rid, red);
            CPH.SendMessage("@" + user + " du hast schon auf " + prev.side.ToUpper() + " gesetzt – Punkte zurück.");
            return true;
        }
        b.e.Add(new E { user = user, uid = uid, side = side, amt = cost, rid = rid, red = red });
        Save(b);
        CPH.SendMessage(user + " setzt " + cost + " Punkte auf " + side.ToUpper() + "!");
        return true;
    }
}
