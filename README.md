# TSqlToPlSql – T-SQL → PL/SQL Mapper

Regelbasierter Konverter (C#/.NET 8) für SQL-Server-Skripte (T-SQL) nach Oracle SQL / PL/SQL.
Er arbeitet ohne externe Abhängigkeiten: eigener Lexer, pragmatischer Statement-Parser und
ein Ausdrucks-Konverter. Alles, was nicht sicher automatisch umsetzbar ist, wird im Ergebnis
mit `TODO` markiert und im Kopfkommentar als Hinweis aufgeführt.

## Projektstruktur

```
src/TSqlToPlSql/        Bibliothek (Lexer, Parser, ExprConverter, TypeMapper, Emitter)
src/TSqlToPlSql.Cli/    Kommandozeilenprogramm  (tsql2plsql)
src/TSqlToPlSql.Desktop/ WPF-Desktop-Anwendung (Windows)
tests/TSqlToPlSql.Tests xUnit-Tests
samples/                Beispiel-Skripte + konvertierte Ausgabe (*.plsql.sql)
```

## Verwendung

```bash
dotnet build
dotnet test

# Einzeldatei
dotnet run --project src/TSqlToPlSql.Cli -- skript.sql -o skript.plsql.sql

# Ganzes Verzeichnis (erzeugt <name>.plsql.sql je Datei)
dotnet run --project src/TSqlToPlSql.Cli -- ./sql -o ./oracle

# Optionen
--target 19c|23ai    Zielversion (Standard 19c)
--no-header          ohne Hinweis-Kopf
--keep-dbo           Schema "dbo." nicht entfernen
```

### Desktop-Anwendung (Windows)

```bash
dotnet run --project src/TSqlToPlSql.Desktop
# oder mit Datei:  TSqlToPlSql.Desktop.exe skript.sql
```

- links T-SQL eingeben/einfügen, rechts erscheint sofort das PL/SQL (automatisch beim Tippen oder mit F5)
- Datei öffnen (Strg+O) oder `.sql`-Datei ins Fenster ziehen; UTF-8, UTF-16 (SSMS) und ANSI werden erkannt
- Zielversion 19c/23ai, „dbo. entfernen“ und „Hinweis-Kopf“ umschaltbar
- Hinweisliste unten, Anzahl der TODO-Stellen in der Statusleiste
- Ergebnis kopieren oder speichern (Strg+S); „Ordner konvertieren“ schreibt alle Dateien nach `<ordner>\oracle\`

Als Bibliothek:

```csharp
var result = new TSqlToPlSqlConverter(new ConversionOptions { Target = OracleVersion.Oracle19c })
    .Convert(tsql);
Console.WriteLine(result.Output);
foreach (var w in result.Warnings) Console.WriteLine(w);
```

## Was umgesetzt wird

### Struktur

| T-SQL | PL/SQL |
|---|---|
| `GO`-Batches | getrennte Statements / Blöcke mit `/` |
| `CREATE [OR ALTER] PROCEDURE @p INT = 0, @o INT OUTPUT` | `CREATE OR REPLACE PROCEDURE (p_p IN NUMBER DEFAULT 0, p_o IN OUT NUMBER)` |
| `CREATE FUNCTION … RETURNS INT` | `CREATE OR REPLACE FUNCTION … RETURN NUMBER` |
| Inline-TVF `RETURNS TABLE AS RETURN (SELECT …)` | Funktion mit `SYS_REFCURSOR` (+ Hinweis auf View/SQL-Macro) |
| Skript mit Variablen/IF/WHILE | anonymer Block `DECLARE … BEGIN … END; /` |
| `DECLARE @x` irgendwo im Code | Deklaration wird in den DECLARE-Teil gezogen |
| `SET @x = …`, `SET @x += …` | `v_x := …` |
| `IF … ELSE IF … ELSE` / `BEGIN … END` | `IF … THEN … ELSIF … ELSE … END IF;` |
| `WHILE … BEGIN … END`, `BREAK`, `CONTINUE` | `WHILE … LOOP … END LOOP;`, `EXIT`, `CONTINUE` |
| `IF EXISTS (SELECT …)`, `IF (SELECT …) > 0` | Unterabfrage → `SELECT … INTO v_tmp`, dann `IF` |
| `BEGIN TRY … END TRY BEGIN CATCH … END CATCH` | `BEGIN … EXCEPTION WHEN OTHERS THEN … END;` |
| `ERROR_MESSAGE()`, `ERROR_NUMBER()` | `SQLERRM`, `SQLCODE` (im CATCH über Hilfsvariablen, damit in SQL nutzbar) |
| `THROW;` / `THROW 50001, 'x', 1` | `RAISE;` / `RAISE_APPLICATION_ERROR(-20001, 'x')` |
| `RAISERROR('Text %s', 16, 1, @x)` | `RAISE_APPLICATION_ERROR(-20000, 'Text ' \|\| v_x)` |
| `PRINT` | `DBMS_OUTPUT.PUT_LINE` |
| Cursor `DECLARE/OPEN/FETCH/@@FETCH_STATUS/CLOSE` | `CURSOR c IS …`, `FETCH c INTO`, `c%FOUND` |
| `SELECT @a = x, @b = y FROM …` | `SELECT x, y INTO v_a, v_b FROM …` |
| `SELECT @s = @s + spalte FROM …` | `LISTAGG(…) WITHIN GROUP (…)` |
| `SELECT …` (Ergebnismenge in Prozedur) | `OPEN c FOR …; DBMS_SQL.RETURN_RESULT(c);` |
| `INSERT …; SET @id = SCOPE_IDENTITY()` | `INSERT … RETURNING id INTO v_id;` |
| `EXEC proc @a = 1, @b = @x OUTPUT` | `proc(p_a => 1, p_b => v_x);` |
| `EXEC('…')`, `sp_executesql N'… @p', …` | `EXECUTE IMMEDIATE '… :p' USING …` (Literal wird mitkonvertiert) |
| `BEGIN TRAN / COMMIT / ROLLBACK / SAVE TRAN` | implizit / `COMMIT` / `ROLLBACK` / `SAVEPOINT` |
| `#temp`, `@tabelle TABLE (…)` | Global Temporary Table (Vorab-DDL am Dateianfang), im Code `DELETE` |

### SQL / DML

| T-SQL | Oracle |
|---|---|
| `[Name]`, `dbo.Tabelle` | `Name`, `Tabelle` (reservierte Wörter wie `Level`, `Date` → `"LEVEL"`) |
| `SELECT TOP n … ORDER BY` | `… ORDER BY … FETCH FIRST n ROWS ONLY` |
| `SELECT 1` (ohne FROM) | `SELECT 1 FROM DUAL` (19c) |
| `'a' + 'b'` | `'a' \|\| 'b'` (wenn ein Operand erkennbar ein String ist) |
| `Alias = Ausdruck` | `Ausdruck AS Alias` |
| `FROM t AS a`, `WITH (NOLOCK)`, `OPTION (…)` | `FROM t a`, entfernt |
| `INSERT … VALUES (…), (…)` | `INSERT … SELECT … FROM DUAL UNION ALL …` (19c) |
| `UPDATE a SET … FROM t a JOIN s b ON …` | `MERGE INTO t a USING s b ON (…) WHEN MATCHED THEN UPDATE …` |
| `DELETE a FROM t a JOIN s b ON …` | `DELETE FROM t a WHERE EXISTS (SELECT 1 FROM s b WHERE …)` |
| `DELETE/UPDATE TOP (n)` | `… AND ROWNUM <= n` |
| `MERGE … WHEN NOT MATCHED BY TARGET` | `WHEN NOT MATCHED` |
| `EXCEPT` | `MINUS` (19c) |
| `NEXT VALUE FOR seq` | `seq.NEXTVAL` |
| `a % b`, `a & b` | `MOD(a, b)`, `BITAND(a, b)` |
| `@@ROWCOUNT` | `SQL%ROWCOUNT` |

### Funktionen (Auszug)

`GETDATE`→`SYSDATE`, `SYSDATETIME`→`SYSTIMESTAMP`, `ISNULL`→`NVL`, `LEN`→`LENGTH`,
`SUBSTRING`→`SUBSTR`, `LEFT/RIGHT`→`SUBSTR`, `CHARINDEX`→`INSTR` (Argumente getauscht),
`REPLICATE`→`RPAD`, `CONCAT`→`||`, `STUFF`→`SUBSTR … ||`, `CHAR`→`CHR`, `NEWID`→`SYS_GUID`,
`IIF`/`CHOOSE`→`CASE`, `STRING_AGG`→`LISTAGG … WITHIN GROUP`, `CEILING`→`CEIL`, `LOG`→`LN`,
`DATEADD`→`+ n` / `ADD_MONTHS` / `NUMTODSINTERVAL`, `DATEDIFF`→Datumsarithmetik / `MONTHS_BETWEEN`,
`DATEPART/YEAR/MONTH/DAY`→`EXTRACT`/`TO_CHAR`, `EOMONTH`→`LAST_DAY`,
`CAST(x AS DATE)`→`TRUNC(x)`, `CONVERT(VARCHAR, d, 104)`→`TO_CHAR(d, 'DD.MM.YYYY')`,
`TRY_CAST/TRY_CONVERT`→`CAST(… DEFAULT NULL ON CONVERSION ERROR)`, `FORMAT`→`TO_CHAR` (Formatmaske übersetzt),
`ISNUMERIC`→`VALIDATE_CONVERSION`, `SCOPE_IDENTITY`→`RETURNING INTO`.

### Datentypen

| SQL Server | Oracle |
|---|---|
| `INT` / `BIGINT` / `SMALLINT` / `TINYINT` / `BIT` | `NUMBER(10)` / `NUMBER(19)` / `NUMBER(5)` / `NUMBER(3)` / `NUMBER(1)` |
| `DECIMAL(p,s)`, `MONEY` | `NUMBER(p,s)`, `NUMBER(19,4)` |
| `FLOAT`, `REAL` | `BINARY_DOUBLE`, `BINARY_FLOAT` |
| `VARCHAR(n)` / `VARCHAR(MAX)` | `VARCHAR2(n)` / `CLOB` (Variable: `VARCHAR2(32767)`) |
| `NVARCHAR(n)` / `NVARCHAR(MAX)` | `NVARCHAR2(n)` / `NCLOB` |
| `DATETIME`, `DATETIME2(n)`, `SMALLDATETIME`, `DATE` | `TIMESTAMP(3)`, `TIMESTAMP(n)`, `DATE`, `DATE` |
| `DATETIMEOFFSET` | `TIMESTAMP WITH TIME ZONE` |
| `UNIQUEIDENTIFIER` | `RAW(16)` |
| `VARBINARY(n)` / `(MAX)`, `IMAGE` | `RAW(n)` / `BLOB` |
| `TEXT`, `NTEXT`, `XML` | `CLOB`, `NCLOB`, `XMLTYPE` |
| `IDENTITY(1,1)` | `GENERATED BY DEFAULT ON NULL AS IDENTITY (START WITH 1 INCREMENT BY 1)` |

Parameter erhalten keine Längenangaben (Oracle-Regel), `DEFAULT` wird vor `NOT NULL` gestellt,
`CLUSTERED`, `ON [PRIMARY]`, `WITH (…)`, `ASC/DESC` in PK-Listen werden entfernt.

## Bekannte Grenzen / bitte prüfen

- **Leerstring = NULL** in Oracle: `WHERE x = ''` verhält sich anders (Hinweis wird ausgegeben).
- **SELECT INTO** wirft `NO_DATA_FOUND`/`TOO_MANY_ROWS` – in T-SQL bleibt die Variable unverändert.
- **String-Konkatenation** wird nur erkannt, wenn ein Operand sicher ein String ist (Literal,
  String-Variable, String-Funktion). Bei `spalteA + spalteB` (beide VARCHAR) bitte prüfen.
- **VARCHAR-Längen** sind in Oracle standardmäßig Bytes: bei Umlauten ggf. `NLS_LENGTH_SEMANTICS=CHAR`
  bzw. `VARCHAR2(n CHAR)` verwenden.
- **Trigger**, Multi-Statement-TVFs, `FOR XML/JSON`, `PIVOT`-Details, `OUTPUT`-Klausel,
  Rückgabewerte von Prozeduren (`RETURN n`) werden markiert, nicht automatisch umgesetzt.
- `UPDATE … FROM` → `MERGE` nur bei genau einem `INNER JOIN`; sonst `TODO`.
- `DATEADD(month, …)` → `ADD_MONTHS` weicht am Monatsende ab.
