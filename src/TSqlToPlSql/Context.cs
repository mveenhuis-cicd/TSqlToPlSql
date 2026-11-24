namespace TSqlToPlSql;

public enum OracleVersion
{
    /// <summary>Oracle 19c (LTS) – Standard.</summary>
    Oracle19c,
    /// <summary>Oracle 23ai – SELECT ohne FROM DUAL, DROP ... IF EXISTS, Multi-Row-VALUES.</summary>
    Oracle23ai
}

public sealed class ConversionOptions
{
    public OracleVersion Target { get; set; } = OracleVersion.Oracle19c;
    public string LocalVariablePrefix { get; set; } = "v_";
    public string ParameterPrefix { get; set; } = "p_";
    /// <summary>Entfernt das Schema "dbo." aus Objektnamen.</summary>
    public bool RemoveDboSchema { get; set; } = true;
    /// <summary>Schreibt einen Kopfkommentar mit allen Hinweisen in die Ausgabe.</summary>
    public bool IncludeHeader { get; set; } = true;
}

public sealed class ConversionResult
{
    public ConversionResult(string output, IReadOnlyList<string> warnings)
    {
        Output = output;
        Warnings = warnings;
    }

    public string Output { get; }
    public IReadOnlyList<string> Warnings { get; }
}

internal sealed class VarInfo
{
    public required string OraName { get; init; }
    public bool IsString { get; init; }
    public bool IsDate { get; init; }
    public bool IsParam { get; init; }
    public bool IsTable { get; init; }
    public string OraType { get; init; } = "";
}

/// <summary>Zustand einer PL/SQL-Einheit (Prozedur, Funktion, anonymer Block) bzw. reiner SQL-Modus.</summary>
internal sealed class UnitScope
{
    readonly HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
    int counter;

    public bool IsPlSql { get; init; }
    public bool IsFunction { get; init; }
    public Dictionary<string, VarInfo> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Declarations { get; } = new();
    public HashSet<string> Savepoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastCursor { get; set; }
    public int CatchDepth { get; set; }
    public bool UsedErrCode { get; set; }
    public bool UsedErrMsg { get; set; }
    public bool UsedErrLine { get; set; }
    public bool NoDataHintGiven { get; set; }

    public string NewName(string prefix) => prefix + (++counter);

    public void Declare(string name, string line)
    {
        if (declared.Add(name)) Declarations.Add(line);
    }

    public static UnitScope Sql() => new() { IsPlSql = false };
}

internal sealed class Ctx
{
    readonly HashSet<string> seen = new();

    public Ctx(ConversionOptions options) { Options = options; }

    public ConversionOptions Options { get; }
    public List<string> Warnings { get; } = new();
    /// <summary>Identity-Spalten aus CREATE TABLE (Tabelle → Spalte), für SCOPE_IDENTITY → RETURNING.</summary>
    public Dictionary<string, string> IdentityColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Einmalig anzulegende Objekte (Global Temporary Tables), werden vor dem Code ausgegeben.</summary>
    public List<string> PrereqDdl { get; } = new();
    public HashSet<string> PrereqNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public UnitScope Scope { get; set; } = UnitScope.Sql();
    public bool Is19c => Options.Target == OracleVersion.Oracle19c;

    public void Warn(string message)
    {
        if (seen.Add(message)) Warnings.Add(message);
    }
}
