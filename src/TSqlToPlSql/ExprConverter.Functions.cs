using System.Text;
using System.Text.RegularExpressions;

namespace TSqlToPlSql;

internal sealed partial class ExprConverter
{
    static readonly Dictionary<int, string> DateStyles = new()
    {
        [1] = "MM/DD/YY", [101] = "MM/DD/YYYY",
        [2] = "YY.MM.DD", [102] = "YYYY.MM.DD",
        [3] = "DD/MM/YY", [103] = "DD/MM/YYYY",
        [4] = "DD.MM.YY", [104] = "DD.MM.YYYY",
        [5] = "DD-MM-YY", [105] = "DD-MM-YYYY",
        [6] = "DD MON YY", [106] = "DD MON YYYY",
        [7] = "MON DD, YY", [107] = "MON DD, YYYY",
        [8] = "HH24:MI:SS", [108] = "HH24:MI:SS",
        [10] = "MM-DD-YY", [110] = "MM-DD-YYYY",
        [11] = "YY/MM/DD", [111] = "YYYY/MM/DD",
        [12] = "YYMMDD", [112] = "YYYYMMDD",
        [20] = "YYYY-MM-DD HH24:MI:SS", [120] = "YYYY-MM-DD HH24:MI:SS",
        [21] = "YYYY-MM-DD HH24:MI:SS.FF3", [121] = "YYYY-MM-DD HH24:MI:SS.FF3",
        [23] = "YYYY-MM-DD",
        [126] = "YYYY-MM-DD\"T\"HH24:MI:SS.FF3", [127] = "YYYY-MM-DD\"T\"HH24:MI:SS.FF3",
    };

    static readonly Dictionary<string, string> DatePartAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["YEAR"] = "YEAR", ["YY"] = "YEAR", ["YYYY"] = "YEAR",
        ["QUARTER"] = "QUARTER", ["QQ"] = "QUARTER", ["Q"] = "QUARTER",
        ["MONTH"] = "MONTH", ["MM"] = "MONTH", ["M"] = "MONTH",
        ["DAYOFYEAR"] = "DAYOFYEAR", ["DY"] = "DAYOFYEAR", ["Y"] = "DAYOFYEAR",
        ["DAY"] = "DAY", ["DD"] = "DAY", ["D"] = "DAY",
        ["WEEK"] = "WEEK", ["WK"] = "WEEK", ["WW"] = "WEEK",
        ["ISO_WEEK"] = "ISO_WEEK", ["ISOWK"] = "ISO_WEEK", ["ISOWW"] = "ISO_WEEK",
        ["WEEKDAY"] = "WEEKDAY", ["DW"] = "WEEKDAY", ["W"] = "WEEKDAY",
        ["HOUR"] = "HOUR", ["HH"] = "HOUR",
        ["MINUTE"] = "MINUTE", ["MI"] = "MINUTE", ["N"] = "MINUTE",
        ["SECOND"] = "SECOND", ["SS"] = "SECOND", ["S"] = "SECOND",
        ["MILLISECOND"] = "MILLISECOND", ["MS"] = "MILLISECOND",
    };

    /// <summary>Klammert einen Ausdruck, wenn er nicht atomar ist.</summary>
    static string W(string s)
    {
        s = s.Trim();
        if (Regex.IsMatch(s, @"^[\w.$#""%:]+$") || Regex.IsMatch(s, @"^'([^']|'')*'$")) return s;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            int d = 0;
            for (int k = 0; k < s.Length; k++)
            {
                if (s[k] == '(') d++;
                else if (s[k] == ')') { d--; if (d == 0 && k < s.Length - 1) return "(" + s + ")"; }
            }
            return s;
        }
        if (Regex.IsMatch(s, @"^[\w.$]+\(.*\)$", RegexOptions.Singleline) && BalancedCall(s)) return s;
        return "(" + s + ")";
    }

    static bool BalancedCall(string s)
    {
        int open = s.IndexOf('(');
        int d = 0;
        for (int k = open; k < s.Length; k++)
        {
            if (s[k] == '(') d++;
            else if (s[k] == ')') { d--; if (d == 0) return k == s.Length - 1; }
        }
        return false;
    }

    string RenderFunc(Unit u)
    {
        var fnText = u.Tokens.Count > 1 ? RenderName(u.Tokens) : u.Tokens[0].Text;
        if (u.Tokens.Count > 1)
            return $"{fnText}{(u.Gap.Length > 0 ? " " : "")}({Convert(u.Inner)})";

        var raw = Tok.SplitTopLevel(u.Inner).Select(Tok.Trim).ToList();
        if (raw.Count == 1 && raw[0].Count == 0) raw.Clear();
        string A(int k) => k < raw.Count ? Convert(raw[k]).Trim() : "NULL";
        string Generic() => $"{fnText}{(u.Gap.Length == 0 ? "" : u.Gap.Contains('\n') ? u.Gap : " ")}({Convert(u.Inner)})";

        switch (u.FuncName)
        {
            // ---------------------------------------------------- Datum/Zeit
            case "GETDATE": return "SYSDATE";
            case "SYSDATETIME":
            case "SYSDATETIMEOFFSET": return "SYSTIMESTAMP";
            case "GETUTCDATE":
            case "SYSUTCDATETIME": return "SYS_EXTRACT_UTC(SYSTIMESTAMP)";
            case "DATEADD": return DateAdd(raw);
            case "DATEDIFF":
            case "DATEDIFF_BIG": return DateDiff(raw);
            case "DATEPART": return DatePart(PartOf(raw, 0), DateArg(raw, 1));
            case "YEAR": return $"EXTRACT(YEAR FROM {DateArg(raw, 0)})";
            case "MONTH": return $"EXTRACT(MONTH FROM {DateArg(raw, 0)})";
            case "DAY": return $"EXTRACT(DAY FROM {DateArg(raw, 0)})";
            case "DATENAME":
            {
                var part = PartOf(raw, 0);
                var d = DateArg(raw, 1);
                return part switch
                {
                    "MONTH" => $"TRIM(TO_CHAR({d}, 'Month'))",
                    "WEEKDAY" => $"TRIM(TO_CHAR({d}, 'Day'))",
                    _ => $"TO_CHAR({DatePart(part, d)})"
                };
            }
            case "EOMONTH":
                return raw.Count > 1 ? $"LAST_DAY(ADD_MONTHS({DateArg(raw, 0)}, {A(1)}))" : $"LAST_DAY({DateArg(raw, 0)})";
            case "DATEFROMPARTS":
                return $"TO_DATE({A(0)} || '-' || {A(1)} || '-' || {A(2)}, 'YYYY-MM-DD')";
            case "DATETIMEFROMPARTS":
            case "DATETIME2FROMPARTS":
                return $"TO_TIMESTAMP({A(0)} || '-' || {A(1)} || '-' || {A(2)} || ' ' || {A(3)} || ':' || {A(4)} || ':' || {A(5)}, 'YYYY-MM-DD HH24:MI:SS')";

            // ---------------------------------------------------- NULL-Behandlung / Logik
            case "ISNULL": return $"NVL({A(0)}, {A(1)})";
            case "IIF": return $"CASE WHEN {A(0)} THEN {A(1)} ELSE {A(2)} END";
            case "CHOOSE":
            {
                var sb = new StringBuilder($"CASE {A(0)}");
                for (int k = 1; k < raw.Count; k++) sb.Append($" WHEN {k} THEN {A(k)}");
                return sb.Append(" END").ToString();
            }

            // ---------------------------------------------------- Strings
            case "LEN": return $"LENGTH({A(0)})";
            case "DATALENGTH": return $"LENGTHB({A(0)})";
            case "SUBSTRING": return $"SUBSTR({A(0)}, {A(1)}, {A(2)})";
            case "LEFT": return $"SUBSTR({A(0)}, 1, {A(1)})";
            case "RIGHT": return $"SUBSTR({A(0)}, -{W(A(1))})";
            case "CHARINDEX":
                return raw.Count > 2 ? $"INSTR({A(1)}, {A(0)}, {A(2)})" : $"INSTR({A(1)}, {A(0)})";
            case "PATINDEX":
                ctx.Warn("PATINDEX → REGEXP_INSTR: LIKE-Muster in regulären Ausdruck umwandeln.");
                return $"REGEXP_INSTR({A(1)}, {A(0)}) /* TODO: Muster in Regex umwandeln */";
            case "REPLICATE": return $"RPAD({A(0)}, LENGTH({A(0)}) * {W(A(1))}, {A(0)})";
            case "SPACE": return $"RPAD(' ', {A(0)})";
            case "CONCAT": return "(" + string.Join(" || ", raw.Select((_, k) => A(k))) + ")";
            case "CONCAT_WS":
                ctx.Warn("CONCAT_WS → || : NULL-Werte werden in Oracle nicht übersprungen.");
                return "(" + string.Join($" || {A(0)} || ", raw.Skip(1).Select((_, k) => A(k + 1))) + ")";
            case "STUFF":
                if (Tok.Text(u.Inner).Contains("XML", StringComparison.OrdinalIgnoreCase))
                    ctx.Warn("STUFF(... FOR XML PATH('')) → LISTAGG(...) WITHIN GROUP (ORDER BY ...) verwenden.");
                return $"SUBSTR({A(0)}, 1, {W(A(1))} - 1) || {A(3)} || SUBSTR({A(0)}, {W(A(1))} + {W(A(2))})";
            case "CHAR": return $"CHR({A(0)})";
            case "NCHAR": return $"NCHR({A(0)})";
            case "UNICODE": return $"ASCII({A(0)})";
            case "QUOTENAME": return $"'\"' || {A(0)} || '\"'";
            case "STR":
                if (raw.Count > 1) ctx.Warn("STR(n, länge, dezimalen) → TO_CHAR mit Formatmaske prüfen.");
                return $"TO_CHAR({A(0)})";
            case "FORMAT": return Format(raw);
            case "STRING_AGG": return $"LISTAGG({A(0)}, {A(1)})";
            case "REVERSE":
                ctx.Warn("REVERSE ist in Oracle nur in SQL (nicht in PL/SQL) verfügbar und nicht dokumentiert.");
                return Generic();

            // ---------------------------------------------------- Mathe
            case "CEILING": return $"CEIL({A(0)})";
            case "SQUARE": return $"POWER({A(0)}, 2)";
            case "LOG": return raw.Count > 1 ? $"LOG({A(1)}, {A(0)})" : $"LN({A(0)})";
            case "LOG10": return $"LOG(10, {A(0)})";
            case "RAND": return "DBMS_RANDOM.VALUE";
            case "PI": return "ACOS(-1)";
            case "ATN2": return $"ATAN2({A(0)}, {A(1)})";
            case "ROUND":
                return raw.Count > 2 && A(2) != "0" ? $"TRUNC({A(0)}, {A(1)})" : Generic();
            case "COUNT_BIG": return $"COUNT({Convert(u.Inner)})";
            case "ISNUMERIC": return $"CASE WHEN VALIDATE_CONVERSION({A(0)} AS NUMBER) = 1 THEN 1 ELSE 0 END";
            case "ISDATE":
                ctx.Warn("ISDATE → VALIDATE_CONVERSION(... AS DATE) hängt von NLS_DATE_FORMAT ab.");
                return $"VALIDATE_CONVERSION({A(0)} AS DATE)";

            // ---------------------------------------------------- Konvertierung
            case "CAST":
            case "TRY_CAST":
            case "PARSE":
            case "TRY_PARSE":
            {
                var arg = raw.Count > 0 ? raw[0] : new List<Token>();
                int asIdx = Tok.FindTopLevel(arg, 0, x => x.Is("AS"));
                if (asIdx < 0) return Generic();
                var expr = Tok.Trim(arg.GetRange(0, asIdx));
                var type = arg.GetRange(asIdx + 1, arg.Count - asIdx - 1);
                int usingIdx = Tok.FindTopLevel(type, 0, x => x.Is("USING"));
                if (usingIdx >= 0) type = type.GetRange(0, usingIdx);
                return CastCore(expr, type, u.FuncName.StartsWith("TRY"));
            }
            case "CONVERT":
            case "TRY_CONVERT":
            {
                if (raw.Count < 2) return Generic();
                var target = TypeMapper.Map(raw[0], TypeContext.Cast, ctx);
                if (raw.Count > 2 && int.TryParse(A(2), out var style) && DateStyles.TryGetValue(style, out var fmt))
                {
                    var e = Convert(raw[1]).Trim();
                    if (target.IsString) return $"TO_CHAR({e}, '{fmt}')";
                    if (target.IsDate)
                        return target.Type.StartsWith("TIMESTAMP") ? $"TO_TIMESTAMP({e}, '{fmt}')" : $"TO_DATE({e}, '{fmt}')";
                }
                return CastCore(raw[1], raw[0], u.FuncName.StartsWith("TRY"));
            }

            // ---------------------------------------------------- System / Fehler
            case "NEWID":
            case "NEWSEQUENTIALID": return "SYS_GUID()";
            case "DB_NAME": return "SYS_CONTEXT('USERENV', 'DB_NAME')";
            case "USER_NAME":
            case "SUSER_SNAME":
            case "SUSER_NAME":
            case "ORIGINAL_LOGIN": return "USER";
            case "HOST_NAME": return "SYS_CONTEXT('USERENV', 'HOST')";
            case "APP_NAME": return "SYS_CONTEXT('USERENV', 'MODULE')";
            case "OBJECT_ID":
                ctx.Warn("OBJECT_ID → Abfrage auf USER_OBJECTS/ALL_OBJECTS umsetzen.");
                return Generic() + " /* TODO: USER_OBJECTS */";
            case "ERROR_MESSAGE":
                if (S.CatchDepth > 0) { S.UsedErrMsg = true; return "v_err_msg"; }
                return "SQLERRM";
            case "ERROR_NUMBER":
                if (S.CatchDepth > 0) { S.UsedErrCode = true; return "v_err_code"; }
                return "SQLCODE";
            case "ERROR_LINE":
                if (S.CatchDepth > 0) { S.UsedErrLine = true; return "v_err_line"; }
                return "DBMS_UTILITY.FORMAT_ERROR_BACKTRACE";
            case "ERROR_PROCEDURE": return "$$PLSQL_UNIT";
            case "ERROR_SEVERITY": return "16 /* TODO: ERROR_SEVERITY */";
            case "ERROR_STATE": return "1 /* TODO: ERROR_STATE */";
            case "SCOPE_IDENTITY":
            case "IDENT_CURRENT":
                ctx.Warn("SCOPE_IDENTITY()/IDENT_CURRENT → INSERT ... RETURNING <id> INTO <variable> verwenden.");
                return "NULL /* TODO: SCOPE_IDENTITY() → INSERT ... RETURNING id INTO var */";
            case "HASHBYTES":
                ctx.Warn("HASHBYTES → STANDARD_HASH (nur in SQL) bzw. DBMS_CRYPTO.HASH (PL/SQL).");
                return $"STANDARD_HASH({A(1)}, {A(0)})";
            case "@@ROWCOUNT": return "SQL%ROWCOUNT";
            default:
                return Generic();
        }
    }

    // ---------------------------------------------------------------------------------------- CAST

    string CastCore(List<Token> exprTokens, List<Token> typeTokens, bool isTry)
    {
        var target = TypeMapper.Map(typeTokens, TypeContext.Cast, ctx);
        var exprTrim = Tok.Trim(exprTokens);
        var e = Convert(exprTrim).Trim();
        bool literal = exprTrim.Count == 1 && exprTrim[0].Kind is TokenKind.String or TokenKind.NString;
        string tail = isTry ? " DEFAULT NULL ON CONVERSION ERROR" : "";

        if (target.IsDate)
        {
            bool ts = target.Type.StartsWith("TIMESTAMP");
            if (literal)
            {
                var fmt = GuessDateFormat(exprTrim[0].StringContent);
                return ts ? $"TO_TIMESTAMP({e}{tail}, '{fmt}')" : $"TO_DATE({e}{tail}, '{fmt}')";
            }
            if (IsStringy(exprTrim))
            {
                ctx.Warn("Konvertierung String → Datum: Formatmaske bei TO_DATE/TO_TIMESTAMP prüfen.");
                return ts ? $"TO_TIMESTAMP({e}{tail}, 'YYYY-MM-DD HH24:MI:SS')" : $"TO_DATE({e}{tail}, 'YYYY-MM-DD')";
            }
            if (target.Type == "DATE" && !isTry) return $"TRUNC({e})";
        }
        return $"CAST({e} AS {target.Type}{tail})";
    }

    /// <summary>String-Literal → TO_DATE/TO_TIMESTAMP (für Zuweisungen an Datumsvariablen).</summary>
    public string DateLiteral(Token lit, bool timestamp)
    {
        var fmt = GuessDateFormat(lit.StringContent);
        return timestamp || fmt.Contains("HH24") ? $"TO_TIMESTAMP({lit.Text}, '{fmt}')" : $"TO_DATE({lit.Text}, '{fmt}')";
    }

    string GuessDateFormat(string s)
    {
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$")) return "YYYY-MM-DD";
        if (Regex.IsMatch(s, @"^\d{8}$")) return "YYYYMMDD";
        if (Regex.IsMatch(s, @"^\d{2}\.\d{2}\.\d{4}$")) return "DD.MM.YYYY";
        var m = Regex.Match(s, @"^\d{4}-\d{2}-\d{2}([ T])\d{2}:\d{2}(:\d{2})?(\.\d+)?$");
        if (m.Success)
        {
            var sep = m.Groups[1].Value == "T" ? "\"T\"" : " ";
            var sec = m.Groups[2].Success ? ":SS" : "";
            var frac = m.Groups[3].Success ? ".FF" : "";
            return $"YYYY-MM-DD{sep}HH24:MI{sec}{frac}";
        }
        ctx.Warn($"Datumsformat von '{s}' nicht erkannt – Formatmaske prüfen.");
        return "YYYY-MM-DD";
    }

    // ---------------------------------------------------------------------------------------- Datum

    string PartOf(List<List<Token>> raw, int k)
    {
        if (k >= raw.Count) return "DAY";
        var sig = Tok.Sig(raw[k]);
        var p = sig.Count > 0 ? sig[0].Name.Trim('\'') : "DAY";
        if (DatePartAlias.TryGetValue(p, out var norm)) return norm;
        ctx.Warn($"Datumsteil '{p}' nicht unterstützt.");
        return p.ToUpperInvariant();
    }

    string DateArg(List<List<Token>> raw, int k)
    {
        if (k >= raw.Count) return "NULL";
        var t = Tok.Trim(raw[k]);
        if (t.Count == 1 && t[0].Kind is TokenKind.String or TokenKind.NString)
        {
            var fmt = GuessDateFormat(t[0].StringContent);
            return fmt.Contains("HH24") ? $"TO_TIMESTAMP({t[0].Text}, '{fmt}')" : $"TO_DATE({t[0].Text}, '{fmt}')";
        }
        return Convert(t).Trim();
    }

    string DateAdd(List<List<Token>> raw)
    {
        var part = PartOf(raw, 0);
        var n = raw.Count > 1 ? Convert(raw[1]).Trim() : "0";
        var d = DateArg(raw, 2);
        switch (part)
        {
            case "YEAR":
            case "QUARTER":
            case "MONTH":
                ctx.Warn("DATEADD(month/year) → ADD_MONTHS: am Monatsende abweichend (ADD_MONTHS springt auf den letzten Tag).");
                return part switch
                {
                    "YEAR" => $"ADD_MONTHS({d}, 12 * {W(n)})",
                    "QUARTER" => $"ADD_MONTHS({d}, 3 * {W(n)})",
                    _ => $"ADD_MONTHS({d}, {n})"
                };
            case "WEEK": return $"{W(d)} + 7 * {W(n)}";
            case "HOUR": return $"{W(d)} + NUMTODSINTERVAL({n}, 'HOUR')";
            case "MINUTE": return $"{W(d)} + NUMTODSINTERVAL({n}, 'MINUTE')";
            case "SECOND": return $"{W(d)} + NUMTODSINTERVAL({n}, 'SECOND')";
            case "MILLISECOND": return $"{W(d)} + NUMTODSINTERVAL({W(n)} / 1000, 'SECOND')";
            default: return $"{W(d)} + {W(n)}";
        }
    }

    string DateDiff(List<List<Token>> raw)
    {
        var part = PartOf(raw, 0);
        var a = W(DateArg(raw, 1));
        var b = W(DateArg(raw, 2));
        switch (part)
        {
            case "YEAR": return $"(EXTRACT(YEAR FROM {b}) - EXTRACT(YEAR FROM {a}))";
            case "QUARTER":
                return $"((EXTRACT(YEAR FROM {b}) * 4 + TO_NUMBER(TO_CHAR({b}, 'Q'))) - (EXTRACT(YEAR FROM {a}) * 4 + TO_NUMBER(TO_CHAR({a}, 'Q'))))";
            case "MONTH": return $"MONTHS_BETWEEN(TRUNC({b}, 'MM'), TRUNC({a}, 'MM'))";
            case "WEEK":
                ctx.Warn("DATEDIFF(week) zählt in T-SQL Sonntagsgrenzen – Oracle-Umsetzung nutzt ISO-Wochen (TRUNC 'IW').");
                return $"((TRUNC({b}, 'IW') - TRUNC({a}, 'IW')) / 7)";
            case "HOUR": return $"ROUND((TRUNC({b}, 'HH24') - TRUNC({a}, 'HH24')) * 24)";
            case "MINUTE": return $"ROUND((TRUNC({b}, 'MI') - TRUNC({a}, 'MI')) * 1440)";
            case "SECOND": return $"ROUND((CAST({b} AS DATE) - CAST({a} AS DATE)) * 86400)";
            case "MILLISECOND":
                ctx.Warn("DATEDIFF(millisecond) → Intervall-Arithmetik prüfen.");
                return $"ROUND((CAST({b} AS DATE) - CAST({a} AS DATE)) * 86400000)";
            default: return $"(TRUNC({b}) - TRUNC({a}))";
        }
    }

    string DatePart(string part, string d)
    {
        switch (part)
        {
            case "YEAR": return $"EXTRACT(YEAR FROM {d})";
            case "MONTH": return $"EXTRACT(MONTH FROM {d})";
            case "DAY": return $"EXTRACT(DAY FROM {d})";
            case "HOUR": return $"EXTRACT(HOUR FROM CAST({d} AS TIMESTAMP))";
            case "MINUTE": return $"EXTRACT(MINUTE FROM CAST({d} AS TIMESTAMP))";
            case "SECOND": return $"TRUNC(EXTRACT(SECOND FROM CAST({d} AS TIMESTAMP)))";
            case "MILLISECOND": return $"TO_NUMBER(TO_CHAR(CAST({d} AS TIMESTAMP), 'FF3'))";
            case "QUARTER": return $"TO_NUMBER(TO_CHAR({d}, 'Q'))";
            case "DAYOFYEAR": return $"TO_NUMBER(TO_CHAR({d}, 'DDD'))";
            case "WEEK": return $"TO_NUMBER(TO_CHAR({d}, 'WW'))";
            case "ISO_WEEK": return $"TO_NUMBER(TO_CHAR({d}, 'IW'))";
            case "WEEKDAY":
                ctx.Warn("DATEPART(weekday) → TO_CHAR(d, 'D') hängt von NLS_TERRITORY ab.");
                return $"TO_NUMBER(TO_CHAR({d}, 'D'))";
            default: return $"EXTRACT({part} FROM {d})";
        }
    }

    // ---------------------------------------------------------------------------------------- FORMAT

    string Format(List<List<Token>> raw)
    {
        var v = raw.Count > 0 ? Convert(raw[0]).Trim() : "NULL";
        if (raw.Count < 2) return $"TO_CHAR({v})";
        var f = Tok.Trim(raw[1]);
        if (f.Count == 1 && f[0].Kind is TokenKind.String or TokenKind.NString)
        {
            var fmt = f[0].StringContent;
            var mN = Regex.Match(fmt, @"^[Nn](\d*)$");
            if (mN.Success)
            {
                int dec = mN.Groups[1].Value.Length > 0 ? int.Parse(mN.Groups[1].Value) : 2;
                return $"TO_CHAR({v}, 'FM999G999G999G990{(dec > 0 ? "D" + new string('0', dec) : "")}')";
            }
            if (Regex.IsMatch(fmt, "[yMdHhms]") && !fmt.Contains('#') && !fmt.Contains('0'))
                return $"TO_CHAR({v}, '{NetDateFormatToOracle(fmt)}')";
        }
        ctx.Warn("FORMAT → TO_CHAR: Formatmaske manuell prüfen.");
        return $"TO_CHAR({v}, {Convert(raw[1]).Trim()}) /* TODO: Formatmaske */";
    }

    static string NetDateFormatToOracle(string f)
    {
        var map = new (string Net, string Ora)[]
        {
            ("yyyy", "YYYY"), ("yy", "YY"), ("MMMM", "FMMonth"), ("MMM", "Mon"), ("MM", "MM"), ("M", "FMMM"),
            ("dddd", "FMDay"), ("ddd", "Dy"), ("dd", "DD"), ("d", "FMDD"), ("HH", "HH24"), ("H", "FMHH24"),
            ("hh", "HH12"), ("h", "FMHH12"), ("mm", "MI"), ("ss", "SS"), ("fff", "FF3"), ("ff", "FF2"),
            ("tt", "AM")
        };
        var sb = new StringBuilder();
        int i = 0;
        while (i < f.Length)
        {
            bool matched = false;
            foreach (var (net, ora) in map)
            {
                if (string.CompareOrdinal(f, i, net, 0, net.Length) == 0)
                {
                    sb.Append(ora);
                    i += net.Length;
                    matched = true;
                    break;
                }
            }
            if (matched) continue;
            var c = f[i];
            if (char.IsLetter(c)) sb.Append('"').Append(c).Append('"');
            else sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
