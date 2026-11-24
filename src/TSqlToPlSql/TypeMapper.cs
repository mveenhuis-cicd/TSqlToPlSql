using System.Text.RegularExpressions;

namespace TSqlToPlSql;

internal enum TypeContext { Column, Variable, Parameter, Cast, Return }

internal sealed record OraType(string Type, bool IsString, bool IsDate);

/// <summary>Abbildung der SQL-Server-Datentypen auf Oracle.</summary>
internal static class TypeMapper
{
    public static OraType Map(List<Token> typeTokens, TypeContext tc, Ctx ctx)
    {
        var sig = Tok.Sig(typeTokens);
        if (sig.Count == 0) return new OraType("VARCHAR2(4000)", true, false);

        int k = 0;
        var parts = new List<string> { sig[0].Name };
        while (k + 2 < sig.Count && sig[k + 1].IsSym(".")) { parts.Add(sig[k + 2].Name); k += 2; }
        k++;
        var name = parts[^1].ToUpperInvariant();
        bool userType = parts.Count > 1 && !parts[0].Equals("sys", StringComparison.OrdinalIgnoreCase);

        if (name == "DOUBLE" && k < sig.Count && sig[k].Is("PRECISION")) { name = "FLOAT"; k++; }
        if (name is "CHAR" or "CHARACTER" && k < sig.Count && sig[k].Is("VARYING")) { name = "VARCHAR"; k++; }
        if (name == "NATIONAL")
        {
            name = "NCHAR";
            while (k < sig.Count && sig[k].IsAny("CHAR", "CHARACTER", "VARYING"))
            {
                if (sig[k].Is("VARYING")) name = "NVARCHAR";
                k++;
            }
        }

        var args = new List<string>();
        if (k < sig.Count && sig[k].IsSym("("))
        {
            var cur = "";
            for (int j = k + 1; j < sig.Count && !sig[j].IsSym(")"); j++)
            {
                if (sig[j].IsSym(",")) { args.Add(cur.Trim()); cur = ""; }
                else cur += sig[j].Text;
            }
            args.Add(cur.Trim());
        }
        string? a0 = args.Count > 0 ? args[0] : null;
        string? a1 = args.Count > 1 ? args[1] : null;
        bool isMax = string.Equals(a0, "MAX", StringComparison.OrdinalIgnoreCase);
        int.TryParse(a0, out var n0);
        bool col = tc == TypeContext.Column;
        bool cast = tc == TypeContext.Cast;

        string type;
        bool str = false, date = false;

        if (userType)
        {
            ctx.Warn($"Benutzerdefinierter Typ '{string.Join(".", parts)}' wurde unverändert übernommen.");
            return new OraType(parts[^1], false, false);
        }

        switch (name)
        {
            case "INT":
            case "INTEGER": type = "NUMBER(10)"; break;
            case "BIGINT": type = "NUMBER(19)"; break;
            case "SMALLINT": type = "NUMBER(5)"; break;
            case "TINYINT": type = "NUMBER(3)"; break;
            case "BIT": type = "NUMBER(1)"; break;
            case "DECIMAL":
            case "NUMERIC":
            case "DEC":
                type = a0 == null ? "NUMBER(18)" : a1 == null ? $"NUMBER({a0})" : $"NUMBER({a0},{a1})";
                break;
            case "MONEY": type = "NUMBER(19,4)"; break;
            case "SMALLMONEY": type = "NUMBER(10,4)"; break;
            case "FLOAT": type = a0 != null && n0 > 0 && n0 <= 24 ? "BINARY_FLOAT" : "BINARY_DOUBLE"; break;
            case "REAL": type = "BINARY_FLOAT"; break;

            case "VARCHAR":
                str = true;
                if (isMax) type = col ? "CLOB" : cast ? "VARCHAR2(4000)" : "VARCHAR2(32767)";
                else if (a0 == null) type = cast ? "VARCHAR2(30)" : "VARCHAR2(1)";
                else if (col && n0 > 4000)
                {
                    type = "CLOB";
                    ctx.Warn($"VARCHAR({n0}) überschreitet 4000 Byte → CLOB (oder MAX_STRING_SIZE=EXTENDED nutzen).");
                }
                else type = $"VARCHAR2({a0})";
                break;
            case "NVARCHAR":
                str = true;
                if (isMax) type = col ? "NCLOB" : cast ? "NVARCHAR2(2000)" : "NVARCHAR2(16383)";
                else if (a0 == null) type = cast ? "NVARCHAR2(30)" : "NVARCHAR2(1)";
                else if (col && n0 > 2000)
                {
                    type = "NCLOB";
                    ctx.Warn($"NVARCHAR({n0}) überschreitet 2000 Zeichen → NCLOB.");
                }
                else type = $"NVARCHAR2({a0})";
                break;
            case "CHAR":
            case "CHARACTER":
                str = true;
                type = a0 == null ? "CHAR(1)" : n0 > 2000 ? "CLOB" : $"CHAR({a0})";
                break;
            case "NCHAR":
                str = true;
                type = a0 == null ? "NCHAR(1)" : n0 > 1000 ? "NCLOB" : $"NCHAR({a0})";
                break;
            case "TEXT": str = true; type = "CLOB"; break;
            case "NTEXT": str = true; type = "NCLOB"; break;
            case "SYSNAME": str = true; type = "NVARCHAR2(128)"; break;

            case "DATE": date = true; type = "DATE"; break;
            case "SMALLDATETIME": date = true; type = "DATE"; break;
            case "DATETIME": date = true; type = "TIMESTAMP(3)"; break;
            case "DATETIME2": date = true; type = $"TIMESTAMP({a0 ?? "7"})"; break;
            case "DATETIMEOFFSET": date = true; type = $"TIMESTAMP({a0 ?? "7"}) WITH TIME ZONE"; break;
            case "TIME":
                type = $"INTERVAL DAY(0) TO SECOND({a0 ?? "7"})";
                ctx.Warn("TIME → INTERVAL DAY TO SECOND: Verwendung im Code prüfen.");
                break;

            case "UNIQUEIDENTIFIER":
                type = "RAW(16)";
                ctx.Warn("UNIQUEIDENTIFIER → RAW(16): GUID-Literale/Vergleiche mit HEXTORAW/RAWTOHEX anpassen.");
                break;
            case "BINARY": type = $"RAW({a0 ?? "1"})"; break;
            case "VARBINARY": type = isMax || n0 > 2000 ? "BLOB" : $"RAW({a0 ?? "1"})"; break;
            case "IMAGE": type = "BLOB"; break;
            case "XML": type = "XMLTYPE"; break;
            case "SQL_VARIANT": type = "ANYDATA"; ctx.Warn("SQL_VARIANT → ANYDATA: Zugriffe manuell anpassen."); break;
            case "GEOGRAPHY":
            case "GEOMETRY": type = "SDO_GEOMETRY"; ctx.Warn($"{name} → SDO_GEOMETRY: Spatial-Funktionen manuell umsetzen."); break;
            case "HIERARCHYID": type = "VARCHAR2(4000)"; ctx.Warn("HIERARCHYID hat kein Oracle-Pendant (CONNECT BY prüfen)."); break;
            case "ROWVERSION":
            case "TIMESTAMP":
                type = "RAW(8)";
                ctx.Warn("ROWVERSION/TIMESTAMP (SQL Server) → RAW(8): Versionierung per Trigger oder ORA_ROWSCN umsetzen.");
                break;
            case "CURSOR": type = "SYS_REFCURSOR"; break;
            default:
                ctx.Warn($"Unbekannter Datentyp '{name}' unverändert übernommen.");
                type = Tok.Text(sig);
                break;
        }

        if (tc is TypeContext.Parameter or TypeContext.Return)
            type = Regex.Replace(type, @"\s*\([^)]*\)", "");
        return new OraType(type, str, date);
    }
}
