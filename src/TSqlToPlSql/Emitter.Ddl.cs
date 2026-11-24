namespace TSqlToPlSql;

internal sealed partial class Emitter
{
    string? currentTable;

    static readonly HashSet<string> ColumnStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT", "NULL", "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "REFERENCES", "IDENTITY", "COLLATE",
        "ROWGUIDCOL", "SPARSE", "FOREIGN", "DEFAULT", "PERSISTED", "FILESTREAM"
    };

    string CreateTable(List<Token> tk)
    {
        int open = Tok.FindTopLevel(tk, 0, x => x.IsSym("("));
        if (open < 0) return Ddl(Cv(tk));
        int tableIdx = Tok.FindTopLevel(tk, 0, x => x.Is("TABLE"));
        var nameTokens = Tok.Sig(tk.GetRange(tableIdx + 1, open - tableIdx - 1));
        bool temp = nameTokens.Count > 0 && nameTokens[0].Kind == TokenKind.TempTable;
        var name = temp ? ex.TempName(nameTokens[0].Text) : ex.RenderName(nameTokens);
        int close = Tok.MatchParen(tk, open);
        var inner = tk.GetRange(open + 1, Math.Max(0, Math.Min(close, tk.Count) - open - 1));
        var items = Tok.SplitTopLevel(inner).Select(Tok.Trim).Where(i => i.Count > 0).ToList();

        var cols = new List<string>();
        var extra = new List<string>();
        currentTable = name;
        foreach (var item in items)
        {
            if (Tok.Sig(item)[0].Is("INDEX"))
            {
                ctx.Warn("Inline-INDEX in CREATE TABLE → separates CREATE INDEX.");
                extra.Add("-- TODO: separates CREATE INDEX für: " + Collapse(Tok.Text(item)));
                continue;
            }
            var c = ConvertColumnOrConstraint(item);
            if (c != null) cols.Add(c);
        }

        if (close + 1 < tk.Count)
        {
            var after = Tok.Sig(tk.GetRange(close + 1, tk.Count - close - 1));
            if (after.Count > 0 && !after[0].IsAny("ON", "TEXTIMAGE_ON", "WITH", "FILESTREAM_ON"))
                ctx.Warn("Tabellenoptionen nach CREATE TABLE (...) wurden entfernt.");
        }

        if (temp) ctx.Warn("CREATE TABLE #tmp → CREATE GLOBAL TEMPORARY TABLE (gehört in ein einmaliges DDL-Skript).");
        var ddl = $"CREATE {(temp ? "GLOBAL TEMPORARY " : "")}TABLE {name} (\n    {string.Join(",\n    ", cols)}\n)" +
                  (temp ? " ON COMMIT PRESERVE ROWS" : "");
        if (temp && S.IsPlSql)
        {
            currentTable = null;
            AddPrereq(name, ddl + ";");
            return $"DELETE FROM {name}; -- #temp-Tabelle → GTT (siehe Vorab-DDL)";
        }
        currentTable = null;
        var result = Ddl(ddl);
        return extra.Count > 0 ? result + "\n" + string.Join("\n", extra) : result;
    }

    /// <summary>Spaltendefinition oder Tabellen-Constraint (für CREATE/ALTER TABLE und Tabellenvariablen).</summary>
    string? ConvertColumnOrConstraint(List<Token> item)
    {
        var sig = Tok.Sig(item);
        if (sig.Count == 0) return null;
        if (sig[0].IsAny("CONSTRAINT", "PRIMARY", "UNIQUE", "FOREIGN", "CHECK"))
            return ConvertConstraint(item);
        if (sig[0].Is("INDEX")) return null;

        var name = ex.Ident(sig[0]);

        // Berechnete Spalte: name AS (expr) [PERSISTED]
        if (sig.Count > 1 && sig[1].Is("AS"))
        {
            int asIdx = item.IndexOf(sig[1]);
            var expr = item.GetRange(asIdx + 1, item.Count - asIdx - 1)
                .Where(x => !x.Is("PERSISTED")).ToList();
            expr = Tok.Trim(expr);
            var e = Collapse(ex.Convert(expr));
            if (!(e.StartsWith('(') && e.EndsWith(')'))) e = "(" + e + ")";
            return $"{name} AS {e}";
        }

        // Typ: Name[.Name] [ (args) ]
        int i = item.IndexOf(sig[0]) + 1;
        i = Tok.NextSig(item, i);
        var typeTokens = new List<Token>();
        if (i < item.Count && !ColumnStopWords.Contains(item[i].Text))
        {
            typeTokens.Add(item[i]);
            i++;
            while (i + 1 < item.Count && item[i].IsSym(".")) { typeTokens.Add(item[i]); typeTokens.Add(item[i + 1]); i += 2; }
            int j = Tok.NextSig(item, i);
            if (j < item.Count && item[j].Is("PRECISION")) { typeTokens.Add(item[j]); i = j + 1; j = Tok.NextSig(item, i); }
            if (j < item.Count && item[j].Is("VARYING")) { typeTokens.Add(item[j]); i = j + 1; j = Tok.NextSig(item, i); }
            if (j < item.Count && item[j].IsSym("("))
            {
                int c = Tok.MatchParen(item, j);
                typeTokens.AddRange(item.GetRange(j, Math.Min(c, item.Count - 1) - j + 1));
                i = c + 1;
            }
        }
        var type = typeTokens.Count > 0 ? TypeMapper.Map(typeTokens, TypeContext.Column, ctx).Type : "";
        var rest = i < item.Count ? item.GetRange(i, item.Count - i) : new List<Token>();
        var r = Tok.Sig(rest);

        string? def = null, identity = null;
        var parts = new List<string>();
        for (int k = 0; k < r.Count; k++)
        {
            var t = r[k];
            if (t.Is("IDENTITY"))
            {
                if (k + 1 < r.Count && r[k + 1].IsSym("("))
                {
                    var args = new List<string>();
                    int m = k + 2;
                    var cur = "";
                    for (; m < r.Count && !r[m].IsSym(")"); m++)
                    {
                        if (r[m].IsSym(",")) { args.Add(cur); cur = ""; } else cur += r[m].Text;
                    }
                    args.Add(cur);
                    k = m;
                    if (currentTable != null) ctx.IdentityColumns[currentTable] = name;
                    identity = args.Count == 2
                        ? $"GENERATED BY DEFAULT ON NULL AS IDENTITY (START WITH {args[0]} INCREMENT BY {args[1]})"
                        : "GENERATED BY DEFAULT ON NULL AS IDENTITY";
                }
                else identity = "GENERATED BY DEFAULT ON NULL AS IDENTITY";
                if (currentTable != null) ctx.IdentityColumns[currentTable] = name;
                continue;
            }
            if (t.Is("CONSTRAINT"))
            {
                // benannter Default (CONSTRAINT DF_x DEFAULT ...) → Name entfällt
                if (k + 2 < r.Count && r[k + 2].Is("DEFAULT")) { k++; continue; }
                parts.Add($"CONSTRAINT {ex.Ident(r[k + 1])}");
                k++;
                continue;
            }
            if (t.Is("DEFAULT"))
            {
                var exprTok = new List<Token>();
                int m = k + 1, depth = 0;
                for (; m < r.Count; m++)
                {
                    if (depth == 0 && ColumnStopWords.Contains(r[m].Text) && r[m].Kind == TokenKind.Identifier) break;
                    if (r[m].IsSym("(")) depth++;
                    else if (r[m].IsSym(")")) depth--;
                    exprTok.Add(r[m]);
                }
                k = m - 1;
                def = Collapse(ex.Convert(SpaceOut(exprTok)));
                continue;
            }
            if (t.Is("NOT") && k + 1 < r.Count && r[k + 1].Is("NULL")) { parts.Add("NOT NULL"); k++; continue; }
            if (t.Is("NULL")) { parts.Add("NULL"); continue; }
            if (t.Is("PRIMARY")) { parts.Add("PRIMARY KEY"); k++; if (k + 1 < r.Count && r[k + 1].IsAny("CLUSTERED", "NONCLUSTERED")) k++; continue; }
            if (t.Is("UNIQUE")) { parts.Add("UNIQUE"); if (k + 1 < r.Count && r[k + 1].IsAny("CLUSTERED", "NONCLUSTERED")) k++; continue; }
            if (t.Is("CHECK") && k + 1 < r.Count && r[k + 1].IsSym("("))
            {
                int c = Tok.MatchParen(r, k + 1);
                parts.Add("CHECK (" + Collapse(ex.Convert(SpaceOut(r.GetRange(k + 2, Math.Max(0, c - k - 2))))) + ")");
                k = c;
                continue;
            }
            if (t.Is("REFERENCES"))
            {
                var refTok = new List<Token>();
                int m = k + 1;
                while (m < r.Count && !(r[m].Is("ON") || (r[m].Kind == TokenKind.Identifier && ColumnStopWords.Contains(r[m].Text))))
                {
                    refTok.Add(r[m]);
                    m++;
                }
                var refText = "REFERENCES " + Collapse(ex.Convert(refTok));
                while (m + 2 < r.Count && r[m].Is("ON"))
                {
                    var action = r[m + 1].Upper;
                    var what = r[m + 2].Upper;
                    int consumed = 3;
                    if (what is "NO" or "SET") { what += " " + r[m + 3].Upper; consumed = 4; }
                    if (action == "DELETE" && what is "CASCADE" or "SET NULL") refText += $" ON DELETE {what}";
                    else if (action == "UPDATE" && what != "NO ACTION")
                        ctx.Warn("ON UPDATE CASCADE gibt es in Oracle nicht → Trigger oder entfernen.");
                    m += consumed;
                }
                parts.Add(refText);
                k = m - 1;
                continue;
            }
            if (t.IsAny("ROWGUIDCOL", "SPARSE", "PERSISTED", "FILESTREAM")) continue;
            if (t.Is("COLLATE")) { ctx.Warn("COLLATE in Spaltendefinition entfernt."); k++; continue; }
            parts.Add(t.Text);
        }

        var sb = new System.Text.StringBuilder(name);
        if (type.Length > 0) sb.Append(' ').Append(type);
        if (def != null) sb.Append(" DEFAULT ").Append(def);
        if (identity != null) sb.Append(' ').Append(identity);
        foreach (var p in parts) sb.Append(' ').Append(p);
        return sb.ToString();
    }

    static List<Token> SpaceOut(List<Token> sigTokens)
    {
        // Signifikante Tokens wieder mit Leerzeichen trennen, wo nötig
        var res = new List<Token>();
        for (int k = 0; k < sigTokens.Count; k++)
        {
            var t = sigTokens[k];
            if (t.IsTrivia) { res.Add(t); continue; }
            if (res.Count > 0 && !res[^1].IsTrivia && NeedsSpace(res[^1], t)) res.Add(Token.Space());
            res.Add(t);
        }
        return res;
    }

    static bool NeedsSpace(Token a, Token b) =>
        !(a.IsSym("(") || b.IsSym(")") || b.IsSym("(") || b.IsSym(",") || a.IsSym(".") || b.IsSym("."));

    string ConvertConstraint(List<Token> item)
    {
        var res = new List<Token>();
        var t = item;
        string prevSig = "";
        for (int i = 0; i < t.Count; i++)
        {
            var tk = t[i];
            if (tk.IsAny("CLUSTERED", "NONCLUSTERED"))
            {
                if (i + 1 < t.Count && t[i + 1].Kind == TokenKind.Whitespace) i++;
                continue;
            }
            if (tk.IsSym("(") && prevSig is "KEY" or "UNIQUE")
            {
                int c = Tok.MatchParen(t, i);
                res.Add(tk);
                for (int m = i + 1; m < Math.Min(c, t.Count); m++)
                {
                    if (t[m].IsAny("ASC", "DESC"))
                    {
                        while (res.Count > 0 && res[^1].Kind == TokenKind.Whitespace) res.RemoveAt(res.Count - 1);
                        continue;
                    }
                    res.Add(t[m]);
                }
                if (c < t.Count) res.Add(t[c]);
                i = c;
                prevSig = ")";
                continue;
            }
            if (tk.Is("WITH") && Tok.NextSig(t, i + 1) is var w && w < t.Count && t[w].IsSym("("))
            {
                i = Tok.MatchParen(t, w);
                continue;
            }
            if (tk.Is("ON"))
            {
                int n = Tok.NextSig(t, i + 1);
                if (n < t.Count && t[n].Is("DELETE"))
                {
                    int a = Tok.NextSig(t, n + 1);
                    if (a < t.Count && t[a].Is("NO"))
                    {
                        i = Tok.NextSig(t, a + 1);
                        continue;
                    }
                }
                else if (n < t.Count && t[n].Is("UPDATE"))
                {
                    int a = Tok.NextSig(t, n + 1);
                    if (a < t.Count && !t[a].Is("NO")) ctx.Warn("ON UPDATE CASCADE gibt es in Oracle nicht → Trigger oder entfernen.");
                    i = t[a].Is("NO") || t[a].Is("SET") ? Tok.NextSig(t, a + 1) : a;
                    continue;
                }
                else
                {
                    i = n; // ON [PRIMARY] (Filegroup)
                    continue;
                }
            }
            if (tk.IsAny("NOT") && Tok.NextSig(t, i + 1) is var f && f < t.Count && t[f].Is("FOR"))
            {
                // NOT FOR REPLICATION
                i = Tok.NextSig(t, f + 1);
                continue;
            }
            res.Add(tk);
            if (!tk.IsTrivia) prevSig = tk.Kind == TokenKind.Identifier ? tk.Upper : tk.Text;
        }
        return Collapse(ex.Convert(Tok.Trim(res)));
    }

    string CreateIndex(List<Token> tk)
    {
        var res = new List<Token>();
        bool seenOn = false;
        for (int i = 0; i < tk.Count; i++)
        {
            var t = tk[i];
            if (t.IsAny("CLUSTERED", "NONCLUSTERED"))
            {
                if (i + 1 < tk.Count && tk[i + 1].Kind == TokenKind.Whitespace) i++;
                continue;
            }
            if (t.Is("INCLUDE"))
            {
                int p = Tok.NextSig(tk, i + 1);
                ctx.Warn("INCLUDE-Spalten im Index gibt es in Oracle nicht (ggf. als Schlüsselspalten anhängen).");
                i = Tok.MatchParen(tk, p);
                continue;
            }
            if (t.Is("WHERE"))
            {
                ctx.Warn("Gefilterter Index (WHERE) → funktionsbasierter Index mit CASE-Ausdruck umsetzen.");
                int end = Tok.FindTopLevel(tk, i + 1, x => x.IsAny("WITH", "ON"));
                i = end < 0 ? tk.Count : end - 1;
                continue;
            }
            if (t.Is("WITH") && Tok.NextSig(tk, i + 1) is var w && w < tk.Count && tk[w].IsSym("("))
            {
                i = Tok.MatchParen(tk, w);
                continue;
            }
            if (t.Is("ON"))
            {
                if (seenOn) { i = Tok.NextSig(tk, i + 1); continue; }
                seenOn = true;
            }
            res.Add(t);
        }
        return Ddl(Collapse(ex.Convert(Tok.Trim(res))));
    }

    string CreateView(List<Token> tk)
    {
        int viewIdx = Tok.FindTopLevel(tk, 0, x => x.Is("VIEW"));
        int asIdx = Tok.FindTopLevel(tk, viewIdx + 1, x => x.Is("AS"));
        if (asIdx < 0) return Ddl(Cv(tk));
        var head = tk.GetRange(viewIdx + 1, asIdx - viewIdx - 1);
        int withIdx = Tok.FindTopLevel(head, 0, x => x.Is("WITH"));
        if (withIdx >= 0) head = head.GetRange(0, withIdx);
        var query = Tok.Trim(tk.GetRange(asIdx + 1, tk.Count - asIdx - 1));
        var h = Collapse(ex.Convert(Tok.Trim(head)));
        curIndent = 0;
        var q = Cv(query);
        return Ddl($"CREATE OR REPLACE VIEW {h} AS\n{q}");
    }

    List<Token> RemoveSequenceType(List<Token> tk)
    {
        int asIdx = Tok.FindTopLevel(tk, 0, x => x.Is("AS"));
        if (asIdx < 0) return tk;
        int typeIdx = Tok.NextSig(tk, asIdx + 1);
        var res = new List<Token>(tk.GetRange(0, asIdx));
        res.AddRange(tk.GetRange(typeIdx + 1, tk.Count - typeIdx - 1));
        return res;
    }

    string AlterTable(List<Token> tk)
    {
        var sig = Tok.Sig(tk);
        // Tabellenname
        int nameStart = tk.IndexOf(sig[2]);
        var chain = new List<Token> { tk[nameStart] };
        int i = nameStart + 1;
        while (i + 1 < tk.Count && tk[i].IsSym(".")) { chain.Add(tk[i]); chain.Add(tk[i + 1]); i += 2; }
        var table = chain[0].Kind == TokenKind.TempTable ? ex.TempName(chain[0].Text) : ex.RenderName(chain);
        int p = Tok.NextSig(tk, i);
        if (p < tk.Count && tk[p].Is("WITH")) p = Tok.NextSig(tk, Tok.NextSig(tk, p + 1) + 1); // WITH CHECK/NOCHECK
        if (p >= tk.Count) return Ddl(Cv(tk));
        var action = tk[p];
        var rest = Tok.Trim(tk.GetRange(p + 1, tk.Count - p - 1));
        var rsig = Tok.Sig(rest);

        if (action.Is("ADD"))
        {
            if (rsig.Count > 0 && rsig[0].IsAny("CONSTRAINT", "PRIMARY", "UNIQUE", "FOREIGN", "CHECK"))
            {
                int defIdx = rsig.FindIndex(x => x.Is("DEFAULT"));
                int forIdx = rsig.FindIndex(x => x.Is("FOR"));
                if (defIdx >= 0 && forIdx > defIdx)
                {
                    var expr = rsig.GetRange(defIdx + 1, forIdx - defIdx - 1);
                    var col = ex.Ident(rsig[forIdx + 1]);
                    return Ddl($"ALTER TABLE {table} MODIFY ({col} DEFAULT {Collapse(ex.Convert(SpaceOut(expr)))})");
                }
                return Ddl($"ALTER TABLE {table} ADD {ConvertConstraint(rest)}");
            }
            var cols = Tok.SplitTopLevel(rest).Select(Tok.Trim).Where(c => c.Count > 0)
                .Select(ConvertColumnOrConstraint).Where(c => c != null);
            return Ddl($"ALTER TABLE {table} ADD ({string.Join(", ", cols)})");
        }
        if (action.Is("ALTER") && rsig.Count > 0 && rsig[0].Is("COLUMN"))
        {
            int c = rest.IndexOf(rsig[0]);
            var colDef = ConvertColumnOrConstraint(Tok.Trim(rest.GetRange(c + 1, rest.Count - c - 1)));
            ctx.Warn("ALTER COLUMN → MODIFY: NOT NULL auf bereits NOT-NULL-Spalte erzeugt ORA-01442.");
            return Ddl($"ALTER TABLE {table} MODIFY ({colDef})");
        }
        if (action.Is("DROP"))
        {
            if (rsig.Count > 0 && rsig[0].Is("COLUMN"))
            {
                var names = rsig.Skip(1).Where(x => x.IsName && !x.IsAny("IF", "EXISTS")).Select(ex.Ident).ToList();
                return Ddl(names.Count == 1
                    ? $"ALTER TABLE {table} DROP COLUMN {names[0]}"
                    : $"ALTER TABLE {table} DROP ({string.Join(", ", names)})");
            }
            if (rsig.Count > 1 && rsig[0].Is("CONSTRAINT"))
                return Ddl($"ALTER TABLE {table} DROP CONSTRAINT {ex.Ident(rsig.Last(x => x.IsName))}");
        }
        ctx.Warn("ALTER TABLE-Variante nicht speziell behandelt – prüfen.");
        return Ddl(Cv(tk));
    }

    string DropStmt(List<Token> tk)
    {
        var sig = Tok.Sig(tk);
        var kind = sig.Count > 1 ? sig[1].Upper : "";
        if (kind == "PROC") kind = "PROCEDURE";
        int k = 2;
        bool ifExists = false;
        if (k + 1 < sig.Count && sig[k].Is("IF") && sig[k + 1].Is("EXISTS")) { ifExists = true; k += 2; }

        // Objektnamen (durch Komma getrennt), DROP INDEX ix ON t
        var names = new List<string>();
        var cur = new List<Token>();
        for (; k < sig.Count; k++)
        {
            if (sig[k].IsSym(",")) { if (cur.Count > 0) names.Add(RenderObj(cur)); cur = new(); continue; }
            if (sig[k].Is("ON")) break;
            cur.Add(sig[k]);
        }
        if (cur.Count > 0) names.Add(RenderObj(cur));

        int code = kind switch
        {
            "TABLE" => -942, "VIEW" => -942, "SEQUENCE" => -2289, "INDEX" => -1418,
            "PROCEDURE" or "FUNCTION" or "TRIGGER" or "TYPE" => -4043, _ => -942
        };
        var lines = new List<string>();
        foreach (var n in names)
        {
            if (kind == "TABLE" && S.IsPlSql && ctx.PrereqNames.Contains(n) || (kind == "TABLE" && S.IsPlSql && n.StartsWith("tmp_")))
            {
                lines.Add($"DELETE FROM {n}; -- DROP #temp → GTT leeren");
                continue;
            }
            var stmt = $"DROP {kind} {n}";
            if (ifExists && ctx.Is19c)
            {
                var block = $"BEGIN\n    EXECUTE IMMEDIATE '{stmt}';\nEXCEPTION\n    WHEN OTHERS THEN\n        IF SQLCODE != {code} THEN RAISE; END IF;\nEND;";
                lines.Add(block);
            }
            else lines.Add(Ddl(ifExists ? $"DROP {kind} IF EXISTS {n}" : stmt));
        }
        if (!S.IsPlSql && ifExists && ctx.Is19c) return string.Join("\n/\n", lines);
        return string.Join("\n", lines);
    }

    string RenderObj(List<Token> chain)
    {
        if (chain.Count == 1 && chain[0].Kind == TokenKind.TempTable) return ex.TempName(chain[0].Text);
        return ex.RenderName(chain);
    }
}
