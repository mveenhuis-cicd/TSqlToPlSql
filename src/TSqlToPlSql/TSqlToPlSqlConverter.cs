using System.Text;

namespace TSqlToPlSql;

/// <summary>
/// Einstiegspunkt: konvertiert T-SQL (SQL Server) nach Oracle SQL bzw. PL/SQL.
/// </summary>
/// <example>
/// var result = new TSqlToPlSqlConverter().Convert(tsql);
/// Console.WriteLine(result.Output);
/// </example>
public sealed class TSqlToPlSqlConverter
{
    readonly ConversionOptions options;

    public TSqlToPlSqlConverter(ConversionOptions? options = null)
    {
        this.options = options ?? new ConversionOptions();
    }

    public ConversionResult Convert(string tsql)
    {
        var ctx = new Ctx(options);
        var tokens = Lexer.Tokenize(tsql ?? "");
        var body = new StringBuilder();

        foreach (var batch in Lexer.SplitBatches(tokens))
        {
            try
            {
                var stmts = new Parser(batch, ctx).ParseBatch();
                body.Append(new Emitter(ctx).EmitBatch(stmts));
            }
            catch (Exception e)
            {
                ctx.Warn($"Interner Fehler bei der Konvertierung eines Batches: {e.Message}");
                body.Append("-- TODO: Batch konnte nicht konvertiert werden (").Append(e.Message).Append(")\n");
                foreach (var line in Tok.Text(batch).Split('\n')) body.Append("-- ").Append(line).Append('\n');
                ctx.Scope = UnitScope.Sql();
            }
        }

        var output = new StringBuilder();
        if (options.IncludeHeader)
        {
            output.Append("-- ============================================================================\n");
            output.Append($"-- Konvertiert von T-SQL nach PL/SQL (Ziel: {(ctx.Is19c ? "Oracle 19c" : "Oracle 23ai")})\n");
            output.Append("-- Automatische Umsetzung – Ergebnis prüfen, Stellen mit TODO manuell nacharbeiten.\n");
            if (ctx.Warnings.Count > 0)
            {
                output.Append("--\n-- Hinweise:\n");
                foreach (var w in ctx.Warnings) output.Append("--   * ").Append(w).Append('\n');
            }
            output.Append("-- ============================================================================\n\n");
        }
        if (ctx.PrereqDdl.Count > 0)
        {
            output.Append("-- ----------------------------------------------------------------------------\n");
            output.Append("-- Einmalig anzulegende Objekte (Global Temporary Tables für #temp/@tabellen)\n");
            output.Append("-- ----------------------------------------------------------------------------\n");
            foreach (var d in ctx.PrereqDdl) output.Append(d).Append("\n\n");
            output.Append("-- ----------------------------------------------------------------------------\n\n");
        }
        output.Append(body.ToString().TrimEnd()).Append('\n');
        return new ConversionResult(output.ToString(), ctx.Warnings.ToList());
    }
}
