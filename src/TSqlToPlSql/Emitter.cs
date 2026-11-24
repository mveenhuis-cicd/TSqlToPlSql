using System.Text;
using System.Text.RegularExpressions;

namespace TSqlToPlSql;

/// <summary>Erzeugt PL/SQL aus dem geparsten T-SQL-Batch.</summary>
internal sealed partial class Emitter
{
    readonly Ctx ctx;
    readonly ExprConverter ex;
    StringBuilder sb = new();
    int curIndent;

    public Emitter(Ctx ctx)
    {
        this.ctx = ctx;
        ex = new ExprConverter(ctx);
    }

    UnitScope S => ctx.Scope;
    ConversionOptions O => ctx.Options;

    // ================================================================================ Ausgabe-Helfer

    void L(int ind, string text)
    {
        var pad = new string(' ', ind * 4);
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var l = line.TrimEnd();
            if (l.Length == 0) sb.Append('\n');
            else sb.Append(pad).Append(l).Append('\n');
        }
    }

    string Capture(Action a)
    {
        var old = sb;
        sb = new StringBuilder();
        try { a(); return sb.ToString(); }
        finally { sb = old; }
    }

    static bool HasCode(string s) =>
        s.Split('\n').Any(l =>
        {
            var x = l.Trim();
            return x.Length > 0 && !x.StartsWith("--") && !x.StartsWith("/*") && !x.StartsWith("*");
        });

    /// <summary>Entfernt die Original-Einrückung aus Folgezeilen eines mehrzeiligen Statements.</summary>
    static string Reindent(string text, int baseIndent)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (int k = 1; k < lines.Length; k++)
        {
            int lead = 0;
            while (lead < lines[k].Length && lead < baseIndent && lines[k][lead] == ' ') lead++;
            lines[k] = lines[k][lead..].TrimEnd();
        }
        lines[0] = lines[0].TrimStart();
        return string.Join("\n", lines);
    }

    static string IndentBlock(string text, int levels)
    {
        var pad = new string(' ', 4 * levels);
        return string.Join("\n", text.Split('\n').Select(l => l.Length == 0 ? l : pad + l));
    }

    static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s*\n\s*", " ");

    string Cv(List<Token> tokens) => Reindent(ex.Convert(tokens).Trim(), curIndent);

    // ================================================================================ Batch

    public string EmitBatch(List<Stmt> stmts)
    {
        sb = new StringBuilder();
        bool hasUnit = stmts.Any(s => s is CreateProcStmt or CreateFuncStmt);
        if (hasUnit)
        {
            foreach (var s in stmts)
            {
                switch (s)
                {
                    case CreateProcStmt p: EmitProc(p); break;
                    case CreateFuncStmt f: EmitFunc(f); break;
                    default: EmitSqlMode(s); break;
                }
            }
        }
        else if (stmts.Any(IsProcedural))
        {
            EmitAnonymous(stmts);
        }
        else
        {
            ctx.Scope = UnitScope.Sql();
            foreach (var s in stmts) EmitSqlMode(s);
        }
        return sb.ToString();
    }

    static bool IsProcedural(Stmt s) => s switch
    {
        CommentStmt or SetOptionStmt or UseStmt or UnsupportedStmt => false,
        SqlStmt q => q.Tokens.Any(t => t.Kind == TokenKind.Variable),
        _ => true
    };

    void EmitSqlMode(Stmt s)
    {
        curIndent = s.Indent;
        switch (s)
        {
            case CommentStmt c: L(0, c.Text); break;
            case SetOptionStmt o:
                if (!o.Text.Contains("NOCOUNT", StringComparison.OrdinalIgnoreCase)) L(0, "-- entfernt: " + o.Text);
                break;
            case UseStmt u: L(0, $"ALTER SESSION SET CURRENT_SCHEMA = {u.Database};"); break;
            case UnsupportedStmt u: EmitUnsupported(u, 0); break;
            case SqlStmt q:
            {
                var text = ConvertSql(q.Tokens);
                L(0, text);
                if (text.TrimStart().StartsWith("BEGIN") || text.TrimStart().StartsWith("DECLARE")) L(0, "/");
                L(0, "");
                break;
            }
            default:
                // sollte nicht vorkommen – zur Sicherheit als anonymer Block
                EmitAnonymous(new List<Stmt> { s });
                break;
        }
    }

    void EmitAnonymous(List<Stmt> stmts)
    {
        int lead = 0;
        while (lead < stmts.Count && stmts[lead] is CommentStmt c)
        {
            L(0, c.Text);
            lead++;
        }
        var rest = stmts.Skip(lead).ToList();
        ctx.Scope = new UnitScope { IsPlSql = true };
        var body = Capture(() => EmitStmts(rest, 1, top: true));
        if (S.Declarations.Count > 0)
        {
            L(0, "DECLARE");
            foreach (var d in S.Declarations) L(1, d);
        }
        L(0, "BEGIN");
        sb.Append(body);
        if (!HasCode(body)) L(1, "NULL;");
        L(0, "END;");
        L(0, "/");
        L(0, "");
        ctx.Scope = UnitScope.Sql();
    }

    // ================================================================================ Prozeduren / Funktionen

    List<string> EmitParams(List<ParamDef> ps)
    {
        var res = new List<string>();
        foreach (var p in ps)
        {
            var t = TypeMapper.Map(p.Type, TypeContext.Parameter, ctx);
            var ora = O.ParameterPrefix + p.Name;
            S.Vars[p.Name] = new VarInfo { OraName = ora, IsString = t.IsString, IsDate = t.IsDate, IsParam = true };
            if (p.ReadOnly) ctx.Warn($"Tabellenwertparameter @{p.Name} → Collection-Typ/GTT manuell umsetzen.");
            var dir = p.Output ? "IN OUT" : "IN";
            var def = "";
            if (p.Default != null)
            {
                if (p.Output) ctx.Warn($"Default-Wert für OUTPUT-Parameter @{p.Name} entfernt (in Oracle nicht erlaubt).");
                else def = " DEFAULT " + Collapse(ex.Convert(p.Default));
            }
            res.Add($"{ora} {dir} {t.Type}{def}");
        }
        return res;
    }

    void EmitProc(CreateProcStmt p)
    {
        ctx.Scope = new UnitScope { IsPlSql = true };
        var name = ex.RenderName(p.Name);
        var shortName = name.Split('.').Last();
        var ps = EmitParams(p.Params);
        var body = Capture(() => EmitStmts(p.Body, 1, top: true));

        L(0, ps.Count > 0
            ? $"CREATE OR REPLACE PROCEDURE {name} (\n    {string.Join(",\n    ", ps)}\n)"
            : $"CREATE OR REPLACE PROCEDURE {name}");
        L(0, "AS");
        foreach (var d in S.Declarations) L(1, d);
        L(0, "BEGIN");
        sb.Append(body);
        if (!HasCode(body)) L(1, "NULL;");
        L(0, $"END {shortName};");
        L(0, "/");
        L(0, "");
        ctx.Scope = UnitScope.Sql();
    }

    void EmitFunc(CreateFuncStmt f)
    {
        ctx.Scope = new UnitScope { IsPlSql = true, IsFunction = true };
        var name = ex.RenderName(f.Name);
        var shortName = name.Split('.').Last();
        var ps = EmitParams(f.Params);
        var head = $"CREATE OR REPLACE FUNCTION {name}" + (ps.Count > 0 ? $" (\n    {string.Join(",\n    ", ps)}\n)" : "");

        if (f.InlineQuery != null)
        {
            ctx.Warn($"Inline-Tabellenwertfunktion {shortName} → Funktion mit SYS_REFCURSOR (alternativ View, SQL-Macro oder PIPELINED).");
            curIndent = 0;
            var q = Cv(f.InlineQuery);
            L(0, "-- TODO: Inline-Tabellenwertfunktion – als View, SQL-Macro (21c+) oder PIPELINED Function erwägen");
            L(0, head);
            L(0, "RETURN SYS_REFCURSOR");
            L(0, "AS");
            L(1, "c_result SYS_REFCURSOR;");
            L(0, "BEGIN");
            L(1, "OPEN c_result FOR");
            L(2, q + ";");
            L(1, "RETURN c_result;");
            L(0, $"END {shortName};");
            L(0, "/");
            L(0, "");
            ctx.Scope = UnitScope.Sql();
            return;
        }

        var rt = TypeMapper.Map(f.ReturnType ?? new List<Token>(), TypeContext.Return, ctx);
        var body = Capture(() => EmitStmts(f.Body, 1, top: true));
        L(0, head);
        L(0, $"RETURN {rt.Type}");
        L(0, "AS");
        foreach (var d in S.Declarations) L(1, d);
        L(0, "BEGIN");
        sb.Append(body);
        if (!HasCode(body)) L(1, "RETURN NULL;");
        L(0, $"END {shortName};");
        L(0, "/");
        L(0, "");
        ctx.Scope = UnitScope.Sql();
    }

    // ================================================================================ Statements

    void EmitStmts(List<Stmt> list, int ind, bool top = false)
    {
        for (int k = 0; k < list.Count; k++)
        {
            var s = list[k];
            if (top && s is not (DeclareVarStmt or CommentStmt or DeclareCursorStmt or SetOptionStmt)) top = false;
            if (s is SqlStmt ins && TryInsertReturning(ins, list, k, ind, out int consumed))
            {
                k += consumed;
                continue;
            }
            EmitStmt(s, ind, top);
        }
    }

    /// <summary>INSERT ...; SET @id = SCOPE_IDENTITY() → INSERT ... RETURNING id INTO v_id;</summary>
    bool TryInsertReturning(SqlStmt ins, List<Stmt> list, int k, int ind, out int consumed)
    {
        consumed = 0;
        var sig = Tok.Sig(ins.Tokens);
        if (sig.Count == 0 || !sig[0].Is("INSERT") || !S.IsPlSql) return false;
        int j = k + 1;
        while (j < list.Count && list[j] is CommentStmt) j++;
        if (j >= list.Count) return false;
        string? target = null;
        if (list[j] is SetVarStmt { Op: "=" } sv && IsIdentityCall(sv.Expr)) target = sv.Name;
        else if (list[j] is SqlStmt sel)
        {
            // SELECT @id = SCOPE_IDENTITY()
            var ss = Tok.Sig(sel.Tokens);
            if (ss.Count >= 4 && ss[0].Is("SELECT") && ss[1].Kind == TokenKind.Variable && ss[2].IsSym("=") &&
                IsIdentityCall(ss.Skip(3).ToList()))
                target = ss[1].Text.TrimStart('@');
        }
        if (target == null) return false;
        // Nur INSERT ... VALUES (eine Zeile) unterstützt RETURNING INTO
        int vIdx = Tok.FindTopLevel(ins.Tokens, 0, x => x.Is("VALUES"));
        if (vIdx < 0) return false;
        int tIdx = Tok.FindTopLevel(ins.Tokens, 0, x => x.Is("INTO"));
        string? col = null;
        if (tIdx >= 0)
        {
            var chain = new List<Token>();
            int p = Tok.NextSig(ins.Tokens, tIdx + 1);
            chain.Add(ins.Tokens[p]);
            for (int q = p + 1; q + 1 < ins.Tokens.Count && ins.Tokens[q].IsSym("."); q += 2) { chain.Add(ins.Tokens[q]); chain.Add(ins.Tokens[q + 1]); }
            ctx.IdentityColumns.TryGetValue(ex.RenderName(chain), out col);
        }
        curIndent = ins.Indent;
        var text = ConvertSql(ins.Tokens).TrimEnd().TrimEnd(';');
        var v = ex.VarName(target);
        if (col == null) ctx.Warn("SCOPE_IDENTITY → RETURNING: Identity-Spalte unbekannt (Tabelle nicht im Skript) – Spaltennamen eintragen.");
        L(ind, $"{text}\nRETURNING {col ?? "id /* TODO: Identity-Spalte */"} INTO {v};");
        consumed = j - k;
        return true;
    }

    static bool IsIdentityCall(List<Token> expr)
    {
        var s = Tok.Sig(expr);
        return s.Count == 3 && s[0].IsAny("SCOPE_IDENTITY", "IDENT_CURRENT") && s[1].IsSym("(") && s[2].IsSym(")")
               || s.Count == 1 && s[0].Kind == TokenKind.SystemVariable && s[0].Text.Equals("@@IDENTITY", StringComparison.OrdinalIgnoreCase);
    }

    void EmitBody(Stmt s, int ind)
    {
        var list = s is BlockStmt b ? b.Body : new List<Stmt> { s };
        var text = Capture(() => EmitStmts(list, ind));
        sb.Append(text);
        if (!HasCode(text)) L(ind, "NULL;");
    }

    void EmitBody(List<Stmt> list, int ind)
    {
        var text = Capture(() => EmitStmts(list, ind));
        sb.Append(text);
        if (!HasCode(text)) L(ind, "NULL;");
    }

    void EmitStmt(Stmt s, int ind, bool top)
    {
        curIndent = s.Indent;
        switch (s)
        {
            case CommentStmt c: L(ind, c.Text); break;
            case BlockStmt b: EmitStmts(b.Body, ind); break;
            case IfStmt i: EmitIf(i, ind); break;
            case WhileStmt w: EmitWhile(w, ind); break;
            case TryCatchStmt tc: EmitTryCatch(tc, ind); break;
            case DeclareVarStmt d: EmitDeclare(d, ind, top); break;
            case DeclareTableVarStmt tv: EmitTableVar(tv, ind); break;
            case DeclareCursorStmt dc: EmitCursorDecl(dc); break;
            case SetVarStmt sv:
                EmitAssign(ex.VarName(sv.Name), sv.Op, sv.Expr, ind,
                    S.Vars.TryGetValue(sv.Name, out var vi) && vi.IsString);
                break;
            case SetOptionStmt o:
                if (!o.Text.Contains("NOCOUNT", StringComparison.OrdinalIgnoreCase)) L(ind, "-- entfernt: " + o.Text);
                break;
            case PrintStmt p:
            {
                var pre = new List<string>();
                var e = Collapse(ex.Convert(Hoist(p.Expr, pre)));
                foreach (var x in pre) L(ind, x);
                L(ind, $"DBMS_OUTPUT.PUT_LINE({e});");
                break;
            }
            case ReturnStmt r: EmitReturn(r, ind); break;
            case RaiserrorStmt re: EmitRaiserror(re, ind); break;
            case ThrowStmt th: EmitThrow(th, ind); break;
            case ExecStmt e: EmitExec(e, ind); break;
            case TranStmt t: EmitTran(t, ind); break;
            case CursorOpStmt co: EmitCursorOp(co, ind); break;
            case BreakStmt: L(ind, "EXIT;"); break;
            case ContinueStmt: L(ind, "CONTINUE;"); break;
            case GotoStmt g: L(ind, $"GOTO {g.Label};"); break;
            case LabelStmt lb: L(ind, $"<<{lb.Label}>>"); L(ind, "NULL;"); break;
            case UseStmt u: L(ind, $"-- USE {u.Database}: ggf. ALTER SESSION SET CURRENT_SCHEMA = {u.Database}"); break;
            case UnsupportedStmt u: EmitUnsupported(u, ind); break;
            case SqlStmt q: L(ind, ConvertSql(q.Tokens)); break;
            case CreateProcStmt or CreateFuncStmt:
                ctx.Warn("CREATE PROCEDURE/FUNCTION muss am Batch-Anfang stehen (GO fehlt?).");
                L(ind, "-- TODO: verschachteltes CREATE PROCEDURE/FUNCTION");
                break;
        }
    }

    void EmitUnsupported(UnsupportedStmt u, int ind)
    {
        ctx.Warn("Nicht konvertiert: " + u.Reason);
        L(ind, $"-- TODO (nicht konvertiert): {u.Reason}");
        foreach (var line in u.Original.Replace("\r", "").Split('\n'))
            L(ind, "-- " + line);
    }

    // ---------------------------------------------------------------- Kontrollfluss

    void EmitIf(IfStmt s, int ind)
    {
        var branches = new List<(List<Token> Cond, Stmt Body)> { (s.Cond, s.Then) };
        Stmt? elseBody = s.Else;
        while (true)
        {
            var nested = elseBody switch
            {
                IfStmt i => i,
                BlockStmt { Body.Count: 1 } b when b.Body[0] is IfStmt i2 => i2,
                _ => null
            };
            if (nested == null) break;
            branches.Add((nested.Cond, nested.Then));
            elseBody = nested.Else;
        }

        // IF @@TRANCOUNT > 0 ROLLBACK/COMMIT → in Oracle ohne Bedingung
        if (branches.Count == 1 && elseBody == null && Regex.IsMatch(Collapse(Tok.Text(s.Cond)), @"^\(?\s*@@TRANCOUNT\s*(>\s*0|<>\s*0|>=\s*1)\s*\)?$", RegexOptions.IgnoreCase))
        {
            L(ind, "-- IF @@TRANCOUNT > 0: in Oracle nicht nötig");
            EmitStmts(s.Then is BlockStmt bb ? bb.Body : new List<Stmt> { s.Then }, ind);
            return;
        }

        // IF OBJECT_ID('tempdb..#x') IS NOT NULL DROP TABLE #x → GTT existiert dauerhaft, nur leeren
        if (branches.Count == 1 && elseBody == null && S.IsPlSql &&
            Regex.IsMatch(Tok.Text(s.Cond), @"OBJECT_ID\s*\(\s*N?'tempdb\.", RegexOptions.IgnoreCase))
        {
            EmitStmts(s.Then is BlockStmt b2 ? b2.Body : new List<Stmt> { s.Then }, ind);
            return;
        }

        var pre = new List<string>();
        var conds = branches.Select(b => Collapse(ex.Convert(Hoist(b.Cond, pre)))).ToList();
        foreach (var x in pre) L(ind, x);

        for (int k = 0; k < branches.Count; k++)
        {
            L(ind, $"{(k == 0 ? "IF" : "ELSIF")} {conds[k]} THEN");
            EmitBody(branches[k].Body, ind + 1);
        }
        if (elseBody != null)
        {
            L(ind, "ELSE");
            EmitBody(elseBody, ind + 1);
        }
        L(ind, "END IF;");
    }

    void EmitWhile(WhileStmt w, int ind)
    {
        var pre = new List<string>();
        var cond = Collapse(ex.Convert(Hoist(w.Cond, pre)));
        foreach (var x in pre) L(ind, x);
        L(ind, $"WHILE {cond} LOOP");
        EmitBody(w.Body, ind + 1);
        if (pre.Count > 0)
        {
            L(ind + 1, "-- Bedingung neu auswerten");
            foreach (var x in pre) L(ind + 1, x);
        }
        L(ind, "END LOOP;");
    }

    void EmitTryCatch(TryCatchStmt tc, int ind)
    {
        L(ind, "BEGIN");
        EmitBody(tc.Try, ind + 1);
        L(ind, "EXCEPTION");
        L(ind + 1, "WHEN OTHERS THEN");

        bool oc = S.UsedErrCode, om = S.UsedErrMsg, ol = S.UsedErrLine;
        S.UsedErrCode = S.UsedErrMsg = S.UsedErrLine = false;
        S.CatchDepth++;
        var body = Capture(() => EmitStmts(tc.Catch, ind + 2));
        S.CatchDepth--;

        if (S.UsedErrCode) { S.Declare("v_err_code", "v_err_code NUMBER;"); L(ind + 2, "v_err_code := SQLCODE;"); }
        if (S.UsedErrMsg) { S.Declare("v_err_msg", "v_err_msg VARCHAR2(4000);"); L(ind + 2, "v_err_msg := SQLERRM;"); }
        if (S.UsedErrLine)
        {
            S.Declare("v_err_line", "v_err_line VARCHAR2(4000);");
            L(ind + 2, "v_err_line := DBMS_UTILITY.FORMAT_ERROR_BACKTRACE;");
        }
        sb.Append(body);
        if (!HasCode(body)) L(ind + 2, "NULL;");
        S.UsedErrCode |= oc; S.UsedErrMsg |= om; S.UsedErrLine |= ol;
        L(ind, "END;");
    }

    // ---------------------------------------------------------------- Deklarationen / Zuweisungen

    void EmitDeclare(DeclareVarStmt d, int ind, bool top)
    {
        foreach (var v in d.Vars)
        {
            var t = TypeMapper.Map(v.Type, TypeContext.Variable, ctx);
            var ora = O.LocalVariablePrefix + v.Name;
            S.Vars[v.Name] = new VarInfo { OraName = ora, IsString = t.IsString, IsDate = t.IsDate, OraType = t.Type };
            if (v.Default == null)
            {
                S.Declare(ora, $"{ora} {t.Type};");
                continue;
            }
            bool simple = top && !ContainsSubquery(v.Default);
            if (simple)
                S.Declare(ora, $"{ora} {t.Type} := {ValueFor(ora, v.Default, t)};");
            else
            {
                S.Declare(ora, $"{ora} {t.Type};");
                EmitAssign(ora, "=", v.Default, ind, t.IsString);
            }
        }
    }

    string ValueFor(string target, List<Token> expr, OraType t)
    {
        var tr = Tok.Trim(expr);
        if (t.IsDate && tr.Count == 1 && tr[0].Kind is TokenKind.String or TokenKind.NString)
            return ex.DateLiteral(tr[0], t.Type.StartsWith("TIMESTAMP"));
        return Collapse(ex.Convert(expr));
    }

    VarInfo? VarByOra(string ora) => S.Vars.Values.FirstOrDefault(v => v.OraName == ora);

    void EmitTableVar(DeclareTableVarStmt tv, int ind)
    {
        var gtt = "gtt_" + tv.Name;
        S.Vars[tv.Name] = new VarInfo { OraName = gtt, IsTable = true };
        ctx.Warn($"Tabellenvariable @{tv.Name} → Global Temporary Table {gtt} (DDL einmalig ausführen).");
        var cols = Tok.SplitTopLevel(tv.Definition).Select(Tok.Trim).Where(c => c.Count > 0)
            .Select(ConvertColumnOrConstraint).Where(c => c != null).ToList();
        AddPrereq(gtt, $"CREATE GLOBAL TEMPORARY TABLE {gtt} (\n    {string.Join(",\n    ", cols)}\n) ON COMMIT DELETE ROWS;");
        L(ind, $"DELETE FROM {gtt}; -- Tabellenvariable @{tv.Name} → GTT (siehe Vorab-DDL)");
    }

    void AddPrereq(string name, string ddl)
    {
        if (ctx.PrereqNames.Add(name)) ctx.PrereqDdl.Add(ddl);
    }

    void EmitCursorDecl(DeclareCursorStmt dc)
    {
        var q = Cv(dc.Query);
        S.Vars[dc.Name] = new VarInfo { OraName = dc.Name };
        S.Declare(dc.Name, $"CURSOR {dc.Name} IS\n{IndentBlock(q, 1)};");
    }

    bool ContainsSubquery(List<Token> tokens)
    {
        for (int k = 0; k + 1 < tokens.Count; k++)
        {
            if (!tokens[k].IsSym("(")) continue;
            var nx = tokens[Tok.NextSig(tokens, k + 1)];
            if (nx.Is("SELECT")) return true;
        }
        return false;
    }

    void EmitAssign(string target, string op, List<Token> expr, int ind, bool targetIsString)
    {
        var trimmed = Tok.Trim(expr);
        if (op == "=" && Tok.IsParenSubquery(trimmed, out var inner))
        {
            foreach (var line in SelectInto(inner, new List<string> { target })) L(ind, line);
            return;
        }
        var pre = new List<string>();
        var vi = VarByOra(target);
        var value = vi is { IsDate: true } && trimmed.Count == 1 && trimmed[0].Kind is TokenKind.String or TokenKind.NString
            ? ex.DateLiteral(trimmed[0], vi.OraType.StartsWith("TIMESTAMP"))
            : Collapse(ex.Convert(Hoist(expr, pre)));
        foreach (var x in pre) L(ind, x);
        string rhs = op switch
        {
            "=" => value,
            "+=" => targetIsString || ex.IsStringy(expr) ? $"{target} || {W(value)}" : $"{target} + {W(value)}",
            "-=" => $"{target} - {W(value)}",
            "*=" => $"{target} * {W(value)}",
            "/=" => $"{target} / {W(value)}",
            "%=" => $"MOD({target}, {value})",
            "&=" => $"BITAND({target}, {value})",
            _ => value
        };
        L(ind, $"{target} := {rhs};");
    }

    static string W(string s)
    {
        s = s.Trim();
        return Regex.IsMatch(s, @"^[\w.$#""%:']+$") ? s : "(" + s + ")";
    }

    /// <summary>
    /// PL/SQL erlaubt keine Unterabfragen in Ausdrücken (IF EXISTS(...), x = (SELECT ...)).
    /// Diese werden in Hilfsvariablen ausgelagert; die SELECT-INTO-Statements landen in <paramref name="pre"/>.
    /// </summary>
    List<Token> Hoist(List<Token> tokens, List<string> pre)
    {
        if (!S.IsPlSql) return tokens;
        var res = new List<Token>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Is("EXISTS"))
            {
                int p = Tok.NextSig(tokens, i + 1);
                if (p < tokens.Count && tokens[p].IsSym("("))
                {
                    int close = Tok.MatchParen(tokens, p);
                    var sub = tokens.GetRange(p + 1, Math.Max(0, Math.Min(close, tokens.Count) - p - 1));
                    var v = S.NewName("v_exists");
                    S.Declare(v, $"{v} NUMBER(1);");
                    var q = Reindent(ex.Convert(sub).Trim(), curIndent);
                    pre.Add($"SELECT CASE WHEN EXISTS (\n{IndentBlock(q, 1)}\n) THEN 1 ELSE 0 END INTO {v} FROM DUAL;");
                    res.Add(Token.Raw($"{v} = 1"));
                    i = close;
                    continue;
                }
            }
            if (t.IsSym("("))
            {
                int close = Tok.MatchParen(tokens, i);
                var nx = tokens[Math.Min(Tok.NextSig(tokens, i + 1), tokens.Count - 1)];
                bool prevIsIn = res.Count > 0 && Tok.Sig(res).LastOrDefault() is { } pv && pv.IsAny("IN", "ANY", "ALL", "SOME");
                if (nx.Is("SELECT") && !prevIsIn)
                {
                    var sub = tokens.GetRange(i + 1, Math.Max(0, Math.Min(close, tokens.Count) - i - 1));
                    var v = S.NewName("v_sub");
                    S.Declare(v, $"{v} {InferSubqueryType(sub)};");
                    pre.AddRange(SelectInto(sub, new List<string> { v }));
                    res.Add(Token.Raw(v));
                    i = close;
                    continue;
                }
                if (nx.Is("SELECT") && prevIsIn)
                {
                    ctx.Warn("IN (SELECT ...) in einer PL/SQL-Bedingung ist nicht erlaubt → in EXISTS/COUNT umformen.");
                }
            }
            res.Add(t);
        }
        return res;
    }

    string InferSubqueryType(List<Token> sub)
    {
        var (listStart, listEnd) = SelectListBounds(Tok.Trim(sub));
        var trimmed = Tok.Trim(sub);
        if (listStart < 0) return "VARCHAR2(4000)";
        var item = Tok.Sig(trimmed.GetRange(listStart, listEnd - listStart));
        if (item.Count > 0 && item[0].IsAny("COUNT", "COUNT_BIG", "SUM", "AVG")) return "NUMBER";
        // Spalte (ggf. in MAX/MIN) einer einzelnen Tabelle → %TYPE
        var colTokens = item;
        if (item.Count > 3 && item[0].IsAny("MAX", "MIN") && item[1].IsSym("(")) colTokens = item.Skip(2).Take(item.Count - 3).ToList();
        var fromIdx = Tok.FindTopLevel(trimmed, listEnd, x => x.Is("FROM"));
        if (fromIdx >= 0 && colTokens.Count is 1 or 3 && colTokens[^1].IsName)
        {
            var after = Tok.Sig(trimmed.GetRange(fromIdx + 1, trimmed.Count - fromIdx - 1));
            bool hasJoin = after.Any(x => x.Is("JOIN") || x.IsSym(","));
            if (after.Count > 0 && after[0].IsName && !hasJoin)
            {
                var chain = new List<Token> { after[0] };
                for (int k = 1; k + 1 < after.Count && after[k].IsSym("."); k += 2) { chain.Add(after[k]); chain.Add(after[k + 1]); }
                return $"{ex.RenderName(chain)}.{ex.Ident(colTokens[^1])}%TYPE";
            }
        }
        ctx.Warn("Typ einer ausgelagerten Unterabfrage geschätzt (VARCHAR2(4000)) – bitte prüfen.");
        return "VARCHAR2(4000)";
    }

    void EmitReturn(ReturnStmt r, int ind)
    {
        if (r.Expr == null || r.Expr.Count == 0)
        {
            L(ind, S.IsFunction ? "RETURN NULL;" : "RETURN;");
            return;
        }
        if (!S.IsFunction)
        {
            ctx.Warn("RETURN <wert> in Prozeduren → in Oracle als OUT-Parameter abbilden.");
            L(ind, $"RETURN; -- TODO: Rückgabewert {Collapse(Tok.Text(r.Expr))} → OUT-Parameter");
            return;
        }
        var trimmed = Tok.Trim(r.Expr);
        if (Tok.IsParenSubquery(trimmed, out var inner))
        {
            var v = S.NewName("v_ret");
            S.Declare(v, $"{v} {InferSubqueryType(inner)};");
            foreach (var line in SelectInto(inner, new List<string> { v })) L(ind, line);
            L(ind, $"RETURN {v};");
            return;
        }
        var pre = new List<string>();
        var e = Collapse(ex.Convert(Hoist(r.Expr, pre)));
        foreach (var x in pre) L(ind, x);
        L(ind, $"RETURN {e};");
    }

    // ---------------------------------------------------------------- Fehler

    void EmitRaiserror(RaiserrorStmt re, int ind)
    {
        if (re.Args.Count == 0) { L(ind, "RAISE_APPLICATION_ERROR(-20000, 'Fehler');"); return; }
        var msgTok = Tok.Trim(re.Args[0]);
        var msgArgs = re.Args.Skip(3).Select(a => ex.Convert(a).Trim()).ToList();
        string msg;
        if (msgTok.Count == 1 && msgTok[0].Kind is TokenKind.String or TokenKind.NString)
            msg = FormatMessage(msgTok[0].StringContent, msgArgs);
        else if (msgTok.Count == 1 && msgTok[0].Kind == TokenKind.Number)
        {
            ctx.Warn("RAISERROR mit Meldungsnummer (sys.messages) → Meldungstext manuell einsetzen.");
            msg = $"'Fehler {msgTok[0].Text}'";
        }
        else msg = Collapse(ex.Convert(msgTok));

        int sev = 16;
        if (re.Args.Count > 1) int.TryParse(Tok.Text(Tok.Trim(re.Args[1])), out sev);
        if (sev <= 10) L(ind, $"DBMS_OUTPUT.PUT_LINE({msg});");
        else L(ind, $"RAISE_APPLICATION_ERROR(-20000, {msg});");
    }

    static string FormatMessage(string content, List<string> args)
    {
        if (args.Count == 0) return "'" + content + "'";
        var parts = new List<string>();
        int argIdx = 0, last = 0;
        foreach (Match m in Regex.Matches(content, @"%[-+ 0#]*\d*(\.\d+)?[sdiuxXo]"))
        {
            if (m.Index > last) parts.Add("'" + content[last..m.Index] + "'");
            if (argIdx < args.Count)
            {
                var a = args[argIdx++];
                parts.Add(m.Value.EndsWith('s') ? a : $"TO_CHAR({a})");
            }
            last = m.Index + m.Length;
        }
        if (last < content.Length) parts.Add("'" + content[last..] + "'");
        return string.Join(" || ", parts);
    }

    void EmitThrow(ThrowStmt th, int ind)
    {
        if (th.Args == null || th.Args.Count < 2)
        {
            if (S.CatchDepth == 0) ctx.Warn("THROW ohne Argumente außerhalb von CATCH → RAISE ist nur im EXCEPTION-Block gültig.");
            L(ind, "RAISE;");
            return;
        }
        var codeText = Tok.Text(Tok.Trim(th.Args[0]));
        string code;
        if (int.TryParse(codeText, out var n))
        {
            int ora = n is >= 50000 and <= 50999 ? -20000 - (n - 50000) : -20000;
            code = ora.ToString();
        }
        else
        {
            ctx.Warn("THROW mit variabler Fehlernummer → RAISE_APPLICATION_ERROR braucht -20000..-20999.");
            code = "-20000";
        }
        L(ind, $"RAISE_APPLICATION_ERROR({code}, {Collapse(ex.Convert(th.Args[1]))});");
    }

    // ---------------------------------------------------------------- EXEC

    void EmitExec(ExecStmt e, int ind)
    {
        var tk = Tok.Trim(e.Tokens);
        if (tk.Count == 0) return;

        if (tk[0].IsSym("("))
        {
            int close = Tok.MatchParen(tk, 0);
            var inner = tk.GetRange(1, Math.Max(0, Math.Min(close, tk.Count) - 1));
            ctx.Warn("Dynamisches SQL (EXEC) → EXECUTE IMMEDIATE: SQL-Text muss Oracle-Syntax enthalten.");
            var it = Tok.Trim(inner);
            var dyn = it.Count == 1 && it[0].Kind is TokenKind.String or TokenKind.NString ? DynSql(it[0]) : Collapse(ex.Convert(inner));
            L(ind, $"EXECUTE IMMEDIATE {dyn};");
            return;
        }

        string? rcTarget = null;
        if (tk[0].Kind == TokenKind.Variable && tk.Count > 2 && tk[Tok.NextSig(tk, 1)].IsSym("="))
        {
            rcTarget = ex.VarName(tk[0].Text);
            tk = Tok.Trim(tk.GetRange(Tok.NextSig(tk, 1) + 1, tk.Count - Tok.NextSig(tk, 1) - 1));
        }
        if (tk[0].Kind == TokenKind.Variable)
        {
            ctx.Warn("EXEC @prozedurname → EXECUTE IMMEDIATE 'BEGIN ' || name || '; END;'.");
            L(ind, $"EXECUTE IMMEDIATE 'BEGIN ' || {ex.VarName(tk[0].Text)} || '; END;';");
            return;
        }

        // Prozedurname
        var chain = new List<Token> { tk[0] };
        int k = 1;
        while (k + 1 < tk.Count && tk[k].IsSym(".")) { chain.Add(tk[k]); chain.Add(tk[k + 1]); k += 2; }
        var procName = ex.RenderName(chain);
        var shortName = chain[^1].Name.ToLowerInvariant();
        var argTokens = tk.GetRange(k, tk.Count - k);
        var args = Tok.SplitTopLevel(argTokens).Select(Tok.Trim).Where(a => a.Count > 0).ToList();

        if (shortName == "sp_executesql")
        {
            EmitSpExecuteSql(args, ind);
            return;
        }
        if (shortName.StartsWith("sp_") || shortName.StartsWith("xp_"))
        {
            ctx.Warn($"Systemprozedur {shortName} hat kein direktes Oracle-Pendant.");
            L(ind, $"-- TODO: Systemprozedur {Collapse(Tok.Text(tk))}");
            return;
        }

        var outArgs = new List<string>();
        foreach (var a in args)
        {
            var sig = Tok.Sig(a);
            var valueTokens = a;
            string? named = null;
            if (sig.Count > 2 && sig[0].Kind == TokenKind.Variable && sig[1].IsSym("="))
            {
                named = O.ParameterPrefix + sig[0].Text.TrimStart('@');
                int eq = a.IndexOf(sig[1]);
                valueTokens = a.GetRange(eq + 1, a.Count - eq - 1);
            }
            valueTokens = Tok.Trim(valueTokens);
            if (valueTokens.Count > 0 && valueTokens[^1].IsAny("OUTPUT", "OUT"))
                valueTokens = Tok.Trim(valueTokens.GetRange(0, valueTokens.Count - 1));
            if (valueTokens.Count == 1 && valueTokens[0].Is("DEFAULT"))
            {
                if (named == null) ctx.Warn("EXEC mit positionalem DEFAULT → benannte Parameter verwenden.");
                continue;
            }
            var value = Collapse(ex.Convert(valueTokens));
            outArgs.Add(named != null ? $"{named} => {value}" : value);
        }
        var call = outArgs.Count > 0 ? $"{procName}({string.Join(", ", outArgs)});" : $"{procName};";
        if (rcTarget != null)
        {
            ctx.Warn("EXEC @rc = prozedur → Rückgabestatus als OUT-Parameter oder Funktion umsetzen.");
            call += $" -- TODO: Rückgabewert nach {rcTarget}";
        }
        L(ind, call);
    }

    void EmitSpExecuteSql(List<List<Token>> args, int ind)
    {
        if (args.Count == 0) return;
        var stmtTok = args[0];
        string stmt;
        if (stmtTok.Count == 1 && stmtTok[0].Kind is TokenKind.String or TokenKind.NString)
            stmt = DynSql(stmtTok[0]);
        else
        {
            ctx.Warn("sp_executesql: Platzhalter @name im dynamischen SQL durch :name ersetzen.");
            stmt = Collapse(ex.Convert(stmtTok));
        }
        var values = new List<string>();
        foreach (var a in args.Skip(2))
        {
            var sig = Tok.Sig(a);
            var vt = a;
            if (sig.Count > 2 && sig[0].Kind == TokenKind.Variable && sig[1].IsSym("="))
            {
                int eq = a.IndexOf(sig[1]);
                vt = a.GetRange(eq + 1, a.Count - eq - 1);
            }
            vt = Tok.Trim(vt);
            bool output = vt.Count > 0 && vt[^1].IsAny("OUTPUT", "OUT");
            if (output) vt = Tok.Trim(vt.GetRange(0, vt.Count - 1));
            values.Add((output ? "OUT " : "") + Collapse(ex.Convert(vt)));
        }
        ctx.Warn("sp_executesql → EXECUTE IMMEDIATE ... USING: dynamisches SQL auf Oracle-Syntax prüfen.");
        L(ind, values.Count > 0 ? $"EXECUTE IMMEDIATE {stmt} USING {string.Join(", ", values)};" : $"EXECUTE IMMEDIATE {stmt};");
    }

    /// <summary>Konvertiert ein dynamisches SQL-Literal rekursiv (Platzhalter @x → :x).</summary>
    string DynSql(Token lit)
    {
        var content = lit.StringContent.Replace("''", "'");
        content = Regex.Replace(content, @"(?<!@)@(\w+)", ":$1");
        var nested = new TSqlToPlSqlConverter(new ConversionOptions
        {
            Target = O.Target, IncludeHeader = false, RemoveDboSchema = O.RemoveDboSchema,
            LocalVariablePrefix = O.LocalVariablePrefix, ParameterPrefix = O.ParameterPrefix
        }).Convert(content);
        foreach (var w in nested.Warnings) ctx.Warn(w);
        var sql = nested.Output.Trim();
        if (sql.EndsWith(';') && !sql.EndsWith("END;")) sql = sql[..^1];
        if (sql.Contains("\n/")) ctx.Warn("Dynamisches SQL enthält mehrere Statements/PL/SQL-Blöcke – prüfen.");
        return "'" + Collapse(sql).Replace("'", "''") + "'";
    }

    // ---------------------------------------------------------------- Transaktionen / Cursor

    void EmitTran(TranStmt t, int ind)
    {
        switch (t.Kind)
        {
            case "BEGIN":
                L(ind, "-- BEGIN TRANSACTION: in Oracle implizit (Transaktion beginnt mit der ersten DML)");
                break;
            case "COMMIT":
                if (S.IsFunction) ctx.Warn("COMMIT in Funktion → PRAGMA AUTONOMOUS_TRANSACTION nötig.");
                L(ind, "COMMIT;");
                break;
            case "ROLLBACK":
                L(ind, t.Name != null && S.Savepoints.Contains(t.Name) ? $"ROLLBACK TO SAVEPOINT {t.Name};" : "ROLLBACK;");
                break;
            case "SAVE":
                if (t.Name != null) S.Savepoints.Add(t.Name);
                L(ind, $"SAVEPOINT {t.Name};");
                break;
        }
    }

    void EmitCursorOp(CursorOpStmt co, int ind)
    {
        var c = co.Cursor.StartsWith('@') ? ex.VarName(co.Cursor) : co.Cursor;
        switch (co.Op)
        {
            case "OPEN": S.LastCursor = c; L(ind, $"OPEN {c};"); break;
            case "CLOSE": L(ind, $"CLOSE {c};"); break;
            case "DEALLOCATE": break; // in Oracle nicht nötig
            case "FETCH":
                if (co.Direction != "NEXT") ctx.Warn($"FETCH {co.Direction} → Oracle-Cursor sind nur vorwärts lesbar.");
                S.LastCursor = c;
                var into = co.Into is { Count: > 0 } ? " INTO " + string.Join(", ", co.Into.Select(v => ex.VarName(v.Text))) : "";
                L(ind, $"FETCH {c}{into};");
                break;
        }
    }
}
