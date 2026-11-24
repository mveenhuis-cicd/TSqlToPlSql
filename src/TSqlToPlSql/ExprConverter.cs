using System.Text;
using System.Text.RegularExpressions;

namespace TSqlToPlSql;

/// <summary>
/// Wandelt T-SQL-Ausdrücke bzw. SQL-Text (Token-Listen) in Oracle-Syntax um:
/// Funktionen, Variablen, Bezeichner, String-Konkatenation, TOP, FROM DUAL, Hints …
/// </summary>
internal sealed partial class ExprConverter
{
    readonly Ctx ctx;

    public ExprConverter(Ctx ctx) { this.ctx = ctx; }

    UnitScope S => ctx.Scope;

    enum UK { Trivia, Paren, Func, Case, Name, Var, SysVar, Temp, Str, NStr, Num, Sym, Kw, Raw }

    sealed class Unit
    {
        public UK Kind;
        public string Text = "";
        public List<Token> Tokens = new();
        public List<Token> Inner = new();
        public string FuncName = "";
        public string Gap = "";
        public string Suffix = "";
        public bool IsSig => Kind != UK.Trivia && !(Kind == UK.Raw && Text.Length == 0 && Suffix.Length == 0);
        public bool IsKw(string k) => Kind == UK.Kw && Text.Equals(k, StringComparison.OrdinalIgnoreCase);
        public bool IsSym(string s) => Kind == UK.Sym && Text == s;
    }

    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","AND","OR","NOT","IN","IS","NULL","LIKE","BETWEEN","EXISTS","AS","ON","JOIN",
        "INNER","LEFT","RIGHT","FULL","OUTER","CROSS","APPLY","GROUP","BY","ORDER","HAVING","UNION","ALL",
        "DISTINCT","TOP","INTO","VALUES","SET","UPDATE","DELETE","INSERT","WHEN","THEN","ELSE","END","WITH",
        "OVER","PARTITION","ASC","DESC","OFFSET","FETCH","NEXT","ROWS","ROW","ONLY","PERCENT","TIES","ANY",
        "SOME","EXCEPT","INTERSECT","MINUS","MERGE","USING","MATCHED","OUTPUT","DEFAULT","COLLATE","VALUE","FOR",
        "OPTION","WITHIN","ESCAPE","CURRENT_USER","SYSTEM_USER","SESSION_USER","CURRENT_TIMESTAMP","PIVOT",
        "UNPIVOT","PRECEDING","FOLLOWING","UNBOUNDED","CURRENT","RANGE","TARGET","SOURCE"
    };

    static readonly HashSet<string> NonFunc = new(StringComparer.OrdinalIgnoreCase)
    {
        "IN","EXISTS","VALUES","AS","AND","OR","NOT","ON","OVER","INTO","FROM","JOIN","APPLY","WHERE","SELECT",
        "WHEN","THEN","ELSE","ALL","ANY","SOME","UNION","TABLE","KEY","UNIQUE","CHECK","INCLUDE","WITH","USING",
        "OUTPUT","DEFAULT","IS","LIKE","BETWEEN","RETURN","PRINT","IF","WHILE","SET","BY","TOP","EXCEPT",
        "INTERSECT","OPTION","CROSS","OUTER","GROUP","FOR","PIVOT","UNPIVOT","MERGE","UPDATE","INSERT","DELETE",
        "EXEC","EXECUTE","HAVING","ORDER","CASE","END","IDENTITY","REFERENCES","FOREIGN","PRIMARY"
    };

    static readonly HashSet<string> OperandKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL","CURRENT_TIMESTAMP","CURRENT_USER","SYSTEM_USER","SESSION_USER"
    };

    static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","GROUP","ORDER","HAVING","JOIN","APPLY","ON","SET","VALUES","USING","INTO",
        "UPDATE","MERGE","UNION","WITH","DELETE","INSERT","WHEN","OUTPUT","EXCEPT","INTERSECT"
    };

    static readonly HashSet<string> TableHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOLOCK","READUNCOMMITTED","ROWLOCK","UPDLOCK","HOLDLOCK","READPAST","TABLOCK","TABLOCKX","PAGLOCK",
        "INDEX","NOEXPAND","XLOCK","SERIALIZABLE","REPEATABLEREAD","READCOMMITTED","FORCESEEK","FORCESCAN","SNAPSHOT"
    };

    /// <summary>Häufige Spaltennamen, die in Oracle reservierte Wörter sind und daher gequotet werden müssen.</summary>
    static readonly HashSet<string> OracleReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEVEL","COMMENT","NUMBER","DATE","FILE","MODE","UID","ROWID","ROWNUM","RESOURCE","SESSION","SHARE",
        "START","ACCESS","AUDIT","CLUSTER","COLUMN","COMPRESS","INITIAL","LOCK","LONG","ONLINE","OFFLINE","RAW",
        "SUCCESSFUL","SYNONYM","VALIDATE","INCREMENT","MODIFY","RENAME","EXCLUSIVE","PUBLIC","SYSDATE","SIZE",
        "VARCHAR2","OPTION","PRIOR","CONNECT","TRIGGER","PRIVILEGES","IMMEDIATE","IDENTIFIED","MAXEXTENTS"
    };

    static readonly HashSet<string> StringFuncs = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUBSTRING","LEFT","RIGHT","UPPER","LOWER","LTRIM","RTRIM","TRIM","REPLACE","CONCAT","CONCAT_WS","STUFF",
        "REPLICATE","SPACE","CHAR","NCHAR","QUOTENAME","FORMAT","STR","REVERSE","STRING_AGG","DB_NAME",
        "OBJECT_NAME","SUSER_SNAME","USER_NAME","DATENAME","ERROR_MESSAGE","ERROR_PROCEDURE","TRANSLATE","SUBSTR",
        "NVL","LPAD","RPAD","TO_CHAR","HOST_NAME","APP_NAME","SUSER_NAME"
    };

    // =====================================================================================================
    // Öffentliche API
    // =====================================================================================================

    public string Convert(List<Token> tokens)
    {
        var units = BuildUnits(tokens);
        ApplyQueryRewrites(units);
        return Render(units);
    }

    public string RenderName(List<Token> chain)
    {
        var parts = chain.Where(x => !x.IsSym(".")).ToList();
        if (ctx.Options.RemoveDboSchema)
        {
            if (parts.Count >= 3 && parts[1].Name.Equals("dbo", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Warn($"Datenbank-Präfix '{parts[0].Name}' entfernt (ggf. Schema/DB-Link verwenden).");
                parts = parts.Skip(2).ToList();
            }
            else if (parts.Count >= 2 && parts[0].Name.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                parts = parts.Skip(1).ToList();
        }
        return string.Join(".", parts.Select(Ident));
    }

    public string Ident(Token t)
    {
        if (t.IsSym("*")) return "*";
        if (t.Kind == TokenKind.TempTable) return TempName(t.Text);
        var name = t.Name;
        bool simple = Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_$#]*$");
        if (OracleReserved.Contains(name))
        {
            ctx.Warn($"Bezeichner '{name}' ist in Oracle reserviert → als \"{name.ToUpperInvariant()}\" gequotet.");
            return "\"" + name.ToUpperInvariant() + "\"";
        }
        if (t.Kind == TokenKind.QuotedIdentifier && !simple) return "\"" + name.Replace("\"", "\"\"") + "\"";
        if (name.Length > 128) ctx.Warn($"Bezeichner '{name}' ist länger als 128 Zeichen.");
        return name;
    }

    public string TempName(string text)
    {
        ctx.Warn("Temporäre Tabellen (#tmp) → Global Temporary Tables 'tmp_…' (einmalig per DDL anlegen, nicht im Code).");
        return "tmp_" + text.TrimStart('#');
    }

    public string VarName(string tsqlName)
    {
        var n = tsqlName.TrimStart('@');
        if (S.Vars.TryGetValue(n, out var vi)) return vi.OraName;
        if (S.IsPlSql) ctx.Warn($"Variable @{n} ist nicht deklariert (als {ctx.Options.LocalVariablePrefix}{n} übernommen).");
        return ctx.Options.LocalVariablePrefix + n;
    }

    public bool IsStringy(List<Token> tokens)
    {
        var units = BuildUnits(tokens);
        var sig = units.Where(u => u.IsSig).ToList();
        if (sig.Count == 0 || sig[0].IsKw("SELECT")) return false;
        return sig.Any(IsStringy);
    }

    // =====================================================================================================
    // Units
    // =====================================================================================================

    List<Unit> BuildUnits(List<Token> tk)
    {
        var units = new List<Unit>();
        int i = 0, n = tk.Count;
        Token? lastSig = null;
        while (i < n)
        {
            var t = tk[i];
            if (t.IsEof) { i++; continue; }
            if (!t.IsTrivia && units.Count > 0) lastSig = LastSigToken(units) ?? lastSig;
            if (t.IsTrivia)
            {
                // Zeilenkommentar braucht danach einen Zeilenumbruch
                var txt = t.Kind == TokenKind.LineComment && (i + 1 >= n || tk[i + 1].Kind != TokenKind.Whitespace)
                    ? t.Text + "\n" : t.Text;
                units.Add(new Unit { Kind = UK.Trivia, Text = txt });
                i++;
                continue;
            }
            if (t.IsSym("("))
            {
                int j = Tok.MatchParen(tk, i);
                units.Add(new Unit { Kind = UK.Paren, Inner = tk.GetRange(i + 1, Math.Max(0, Math.Min(j, n) - i - 1)) });
                i = j + 1;
                continue;
            }
            if (t.Is("CASE"))
            {
                int j = Tok.MatchCase(tk, i);
                units.Add(new Unit { Kind = UK.Case, Inner = tk.GetRange(i + 1, Math.Max(0, Math.Min(j, n) - i - 1)) });
                i = j + 1;
                continue;
            }
            if (t.IsName)
            {
                int j = i;
                var chain = new List<Token> { t };
                while (j + 2 < n && tk[j + 1].IsSym(".") && (tk[j + 2].IsName || tk[j + 2].IsSym("*")))
                {
                    chain.Add(tk[j + 1]);
                    chain.Add(tk[j + 2]);
                    j += 2;
                }
                int m = j + 1;
                string gap = "";
                if (m < n && tk[m].Kind == TokenKind.Whitespace) { gap = tk[m].Text; m++; }
                var last = chain[^1];
                bool keywordBeforeParen = chain.Count == 1 && last.Kind == TokenKind.Identifier && NonFunc.Contains(last.Upper);
                // Tabellenname vor Spaltenliste: INSERT INTO log (msg), REFERENCES t (id), ON t (col)
                bool tableContext = lastSig != null && lastSig.Kind == TokenKind.Identifier &&
                                    lastSig.IsAny("INTO", "TABLE", "REFERENCES", "JOIN", "FROM", "UPDATE", "MERGE", "USING", "VIEW", "INDEX");
                if (m < n && tk[m].IsSym("(") && last.IsName && !keywordBeforeParen && tableContext)
                {
                    units.Add(new Unit { Kind = UK.Name, Tokens = chain });
                    i = j + 1;
                    continue;
                }
                if (m < n && tk[m].IsSym("(") && last.IsName && !keywordBeforeParen)
                {
                    int close = Tok.MatchParen(tk, m);
                    units.Add(new Unit
                    {
                        Kind = UK.Func, Tokens = chain, FuncName = last.Name.ToUpperInvariant(), Gap = gap,
                        Inner = tk.GetRange(m + 1, Math.Max(0, Math.Min(close, n) - m - 1))
                    });
                    i = close + 1;
                    continue;
                }
                if (chain.Count == 1 && t.Kind == TokenKind.Identifier && Keywords.Contains(t.Upper))
                    units.Add(new Unit { Kind = UK.Kw, Text = t.Text, Tokens = chain });
                else
                    units.Add(new Unit { Kind = UK.Name, Tokens = chain });
                i = j + 1;
                continue;
            }
            var kind = t.Kind switch
            {
                TokenKind.Variable => UK.Var,
                TokenKind.SystemVariable => UK.SysVar,
                TokenKind.TempTable => UK.Temp,
                TokenKind.String => UK.Str,
                TokenKind.NString => UK.NStr,
                TokenKind.Number => UK.Num,
                TokenKind.Raw => UK.Raw,
                _ => UK.Sym
            };
            units.Add(new Unit { Kind = kind, Text = t.Text, Tokens = new List<Token> { t } });
            i++;
        }
        return units;
    }

    static Token? LastSigToken(List<Unit> units)
    {
        for (int k = units.Count - 1; k >= 0; k--)
        {
            var u = units[k];
            if (u.Kind == UK.Trivia) continue;
            if (u.Kind is UK.Kw or UK.Name) return u.Tokens[^1];
            return new Token(TokenKind.Symbol, u.Kind.ToString());
        }
        return null;
    }

    static void Remove(List<Unit> units, int idx)
    {
        units[idx].Kind = UK.Raw;
        units[idx].Text = "";
        if (idx + 1 < units.Count && units[idx + 1].Kind == UK.Trivia && units[idx + 1].Text.Trim().Length == 0)
            units[idx + 1].Text = "";
    }

    /// <summary>TOP → FETCH FIRST, SELECT ohne FROM → FROM DUAL.</summary>
    void ApplyQueryRewrites(List<Unit> units)
    {
        var sig = new List<int>();
        for (int i = 0; i < units.Count; i++) if (units[i].IsSig) sig.Add(i);

        for (int s = 0; s < sig.Count; s++)
        {
            if (!units[sig[s]].IsKw("SELECT")) continue;
            int e = s + 1;
            while (e < sig.Count)
            {
                var x = units[sig[e]];
                if (x.IsKw("UNION") || x.IsKw("EXCEPT") || x.IsKw("INTERSECT") || x.IsKw("MINUS") || x.IsKw("SELECT")) break;
                e++;
            }
            int p = s + 1;
            string? fetch = null;
            if (p < e && (units[sig[p]].IsKw("ALL") || units[sig[p]].IsKw("DISTINCT"))) p++;
            if (p + 1 < e && units[sig[p]].IsKw("TOP"))
            {
                var nu = units[sig[p + 1]];
                var nExpr = nu.Kind == UK.Paren ? Convert(nu.Inner).Trim() : Render(new List<Unit> { nu });
                Remove(units, sig[p]);
                Remove(units, sig[p + 1]);
                int q = p + 2;
                bool percent = false, ties = false;
                if (q < e && units[sig[q]].IsKw("PERCENT")) { percent = true; Remove(units, sig[q]); q++; }
                if (q + 1 < e && units[sig[q]].IsKw("WITH") && units[sig[q + 1]].IsKw("TIES"))
                {
                    ties = true;
                    Remove(units, sig[q]);
                    Remove(units, sig[q + 1]);
                }
                fetch = percent && !ties && nExpr == "100"
                    ? null   // TOP 100 PERCENT ist wirkungslos
                    : $" FETCH FIRST {nExpr}{(percent ? " PERCENT" : "")} ROWS {(ties ? "WITH TIES" : "ONLY")}";
            }
            bool hasFrom = false;
            for (int k = s + 1; k < e; k++) if (units[sig[k]].IsKw("FROM")) hasFrom = true;
            var append = "";
            if (!hasFrom && ctx.Is19c) append += " FROM DUAL";
            if (fetch != null) append += fetch;
            if (append.Length > 0)
            {
                for (int k = e - 1; k >= s; k--)
                {
                    if (units[sig[k]].IsSig) { units[sig[k]].Suffix += append; break; }
                }
            }
            s = e - 1;
        }
    }

    bool IsOperand(Unit u) => u.Kind switch
    {
        UK.Paren or UK.Func or UK.Case or UK.Name or UK.Var or UK.SysVar or UK.Temp or UK.Str or UK.NStr or UK.Num => true,
        UK.Raw => u.Text.Length > 0,
        UK.Kw => OperandKeywords.Contains(u.Text),
        _ => false
    };

    bool IsStringy(Unit u)
    {
        switch (u.Kind)
        {
            case UK.Str:
            case UK.NStr:
                return true;
            case UK.Var:
                return S.Vars.TryGetValue(u.Text.TrimStart('@'), out var vi) && vi.IsString;
            case UK.Func:
                if (u.FuncName is "CAST" or "TRY_CAST")
                {
                    var arg = u.Inner;
                    int asIdx = Tok.FindTopLevel(arg, 0, x => x.Is("AS"));
                    return asIdx >= 0 && TypeMapper.Map(arg.GetRange(asIdx + 1, arg.Count - asIdx - 1), TypeContext.Cast, ctx).IsString;
                }
                if (u.FuncName is "CONVERT" or "TRY_CONVERT")
                {
                    var a = Tok.SplitTopLevel(u.Inner);
                    return TypeMapper.Map(a[0], TypeContext.Cast, ctx).IsString;
                }
                if (u.FuncName is "ISNULL" or "COALESCE" or "NULLIF" or "IIF")
                    return Tok.SplitTopLevel(u.Inner).Any(IsStringy);
                return StringFuncs.Contains(u.FuncName);
            case UK.Paren:
                return IsStringy(u.Inner);
            case UK.Case:
            {
                var sig = Tok.Sig(u.Inner);
                for (int i = 0; i + 1 < sig.Count; i++)
                    if (sig[i].IsAny("THEN", "ELSE") && sig[i + 1].Kind is TokenKind.String or TokenKind.NString) return true;
                return false;
            }
            default:
                return false;
        }
    }

    static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

    // =====================================================================================================
    // Rendering
    // =====================================================================================================

    string Render(List<Unit> units)
    {
        var sig = new List<int>();
        for (int i = 0; i < units.Count; i++) if (units[i].IsSig) sig.Add(i);
        var sigPos = new Dictionary<int, int>();
        for (int k = 0; k < sig.Count; k++) sigPos[sig[k]] = k;

        // String-Konkatenation: + innerhalb einer Operandenkette mit mindestens einem String-Operanden → ||
        var concat = new HashSet<int>();
        for (int a = 0; a < sig.Count;)
        {
            if (!IsOperand(units[sig[a]])) { a++; continue; }
            var ops = new List<int> { sig[a] };
            var plus = new List<int>();
            int b = a;
            while (b + 2 < sig.Count && units[sig[b + 1]].IsSym("+") && IsOperand(units[sig[b + 2]]))
            {
                plus.Add(sig[b + 1]);
                ops.Add(sig[b + 2]);
                b += 2;
            }
            if (plus.Count > 0 && ops.Any(o => IsStringy(units[o]))) foreach (var p in plus) concat.Add(p);
            a = b + 1;
        }

        int NextSigIdx(int idx, int offset = 1)
        {
            int k = sigPos[idx] + offset;
            return k < sig.Count ? sig[k] : -1;
        }
        Unit? NextSig(int idx, int offset = 1)
        {
            int j = NextSigIdx(idx, offset);
            return j >= 0 ? units[j] : null;
        }

        var sb = new StringBuilder();
        string clause = "";
        Unit? prev = null;

        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.IsSig) { sb.Append(u.Text); continue; }
            string? outp = null;

            // Binäre Operatoren, die in Oracle Funktionen sind: a % b → MOD(a, b), a & b → BITAND(a, b)
            if (IsOperand(u))
            {
                var op = NextSig(i);
                var r = NextSig(i, 2);
                if (op != null && r != null && IsOperand(r) && (op.IsSym("%") || op.IsSym("&")))
                {
                    var fn = op.IsSym("%") ? "MOD" : "BITAND";
                    sb.Append($"{fn}({Render(new List<Unit> { u }).Trim()}, {Render(new List<Unit> { r }).Trim()})");
                    sb.Append(r.Suffix);
                    i = NextSigIdx(i, 2);
                    prev = r;
                    continue;
                }
            }

            switch (u.Kind)
            {
                case UK.Sym:
                    outp = u.Text switch
                    {
                        "+" => concat.Contains(i) ? "||" : "+",
                        "!<" => ">=",
                        "!>" => "<=",
                        _ => u.Text
                    };
                    if (u.Text is "^" or "|" or "~")
                        ctx.Warn($"Bit-Operator '{u.Text}' hat kein direktes Oracle-Pendant (BITAND/UTL_RAW) – manuell prüfen.");
                    break;

                case UK.Kw:
                {
                    var up = u.Text.ToUpperInvariant();
                    var nx = NextSig(i);
                    if (up == "AS" && clause is "FROM" or "JOIN" or "USING" or "MERGE" or "UPDATE" or "INTO" or "APPLY" &&
                        prev != null && prev.Kind is UK.Name or UK.Paren or UK.Func or UK.Temp or UK.Var)
                    {
                        // Oracle erlaubt kein AS vor Tabellen-Aliasen
                        TrimTrailingSpaces(sb);
                        prev = u;
                        continue;
                    }
                    if (up == "WITH" && nx is { Kind: UK.Paren } && Tok.Sig(nx.Inner).FirstOrDefault() is { } h &&
                        TableHints.Contains(h.Name))
                    {
                        TrimTrailingSpaces(sb);
                        i = NextSigIdx(i);
                        continue;
                    }
                    if (up == "OPTION" && nx is { Kind: UK.Paren })
                    {
                        ctx.Warn("Query-Hints OPTION(...) entfernt.");
                        TrimTrailingSpaces(sb);
                        i = NextSigIdx(i);
                        continue;
                    }
                    if (up == "COLLATE" && nx is { Kind: UK.Name })
                    {
                        ctx.Warn("COLLATE entfernt – in Oracle ggf. NLS_SORT/NLS_COMP oder COLLATE (12.2+) nutzen.");
                        TrimTrailingSpaces(sb);
                        i = NextSigIdx(i);
                        continue;
                    }
                    if (up == "NEXT" && nx != null && nx.IsKw("VALUE") && NextSig(i, 2) is { } f && f.IsKw("FOR") &&
                        NextSig(i, 3) is { Kind: UK.Name } seq)
                    {
                        outp = RenderName(seq.Tokens) + ".NEXTVAL" + seq.Suffix;
                        i = NextSigIdx(i, 3);
                        prev = seq;
                        sb.Append(outp);
                        continue;
                    }
                    if (up == "FOR" && nx is { Kind: UK.Name } && nx.Tokens[0].IsAny("XML", "JSON", "BROWSE"))
                        ctx.Warn($"FOR {nx.Tokens[0].Upper} wird nicht unterstützt (Oracle: XMLAGG/XMLELEMENT bzw. JSON_OBJECT/JSON_ARRAYAGG).");
                    if (up is "CURRENT_USER" or "SYSTEM_USER" or "SESSION_USER") outp = "USER";
                    else if (up == "EXCEPT") outp = ctx.Is19c ? "MINUS" : u.Text;
                    else outp = u.Text;

                    if (ClauseKeywords.Contains(up)) clause = up;
                    else if (up is "JOIN") clause = "JOIN";
                    if (up == "APPLY") clause = "APPLY";
                    break;
                }

                case UK.Name:
                    outp = RenderName(u.Tokens);
                    // Tabellen-Hint ohne WITH: t (NOLOCK)
                    if (NextSig(i) is { Kind: UK.Paren } hp)
                    {
                        var hs = Tok.Sig(hp.Inner);
                        if (hs.Count > 0 && hs.All(x => x.IsSym(",") || TableHints.Contains(x.Name)))
                        {
                            sb.Append(outp);
                            i = NextSigIdx(i);
                            prev = u;
                            continue;
                        }
                    }
                    break;

                case UK.Var:
                    outp = VarName(u.Text);
                    break;

                case UK.SysVar:
                    outp = RenderSysVar(units, i, NextSig, NextSigIdx, out int skipTo);
                    if (skipTo > i) { sb.Append(outp); i = skipTo; prev = units[i]; continue; }
                    break;

                case UK.Temp:
                    outp = TempName(u.Text);
                    break;

                case UK.Str:
                case UK.NStr:
                    if (prev != null && prev.IsKw("AS"))
                        outp = "\"" + u.Tokens[0].StringContent.Replace("''", "'") + "\"";
                    else
                    {
                        outp = u.Text;
                        if (u.Tokens[0].StringContent.Length == 0)
                            ctx.Warn("Leerstring '' ist in Oracle NULL – Vergleiche wie = '' bzw. <> '' prüfen (IS NULL).");
                        if (prev != null && prev.IsKw("LIKE") && u.Text.Contains('['))
                            ctx.Warn("LIKE mit [..]-Zeichenklassen wird von Oracle nicht unterstützt → REGEXP_LIKE verwenden.");
                    }
                    break;

                case UK.Num:
                    outp = u.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? $"HEXTORAW('{u.Text[2..]}')"
                        : u.Text;
                    break;

                case UK.Paren:
                    outp = "(" + Convert(u.Inner) + ")";
                    break;

                case UK.Case:
                    outp = "CASE" + Convert(u.Inner) + "END";
                    break;

                case UK.Func:
                    outp = RenderFunc(u);
                    if (u.FuncName == "STRING_AGG" && !(NextSig(i) is { } w && w.IsKw("WITHIN")))
                        outp += " WITHIN GROUP (ORDER BY NULL)";
                    break;

                case UK.Raw:
                    outp = u.Text;
                    break;
            }

            sb.Append(outp).Append(u.Suffix);
            prev = u;
        }
        return sb.ToString();
    }

    string RenderSysVar(List<Unit> units, int i, Func<int, int, Unit?> nextSig, Func<int, int, int> nextSigIdx, out int skipTo)
    {
        skipTo = i;
        var u = units[i];
        var name = u.Text.TrimStart('@').ToUpperInvariant();
        switch (name)
        {
            case "ROWCOUNT": return "SQL%ROWCOUNT";
            case "ERROR":
                ctx.Warn("@@ERROR → SQLCODE: Fehlerbehandlung in Oracle über EXCEPTION-Blöcke umsetzen.");
                return "SQLCODE";
            case "FETCH_STATUS":
            {
                var cur = S.LastCursor ?? "c_cursor";
                var op = nextSig(i, 1);
                var v = nextSig(i, 2);
                if (op != null && v != null)
                {
                    bool neg = v.IsSym("-");
                    var num = neg ? nextSig(i, 3) : v;
                    if (num is { Kind: UK.Num })
                    {
                        var value = (neg ? "-" : "") + num.Text;
                        bool found = (op.Text == "=" && value == "0") || (op.Text is "<>" or "!=" && value != "0");
                        skipTo = nextSigIdx(i, neg ? 3 : 2);
                        return found ? $"{cur}%FOUND" : $"{cur}%NOTFOUND";
                    }
                }
                return $"{cur}%FOUND /* TODO: @@FETCH_STATUS prüfen */";
            }
            case "IDENTITY":
                ctx.Warn("@@IDENTITY/SCOPE_IDENTITY → INSERT ... RETURNING <id> INTO <variable> verwenden.");
                return "NULL /* TODO: @@IDENTITY → RETURNING ... INTO */";
            case "TRANCOUNT":
                ctx.Warn("@@TRANCOUNT hat kein Oracle-Pendant (Transaktionen sind implizit).");
                return "0 /* TODO: @@TRANCOUNT */";
            case "SPID": return "SYS_CONTEXT('USERENV', 'SID')";
            case "SERVERNAME": return "SYS_CONTEXT('USERENV', 'SERVER_HOST')";
            case "VERSION": return "DBMS_DB_VERSION.VERSION";
            case "DATEFIRST":
                ctx.Warn("@@DATEFIRST → NLS_TERRITORY bestimmt den Wochenbeginn in Oracle.");
                return "1 /* TODO: @@DATEFIRST */";
            default:
                ctx.Warn($"Systemvariable {u.Text} wurde nicht umgesetzt.");
                return u.Text + " /* TODO */";
        }
    }
}
