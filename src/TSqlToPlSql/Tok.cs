namespace TSqlToPlSql;

/// <summary>Hilfsfunktionen für Token-Listen.</summary>
internal static class Tok
{
    public static List<Token> Trim(List<Token> l)
    {
        int a = 0, b = l.Count;
        while (a < b && (l[a].IsTrivia || l[a].IsEof)) a++;
        while (b > a && (l[b - 1].IsTrivia || l[b - 1].IsEof)) b--;
        return l.GetRange(a, b - a);
    }

    public static int MatchParen(List<Token> l, int open)
    {
        int d = 0;
        for (int k = open; k < l.Count; k++)
        {
            if (l[k].IsSym("(")) d++;
            else if (l[k].IsSym(")")) { d--; if (d == 0) return k; }
        }
        return l.Count;
    }

    public static int MatchCase(List<Token> l, int start)
    {
        int d = 0;
        for (int k = start; k < l.Count; k++)
        {
            if (l[k].Is("CASE")) d++;
            else if (l[k].Is("END")) { d--; if (d == 0) return k; }
        }
        return l.Count;
    }

    public static List<List<Token>> SplitTopLevel(List<Token> l, string sep = ",")
    {
        var res = new List<List<Token>>();
        var cur = new List<Token>();
        int depth = 0;
        foreach (var t in l)
        {
            if (t.IsEof) continue;
            if (t.IsSym("(") || t.Is("CASE")) depth++;
            else if ((t.IsSym(")") || t.Is("END")) && depth > 0) depth--;
            if (depth == 0 && t.IsSym(sep)) { res.Add(cur); cur = new List<Token>(); continue; }
            cur.Add(t);
        }
        res.Add(cur);
        return res;
    }

    public static int FindTopLevel(List<Token> l, int start, Func<Token, bool> pred)
    {
        int depth = 0;
        for (int k = start; k < l.Count; k++)
        {
            var t = l[k];
            if (t.IsTrivia) continue;
            if (depth == 0 && pred(t)) return k;
            if (t.IsSym("(") || t.Is("CASE")) depth++;
            else if ((t.IsSym(")") || t.Is("END")) && depth > 0) depth--;
        }
        return -1;
    }

    public static int NextSig(List<Token> l, int from)
    {
        while (from < l.Count && l[from].IsTrivia) from++;
        return from;
    }

    public static List<Token> Sig(List<Token> l) => l.Where(t => !t.IsTrivia && !t.IsEof).ToList();

    public static string Text(IEnumerable<Token> l) => string.Concat(l.Select(t => t.Text));

    /// <summary>true, wenn die Liste genau aus einer Klammergruppe besteht, deren Inhalt mit SELECT beginnt.</summary>
    public static bool IsParenSubquery(List<Token> trimmed, out List<Token> inner)
    {
        inner = new();
        if (trimmed.Count < 2 || !trimmed[0].IsSym("(")) return false;
        int close = MatchParen(trimmed, 0);
        if (close != trimmed.Count - 1) return false;
        inner = trimmed.GetRange(1, close - 1);
        var sig = Sig(inner);
        return sig.Count > 0 && (sig[0].Is("SELECT") || sig[0].Is("WITH"));
    }
}
