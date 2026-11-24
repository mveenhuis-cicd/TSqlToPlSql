namespace TSqlToPlSql;

internal sealed partial class Emitter
{
    static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "COUNT_BIG", "SUM", "MAX", "MIN", "AVG"
    };

    /// <summary>Konvertiert ein DML/DDL-Statement (inkl. abschließendem Semikolon).</summary>
    string ConvertSql(List<Token> tokens)
    {
        var tk = Tok.Trim(tokens);
        if (tk.Count == 0) return "";
        var sig = Tok.Sig(tk);
        var first = sig[0].Upper;
        var second = sig.Count > 1 ? sig[1].Upper : "";

        switch (first)
        {
            case "SELECT": return ConvertSelect(tk);
            case "WITH":
            {
                var mainIdx = Tok.FindTopLevel(tk, 1, x => x.IsAny("SELECT", "INSERT", "UPDATE", "DELETE", "MERGE"));
                var text = Cv(tk);
                if (mainIdx >= 0 && tk[mainIdx].Is("SELECT")) return ResultSet(text);
                return text + ";";
            }
            case "INSERT": return ConvertInsert(tk);
            case "UPDATE":
            {
                tk = RemoveTopForDml(tk);
                int setIdx = Tok.FindTopLevel(tk, 1, x => x.Is("SET"));
                if (setIdx >= 0 && Tok.FindTopLevel(tk, setIdx, x => x.Is("FROM")) >= 0)
                {
                    var merge = UpdateFromToMerge(tk, setIdx);
                    if (merge != null) return merge;
                    ctx.Warn("UPDATE ... FROM (Join-Update) → MERGE oder korrelierte Unterabfrage verwenden.");
                    return "-- TODO: UPDATE ... FROM → MERGE INTO ... USING ... umschreiben\n" + Cv(tk) + ";";
                }
                if (Tok.FindTopLevel(tk, 1, x => x.Is("OUTPUT")) >= 0) ctx.Warn("OUTPUT-Klausel → RETURNING ... INTO verwenden.");
                return Cv(tk) + ";";
            }
            case "DELETE":
            {
                tk = RemoveTopForDml(tk);
                var del = DeleteJoinToExists(tk);
                if (del != null) return del;
                if (Tok.FindTopLevel(tk, 1, x => x.Is("OUTPUT")) >= 0) ctx.Warn("OUTPUT-Klausel → RETURNING ... INTO verwenden.");
                return Cv(tk) + ";";
            }
            case "MERGE": return ConvertMerge(tk);
            case "TRUNCATE": return Ddl(Cv(tk));
            case "CREATE":
                if (second == "TABLE") return CreateTable(tk);
                if (second is "INDEX" or "UNIQUE" or "CLUSTERED" or "NONCLUSTERED") return CreateIndex(tk);
                if (second == "VIEW" || (second == "OR" && sig.Count > 3 && sig[3].Is("VIEW"))) return CreateView(tk);
                if (second == "SEQUENCE") return Ddl(Cv(RemoveSequenceType(tk)));
                if (second is "SCHEMA" or "TYPE" or "SYNONYM")
                {
                    ctx.Warn($"CREATE {second} muss manuell umgesetzt werden.");
                    return "-- TODO: " + Collapse(Tok.Text(tk));
                }
                return Ddl(Cv(tk));
            case "ALTER":
                if (second == "TABLE") return AlterTable(tk);
                ctx.Warn($"ALTER {second} manuell prüfen.");
                return Ddl(Cv(tk));
            case "DROP": return DropStmt(tk);
            case "GRANT":
            case "REVOKE":
            case "DENY":
                ctx.Warn("Berechtigungen (GRANT/REVOKE/DENY) manuell prüfen – Rollen/Schemata unterscheiden sich.");
                return first == "DENY" ? "-- TODO: DENY gibt es in Oracle nicht: " + Collapse(Tok.Text(tk)) : Ddl(Cv(tk));
            default:
                ctx.Warn($"Statement '{first}' nicht speziell behandelt – Ergebnis prüfen.");
                return Cv(tk) + ";";
        }
    }

    /// <summary>UPDATE/DELETE TOP (n) → zusätzliche Bedingung ROWNUM &lt;= n.</summary>
    List<Token> RemoveTopForDml(List<Token> tk)
    {
        int p = Tok.NextSig(tk, 1);
        if (p >= tk.Count || !tk[p].Is("TOP")) return tk;
        int q = Tok.NextSig(tk, p + 1);
        int end = q;
        List<Token> n;
        if (tk[q].IsSym("(")) { end = Tok.MatchParen(tk, q); n = tk.GetRange(q + 1, end - q - 1); }
        else n = new List<Token> { tk[q] };
        var res = new List<Token>(tk.GetRange(0, p));
        int after = end + 1;
        while (after < tk.Count && tk[after].Kind == TokenKind.Whitespace) after++;
        if (after < tk.Count && tk[after].Is("PERCENT")) { ctx.Warn("DELETE/UPDATE TOP (n) PERCENT nicht unterstützt."); after++; }
        res.AddRange(tk.GetRange(after, tk.Count - after));
        var nText = Collapse(ex.Convert(n));
        int w = Tok.FindTopLevel(res, 1, x => x.Is("WHERE"));
        if (w >= 0)
        {
            var cond = Tok.Trim(res.GetRange(w + 1, res.Count - w - 1));
            var head = res.GetRange(0, w + 1);
            head.Add(Token.Space());
            head.Add(Token.Raw("("));
            head.AddRange(cond);
            head.Add(Token.Raw($") AND ROWNUM <= {nText}"));
            return head;
        }
        res.Add(Token.Space());
        res.Add(Token.Raw($"WHERE ROWNUM <= {nText}"));
        return res;
    }

    sealed record TableRef(string Text, string? Alias, string Name);

    TableRef? ParseTableRef(List<Token> tokens)
    {
        var t = Tok.Trim(tokens);
        if (t.Count == 0) return null;
        string text, name;
        int i;
        if (t[0].IsSym("("))
        {
            int c = Tok.MatchParen(t, 0);
            text = "(" + ex.Convert(t.GetRange(1, c - 1)).Trim() + ")";
            name = "";
            i = c + 1;
        }
        else
        {
            var chain = new List<Token> { t[0] };
            i = 1;
            while (i + 1 < t.Count && t[i].IsSym(".")) { chain.Add(t[i]); chain.Add(t[i + 1]); i += 2; }
            name = chain[0].Kind == TokenKind.TempTable ? ex.TempName(chain[0].Text) : ex.RenderName(chain);
            text = name;
        }
        var rest = Tok.Sig(t.GetRange(Math.Min(i, t.Count), t.Count - Math.Min(i, t.Count)));
        int k = 0;
        if (k < rest.Count && rest[k].Is("AS")) k++;
        string? alias = null;
        if (k < rest.Count && rest[k].IsName && !rest[k].Is("WITH")) { alias = ex.Ident(rest[k]); k++; }
        // Tabellen-Hints (WITH (NOLOCK)) ignorieren; alles andere → nicht unterstützt
        if (k < rest.Count && !rest[k].Is("WITH") && !rest[k].IsSym("(")) return null;
        return new TableRef(text, alias, name);
    }

    /// <summary>Zerlegt "T a [INNER] JOIN S b ON cond" (genau ein Join).</summary>
    (TableRef Left, TableRef Right, List<Token> Cond)? ParseSingleJoin(List<Token> from)
    {
        int j = Tok.FindTopLevel(from, 0, x => x.Is("JOIN"));
        if (j < 0) return null;
        if (Tok.FindTopLevel(from, j + 1, x => x.Is("JOIN") || x.IsSym(",")) >= 0) return null;
        if (Tok.FindTopLevel(from, 0, x => x.IsSym(",")) >= 0) return null;
        var leftTok = from.GetRange(0, j);
        var ls = Tok.Sig(leftTok);
        if (ls.Count > 0 && ls[^1].IsAny("LEFT", "RIGHT", "FULL", "CROSS", "OUTER")) return null;
        if (ls.Count > 0 && ls[^1].Is("INNER")) leftTok = leftTok.GetRange(0, leftTok.LastIndexOf(ls[^1]));
        int on = Tok.FindTopLevel(from, j + 1, x => x.Is("ON"));
        if (on < 0) return null;
        var l = ParseTableRef(leftTok);
        var r = ParseTableRef(from.GetRange(j + 1, on - j - 1));
        if (l == null || r == null) return null;
        return (l, r, Tok.Trim(from.GetRange(on + 1, from.Count - on - 1)));
    }

    static bool Matches(TableRef t, string target) =>
        string.Equals(t.Alias, target, StringComparison.OrdinalIgnoreCase) ||
        (t.Alias == null && string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(t.Name.Split('.').Last(), target, StringComparison.OrdinalIgnoreCase);

    string? UpdateFromToMerge(List<Token> tk, int setIdx)
    {
        int fromIdx = Tok.FindTopLevel(tk, setIdx, x => x.Is("FROM"));
        int whereIdx = Tok.FindTopLevel(tk, fromIdx, x => x.Is("WHERE"));
        var targetSig = Tok.Sig(tk.GetRange(1, setIdx - 1));
        if (targetSig.Count == 0) return null;
        var targetChain = targetSig.Where(x => x.IsName).ToList();
        var target = ex.RenderName(targetSig);
        var sets = Tok.Trim(tk.GetRange(setIdx + 1, fromIdx - setIdx - 1));
        var from = tk.GetRange(fromIdx + 1, (whereIdx < 0 ? tk.Count : whereIdx) - fromIdx - 1);
        var where = whereIdx >= 0 ? Tok.Trim(tk.GetRange(whereIdx + 1, tk.Count - whereIdx - 1)) : null;

        var j = ParseSingleJoin(from);
        if (j == null) return null;
        var (l, r, cond) = j.Value;
        TableRef tgt, src;
        if (Matches(l, target)) { tgt = l; src = r; }
        else if (Matches(r, target)) { tgt = r; src = l; }
        else return null;
        if (tgt.Name.Length == 0) return null;

        ctx.Warn("UPDATE ... FROM ... JOIN → MERGE umgesetzt: Join-Spalten dürfen nicht aktualisiert werden (ORA-38104), die Quelle muss je Zielzeile eindeutig sein (ORA-30926).");
        var sb = new System.Text.StringBuilder();
        sb.Append($"MERGE INTO {tgt.Name}{(tgt.Alias != null ? " " + tgt.Alias : "")}\n");
        sb.Append($"USING {src.Text}{(src.Alias != null ? " " + src.Alias : "")}\n");
        sb.Append($"ON ({Collapse(ex.Convert(cond))})\n");
        sb.Append($"WHEN MATCHED THEN UPDATE SET {Collapse(ex.Convert(sets))}");
        if (where != null) sb.Append($"\n    WHERE {Collapse(ex.Convert(where))}");
        return sb.Append(';').ToString();
    }

    string? DeleteJoinToExists(List<Token> tk)
    {
        // DELETE [FROM] x FROM T a JOIN S b ON cond [WHERE w]
        int f1 = Tok.FindTopLevel(tk, 1, x => x.Is("FROM"));
        int f2 = f1 >= 0 ? Tok.FindTopLevel(tk, f1 + 1, x => x.Is("FROM")) : -1;
        int fromIdx;
        List<Token> targetTok;
        if (f2 >= 0) { fromIdx = f2; targetTok = tk.GetRange(f1 + 1, f2 - f1 - 1); }
        else if (f1 > 0 && Tok.Sig(tk.GetRange(1, f1 - 1)).Count > 0) { fromIdx = f1; targetTok = tk.GetRange(1, f1 - 1); }
        else return null;
        int whereIdx = Tok.FindTopLevel(tk, fromIdx, x => x.Is("WHERE"));
        var target = ex.RenderName(Tok.Sig(targetTok));
        var from = tk.GetRange(fromIdx + 1, (whereIdx < 0 ? tk.Count : whereIdx) - fromIdx - 1);
        var where = whereIdx >= 0 ? Tok.Trim(tk.GetRange(whereIdx + 1, tk.Count - whereIdx - 1)) : null;

        var j = ParseSingleJoin(from);
        TableRef? tgt = null, src = null;
        List<Token>? cond = null;
        if (j != null)
        {
            var (l, r, c) = j.Value;
            if (Matches(l, target)) { tgt = l; src = r; }
            else if (Matches(r, target)) { tgt = r; src = l; }
            cond = c;
        }
        if (tgt == null || src == null || cond == null || tgt.Name.Length == 0)
        {
            ctx.Warn("DELETE ... FROM ... JOIN → DELETE ... WHERE EXISTS (...) manuell umschreiben.");
            return "-- TODO: DELETE mit Join → WHERE EXISTS (...) umschreiben\n" + Cv(tk) + ";";
        }
        ctx.Warn("DELETE ... FROM ... JOIN → DELETE ... WHERE EXISTS (...) umgesetzt.");
        var inner = $"SELECT 1 FROM {src.Text}{(src.Alias != null ? " " + src.Alias : "")} WHERE {Collapse(ex.Convert(cond))}";
        if (where != null) inner += $" AND ({Collapse(ex.Convert(where))})";
        return $"DELETE FROM {tgt.Name}{(tgt.Alias != null ? " " + tgt.Alias : "")}\nWHERE EXISTS ({inner});";
    }

    string Ddl(string text)
    {
        if (S.IsPlSql)
        {
            ctx.Warn("DDL innerhalb von PL/SQL → EXECUTE IMMEDIATE (impliziter COMMIT!).");
            return $"EXECUTE IMMEDIATE '{text.Replace("'", "''")}';";
        }
        return text + ";";
    }

    string ResultSet(string sql)
    {
        if (!S.IsPlSql) return sql + ";";
        if (S.IsFunction)
        {
            ctx.Warn("SELECT ohne INTO in einer Funktion ist in Oracle nicht möglich.");
            return "-- TODO: Ergebnismenge in Funktion\n" + string.Join("\n", sql.Split('\n').Select(l => "-- " + l));
        }
        var c = S.NewName("c_result");
        S.Declare(c, $"{c} SYS_REFCURSOR;");
        ctx.Warn("Ergebnismengen (SELECT ohne INTO) → OPEN cursor FOR ... + DBMS_SQL.RETURN_RESULT (ab Oracle 12c).");
        return $"OPEN {c} FOR\n{IndentBlock(sql, 1)};\nDBMS_SQL.RETURN_RESULT({c});";
    }

    // ================================================================================ SELECT

    /// <summary>Liefert Start (nach SELECT/DISTINCT/TOP) und Ende der Select-Liste.</summary>
    static (int Start, int End) SelectListBounds(List<Token> tk)
    {
        if (tk.Count == 0 || !tk[0].Is("SELECT")) return (-1, -1);
        int p = Tok.NextSig(tk, 1);
        if (p < tk.Count && tk[p].IsAny("ALL", "DISTINCT")) p = Tok.NextSig(tk, p + 1);
        if (p < tk.Count && tk[p].Is("TOP"))
        {
            int q = Tok.NextSig(tk, p + 1);
            if (q < tk.Count && tk[q].IsSym("(")) q = Tok.MatchParen(tk, q);
            p = Tok.NextSig(tk, q + 1);
            if (p < tk.Count && tk[p].Is("PERCENT")) p = Tok.NextSig(tk, p + 1);
            if (p < tk.Count && tk[p].Is("WITH") && tk[Tok.NextSig(tk, p + 1)].Is("TIES")) p = Tok.NextSig(tk, Tok.NextSig(tk, p + 1) + 1);
        }
        int end = Tok.FindTopLevel(tk, p, x => x.IsAny("INTO", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "UNION",
            "EXCEPT", "INTERSECT", "OPTION", "FOR"));
        return (p, end < 0 ? tk.Count : end);
    }

    static List<Token> Join(List<List<Token>> items)
    {
        var res = new List<Token>();
        for (int k = 0; k < items.Count; k++)
        {
            if (k > 0)
            {
                res.Add(Token.Sym(","));
                if (items[k].Count > 0 && !items[k][0].IsTrivia) res.Add(Token.Space());
            }
            res.AddRange(items[k]);
        }
        return res;
    }

    bool IsAggregateOnly(List<List<Token>> exprs, List<Token> rest)
    {
        if (Tok.FindTopLevel(rest, 0, x => x.Is("GROUP")) >= 0) return false;
        return exprs.All(e =>
        {
            var s = Tok.Sig(e);
            for (int k = 0; k + 1 < s.Count; k++)
                if (Aggregates.Contains(s[k].Text) && s[k + 1].IsSym("(")) return true;
            return false;
        });
    }

    /// <summary>SELECT ... INTO ziel FROM ... (für SET @x = (SELECT ...), Unterabfragen usw.).</summary>
    List<string> SelectInto(List<Token> select, List<string> targets)
    {
        var tk = Tok.Trim(select);
        var (ls, le) = SelectListBounds(tk);
        if (ls < 0)
        {
            ctx.Warn("Unterabfrage mit CTE kann nicht automatisch in SELECT INTO umgewandelt werden.");
            return new List<string> { $"-- TODO: SELECT ... INTO {string.Join(", ", targets)}", Cv(tk) + ";" };
        }
        var items = Tok.SplitTopLevel(tk.GetRange(ls, le - ls));
        var rest = tk.GetRange(le, tk.Count - le);
        var nt = new List<Token>();
        nt.AddRange(Tok.Trim(tk.GetRange(0, le)));
        nt.Add(Token.Space());
        nt.Add(Token.Raw("INTO " + string.Join(", ", targets)));
        if (rest.Count > 0) { nt.Add(Token.Space()); nt.AddRange(rest); }
        var lines = new List<string>();
        AddNoDataHint(lines, items, rest);
        lines.Add(Cv(nt) + ";");
        return lines;
    }

    void AddNoDataHint(List<string> lines, List<List<Token>> exprs, List<Token> rest)
    {
        bool hasFrom = rest.Count > 0 && Tok.Sig(rest).FirstOrDefault() is { } f && f.Is("FROM");
        if (!hasFrom || IsAggregateOnly(exprs, rest)) return;
        ctx.Warn("SELECT INTO löst NO_DATA_FOUND/TOO_MANY_ROWS aus (T-SQL: Variable bleibt unverändert bzw. letzter Wert).");
        if (!S.NoDataHintGiven)
        {
            lines.Add("-- Hinweis: SELECT INTO wirft NO_DATA_FOUND, wenn keine Zeile gefunden wird");
            S.NoDataHintGiven = true;
        }
    }

    string ConvertSelect(List<Token> tk)
    {
        var (ls, le) = SelectListBounds(tk);
        var items = Tok.SplitTopLevel(tk.GetRange(ls, le - ls));
        var rest = tk.GetRange(le, tk.Count - le);
        var prefix = tk.GetRange(0, ls);

        // ------------------------------------------------ Variablenzuweisung: SELECT @a = x, @b = y FROM ...
        var assigns = items.Select(ParseAssign).ToList();
        if (assigns.Any(a => a != null))
        {
            if (assigns.Any(a => a == null))
            {
                ctx.Warn("SELECT mischt Variablenzuweisung und Ergebnisspalten – manuell prüfen.");
                return "-- TODO: gemischte SELECT-Zuweisung\n" + Cv(tk) + ";";
            }
            var list = assigns.Select(a => a!.Value).ToList();
            bool hasFrom = rest.Count > 0 && Tok.Sig(rest).FirstOrDefault() is { } f && f.Is("FROM");
            // SELECT @s = @s + ausdruck FROM ... → LISTAGG
            if (hasFrom && list.Count == 1)
            {
                var a0 = list[0];
                var es = Tok.Sig(a0.Expr);
                List<Token>? aggExpr = null;
                if (a0.Op == "+=") aggExpr = a0.Expr;
                else if (a0.Op == "=" && es.Count > 2 && es[0].Kind == TokenKind.Variable &&
                         es[0].Text.Equals(a0.Var, StringComparison.OrdinalIgnoreCase) && es[1].IsSym("+"))
                {
                    int plus = a0.Expr.IndexOf(es[1]);
                    aggExpr = Tok.Trim(a0.Expr.GetRange(plus + 1, a0.Expr.Count - plus - 1));
                }
                if (aggExpr != null && (ex.IsStringy(a0.Expr) || (S.Vars.TryGetValue(a0.Var.TrimStart('@'), out var sv) && sv.IsString)))
                {
                    ctx.Warn("SELECT @s = @s + spalte FROM ... (String-Aggregation) → LISTAGG (max. 4000 Byte, Reihenfolge per WITHIN GROUP festlegen).");
                    var tgt = ex.VarName(a0.Var);
                    var e = Collapse(ex.Convert(aggExpr));
                    var orderIdx = Tok.FindTopLevel(rest, 0, x => x.Is("ORDER"));
                    var order = "NULL";
                    var restNoOrder = rest;
                    if (orderIdx >= 0)
                    {
                        var byIdx = Tok.NextSig(rest, orderIdx + 1);
                        order = Collapse(ex.Convert(rest.GetRange(byIdx + 1, rest.Count - byIdx - 1)));
                        restNoOrder = Tok.Trim(rest.GetRange(0, orderIdx));
                    }
                    var q = Cv(Tok.Trim(prefix).Concat(new[] { Token.Space(), Token.Raw($"{tgt} || LISTAGG({e}) WITHIN GROUP (ORDER BY {order})"),
                        Token.Space(), Token.Raw($"INTO {tgt}"), Token.Space() }).Concat(restNoOrder).ToList());
                    return q + ";";
                }
            }
            if (list.Any(a => a.Op != "="))
            {
                ctx.Warn("SELECT @s += spalte FROM ... (String-Aggregation) → LISTAGG verwenden.");
                if (hasFrom && list.Count == 1 && list[0].Op == "+=")
                {
                    var e = Collapse(ex.Convert(list[0].Expr));
                    var tgt = ex.VarName(list[0].Var);
                    var q = Cv(prefix.Concat(new[] { Token.Raw($"LISTAGG({e}) WITHIN GROUP (ORDER BY NULL)"), Token.Space(),
                        Token.Raw($"INTO {tgt}"), Token.Space() }).Concat(rest).ToList());
                    return $"-- TODO: Reihenfolge/Trennzeichen der Aggregation prüfen (vorheriger Wert von {tgt} geht verloren)\n{q};";
                }
                return "-- TODO: SELECT mit zusammengesetzter Zuweisung\n" + Cv(tk) + ";";
            }
            if (!hasFrom)
            {
                var lines = new List<string>();
                foreach (var a in list)
                {
                    var pre = new List<string>();
                    var tgt = ex.VarName(a.Var);
                    if (Tok.IsParenSubquery(Tok.Trim(a.Expr), out var inner))
                    {
                        lines.AddRange(SelectInto(inner, new List<string> { tgt }));
                        continue;
                    }
                    var v = Collapse(ex.Convert(Hoist(a.Expr, pre)));
                    lines.AddRange(pre);
                    lines.Add($"{tgt} := {v};");
                }
                return string.Join("\n", lines);
            }
            var exprs = list.Select(a => a.Expr).ToList();
            var nt = new List<Token>(Tok.Trim(prefix));
            nt.Add(Token.Space());
            nt.AddRange(Join(exprs));
            nt.Add(Token.Space());
            nt.Add(Token.Raw("INTO " + string.Join(", ", list.Select(a => ex.VarName(a.Var)))));
            nt.Add(Token.Space());
            nt.AddRange(rest);
            var hint = new List<string>();
            AddNoDataHint(hint, exprs, rest);
            hint.Add(Cv(nt) + ";");
            return string.Join("\n", hint);
        }

        // ------------------------------------------------ Alias-Syntax: SELECT Name = Ausdruck
        var newItems = items.Select(RewriteAlias).ToList();

        // ------------------------------------------------ SELECT ... INTO neue_tabelle FROM ...
        if (rest.Count > 0 && Tok.Sig(rest)[0].Is("INTO"))
        {
            int intoIdx = rest.IndexOf(Tok.Sig(rest)[0]);
            int p = Tok.NextSig(rest, intoIdx + 1);
            var chain = new List<Token> { rest[p] };
            int k = p + 1;
            while (k + 1 < rest.Count && rest[k].IsSym(".")) { chain.Add(rest[k]); chain.Add(rest[k + 1]); k += 2; }
            var restAfter = rest.GetRange(k, rest.Count - k);
            bool temp = chain[0].Kind == TokenKind.TempTable;
            var target = temp ? ex.TempName(chain[0].Text) : ex.RenderName(chain);
            var sel = Cv(prefix.Concat(Join(newItems)).Concat(restAfter).ToList());
            if (temp && S.IsPlSql)
            {
                ctx.Warn("SELECT ... INTO #tmp → Global Temporary Table (Vorab-DDL) + INSERT ... SELECT.");
                AddPrereq(target, $"CREATE GLOBAL TEMPORARY TABLE {target} ON COMMIT PRESERVE ROWS AS\nSELECT * FROM (\n{IndentBlock(sel, 1)}\n) WHERE 1 = 0;");
                return $"DELETE FROM {target};\nINSERT INTO {target}\n{sel};";
            }
            ctx.Warn("SELECT ... INTO <tabelle> → CREATE TABLE ... AS SELECT.");
            var ddl = temp
                ? $"CREATE GLOBAL TEMPORARY TABLE {target} ON COMMIT PRESERVE ROWS AS\n{sel}"
                : $"CREATE TABLE {target} AS\n{sel}";
            return Ddl(ddl);
        }

        var sql = Cv(prefix.Concat(Join(newItems)).Concat(rest).ToList());
        return ResultSet(sql);
    }

    (string Var, string Op, List<Token> Expr)? ParseAssign(List<Token> item)
    {
        var t = Tok.Trim(item);
        var sig = Tok.Sig(t);
        if (sig.Count >= 3 && sig[0].Kind == TokenKind.Variable && sig[1].Kind == TokenKind.Symbol &&
            sig[1].Text is "=" or "+=" or "-=" or "*=" or "/=")
        {
            int opIdx = t.IndexOf(sig[1]);
            return (sig[0].Text, sig[1].Text, Tok.Trim(t.GetRange(opIdx + 1, t.Count - opIdx - 1)));
        }
        return null;
    }

    List<Token> RewriteAlias(List<Token> item)
    {
        var t = Tok.Trim(item);
        var sig = Tok.Sig(t);
        if (sig.Count >= 3 && (sig[0].IsName || sig[0].Kind is TokenKind.String) && sig[1].IsSym("="))
        {
            int eq = t.IndexOf(sig[1]);
            var lead = item.TakeWhile(x => x.IsTrivia).ToList();
            var trail = Enumerable.Reverse(item).TakeWhile(x => x.IsTrivia).Reverse().ToList();
            var expr = Tok.Trim(t.GetRange(eq + 1, t.Count - eq - 1));
            var alias = sig[0].Kind == TokenKind.String ? Token.Raw("\"" + sig[0].StringContent + "\"") : sig[0];
            var res = new List<Token>(lead);
            res.AddRange(expr);
            res.Add(Token.Space());
            res.Add(Token.Kw("AS"));
            res.Add(Token.Space());
            res.Add(alias);
            res.AddRange(trail);
            return res;
        }
        return item;
    }

    // ================================================================================ INSERT / MERGE

    string ConvertInsert(List<Token> tk)
    {
        tk = new List<Token>(tk);
        int p = Tok.NextSig(tk, 1);
        if (tk[p].Is("TOP")) ctx.Warn("INSERT TOP(n) wird nicht unterstützt.");
        if (!tk[p].Is("INTO")) tk.InsertRange(1, new[] { Token.Space(), Token.Kw("INTO") });

        if (Tok.FindTopLevel(tk, 1, x => x.IsAny("EXEC", "EXECUTE")) >= 0)
        {
            ctx.Warn("INSERT ... EXEC → Prozedur liefert in Oracle keine Ergebnismenge; als Pipelined Function/Cursor umsetzen.");
            return "-- TODO: INSERT ... EXEC\n" + string.Join("\n", Tok.Text(tk).Split('\n').Select(l => "-- " + l));
        }
        if (Tok.FindTopLevel(tk, 1, x => x.Is("OUTPUT")) >= 0) ctx.Warn("OUTPUT-Klausel → RETURNING ... INTO verwenden.");

        int dv = Tok.FindTopLevel(tk, 1, x => x.Is("DEFAULT"));
        if (dv >= 0 && tk[Tok.NextSig(tk, dv + 1)].Is("VALUES"))
            ctx.Warn("INSERT ... DEFAULT VALUES → INSERT INTO t (spalte) VALUES (DEFAULT).");

        int vIdx = Tok.FindTopLevel(tk, 1, x => x.Is("VALUES"));
        if (vIdx >= 0 && ctx.Is19c)
        {
            var rows = Tok.SplitTopLevel(tk.GetRange(vIdx + 1, tk.Count - vIdx - 1)).Select(Tok.Trim).ToList();
            if (rows.Count > 1 && rows.All(r => r.Count > 0 && r[0].IsSym("(") && Tok.MatchParen(r, 0) == r.Count - 1))
            {
                var head = Cv(tk.GetRange(0, vIdx)).TrimEnd();
                var selects = rows.Select(r => "SELECT " + Collapse(ex.Convert(r.GetRange(1, r.Count - 2))) + " FROM DUAL");
                return head + "\n" + string.Join(" UNION ALL\n", selects) + ";";
            }
        }
        return Cv(tk) + ";";
    }

    string ConvertMerge(List<Token> tk)
    {
        var res = new List<Token>();
        for (int i = 0; i < tk.Count; i++)
        {
            // WHEN NOT MATCHED BY TARGET → WHEN NOT MATCHED
            if (tk[i].Is("BY"))
            {
                int n = Tok.NextSig(tk, i + 1);
                if (n < tk.Count && tk[n].Is("TARGET"))
                {
                    while (res.Count > 0 && res[^1].IsTrivia) res.RemoveAt(res.Count - 1);
                    i = n;
                    continue;
                }
                if (n < tk.Count && tk[n].Is("SOURCE"))
                    ctx.Warn("MERGE ... WHEN NOT MATCHED BY SOURCE gibt es in Oracle nicht → separates DELETE.");
            }
            res.Add(tk[i]);
        }
        if (Tok.FindTopLevel(res, 1, x => x.Is("OUTPUT")) >= 0) ctx.Warn("OUTPUT-Klausel in MERGE wird nicht unterstützt.");
        ctx.Warn("MERGE: In Oracle muss jede WHEN-Klausel eindeutig sein; 'WHEN MATCHED AND ...' → WHERE-Klausel im UPDATE.");
        return Cv(res) + ";";
    }
}
