using System.Text;
using TSqlToPlSql;

// tsql2plsql <eingabe.sql | -> [-o ausgabe.sql] [--target 19c|23ai] [--no-header] [--keep-dbo]
var options = new ConversionOptions();
string? input = null, output = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o":
        case "--output":
            output = args[++i];
            break;
        case "--target":
            options.Target = args[++i].Contains("23", StringComparison.Ordinal) ? OracleVersion.Oracle23ai : OracleVersion.Oracle19c;
            break;
        case "--no-header":
            options.IncludeHeader = false;
            break;
        case "--keep-dbo":
            options.RemoveDboSchema = false;
            break;
        case "-h":
        case "--help":
            PrintHelp();
            return 0;
        default:
            input = args[i];
            break;
    }
}

if (input == null)
{
    PrintHelp();
    return 1;
}

string tsql;
if (input == "-")
    tsql = Console.In.ReadToEnd();
else if (Directory.Exists(input))
{
    // ganzes Verzeichnis: jede .sql-Datei → <name>.plsql.sql
    var files = Directory.GetFiles(input, "*.sql", SearchOption.AllDirectories)
        .Where(f => !f.EndsWith(".plsql.sql", StringComparison.OrdinalIgnoreCase)).ToList();
    var outDir = output ?? input;
    Directory.CreateDirectory(outDir);
    foreach (var f in files)
    {
        var r = new TSqlToPlSqlConverter(options).Convert(File.ReadAllText(f));
        var target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".plsql.sql");
        File.WriteAllText(target, r.Output, new UTF8Encoding(false));
        Console.Error.WriteLine($"{Path.GetFileName(f)} → {target} ({r.Warnings.Count} Hinweise)");
    }
    return 0;
}
else
    tsql = File.ReadAllText(input);

var result = new TSqlToPlSqlConverter(options).Convert(tsql);
if (output != null)
{
    File.WriteAllText(output, result.Output, new UTF8Encoding(false));
    Console.Error.WriteLine($"Geschrieben: {output}");
}
else
    Console.Out.Write(result.Output);

if (result.Warnings.Count > 0)
{
    Console.Error.WriteLine($"{result.Warnings.Count} Hinweis(e):");
    foreach (var w in result.Warnings) Console.Error.WriteLine("  - " + w);
}
return 0;

static void PrintHelp()
{
    Console.Error.WriteLine("""
        T-SQL → PL/SQL Konverter

        Aufruf:
          tsql2plsql <datei.sql | verzeichnis | -> [Optionen]

        Optionen:
          -o, --output <pfad>   Ausgabedatei (bzw. Zielverzeichnis bei Verzeichnis-Eingabe)
          --target 19c|23ai     Oracle-Zielversion (Standard: 19c)
          --no-header           keinen Hinweis-Kopf schreiben
          --keep-dbo            Schema 'dbo.' nicht entfernen
        """);
}
