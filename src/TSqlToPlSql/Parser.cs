namespace TSqlToPlSql;

/// <summary>
/// Pragmatischer T-SQL-Parser: erkennt Statement-Grenzen und Kontrollstrukturen.
/// Ausdrücke und DML bleiben Token-Listen und werden später vom <see cref="ExprConverter"/> umgesetzt.
/// </summary>
internal sealed class Parser
{
    readonly List<Token> t;
    readonly Ctx ctx;
    int pos;

    internal static readonly HashSet<string> StatementStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","INSERT","UPDATE","DELETE","MERGE","DECLARE","SET","IF","ELSE","WHILE","BEGIN","END","PRINT",
        "RETURN","EXEC","EXECUTE","CREATE","ALTER","DROP","TRUNCATE","RAISERROR","THROW","OPEN","FETCH","CLOSE",
        "DEALLOCATE","COMMIT","ROLLBACK","SAVE","BREAK","CONTINUE","GOTO","WITH","USE","GO","WAITFOR","GRANT",
        "REVOKE","DENY"
    };

    public Parser(List<Token> tokens, Ctx ctx)
    {
        t = new List<Token>(tokens);
        if (t.Count == 0 || !t[^1].IsEof) t.Add(Token.Eof());
        this.ctx = ctx;
    }

    // ---------------------------------------------------------------- Navigation

    int Last => t.Count - 1;

    int Sig(int i)
    {
        i = Math.Min(i, Last);
        while (i < Last && t[i].IsTrivia) i++;
        return i;
    }

    Token Peek(int n = 0)
    {
        int i = Sig(pos);
        for (int k = 0; k < n; k++) i = Sig(i + 1);
        return t[i];
    }

    Token Next()
    {
        int i = Sig(pos);
        pos = Math.Min(i + 1, Last);
        return t[i];
    }

    bool AcceptKw(string kw) { if (Peek().Is(kw)) { Next(); return true; } return false; }
    bool AcceptSym(string s) { if (Peek().IsSym(s)) { Next(); return true; } return false; }

    List<string> TakeComments()
    {
        var res = new List<string>();
        int i = pos;
        while (i < Last && t[i].IsTrivia)
        {
            if (t[i].IsComment) res.Add(t[i].Text);
            i++;
        }
        pos = i;
        return res;
    }

    int IndentOf(int idx)
    {
        if (idx > 0 && t[idx - 1].Kind == TokenKind.Whitespace)
        {
            var w = t[idx - 1].Text;
            int nl = w.LastIndexOf('\n');
            if (nl >= 0) return w.Length - nl - 1;
            if (idx - 1 == 0) return w.Length;
        }
        return 0;
    }

    string RawText(int from, int to) => Tok.Text(t.GetRange(from, Math.Max(0, to - from))).Trim();

    List<Token> TakeParenGroup()
    {
        int i = Sig(pos);
        int j = Math.Min(Tok.MatchParen(t, i), Last - 1);
        var g = t.GetRange(i, j - i + 1);
        pos = j + 1;
        return g;
    }

    List<Token> NameChain()
    {
        var res = new List<Token> { Next() };
        while (Peek().IsSym(".")) { res.Add(Next()); res.Add(Next()); }
        return res;
    }

    // ---------------------------------------------------------------- Statements

    public List<Stmt> ParseBatch() => ParseStatements(_ => false);

    List<Stmt> ParseStatements(Func<Token, bool> stop)
    {
        var list = new List<Stmt>();
        while (true)
        {
            foreach (var c in TakeComments()) list.Add(new CommentStmt(c));
            var tk = Peek();
            if (tk.IsEof || stop(tk)) break;
            if (tk.IsSym(";")) { Next(); continue; }
            int before = pos;
            var s = ParseStatement();
            if (s != null) list.Add(s);
            if (pos == before)
            {
                var bad = Next();
                list.Add(new UnsupportedStmt(bad.Text, "Unerwartetes Token"));
            }
        }
        return list;
    }

    Stmt? ParseStatement()
    {
        var k = Peek();
        int start = Sig(pos);
        int indent = IndentOf(start);
        var s = ParseStatementCore(k, start);
        if (s != null) s.Indent = indent;
        return s;
    }

    Stmt? ParseStatementCore(Token k, int start)
    {
        if (k.Kind != TokenKind.Identifier)
            return new UnsupportedStmt(Tok.Text(CollectStatement()), "Unbekanntes Statement");

        switch (k.Upper)
        {
            case "BEGIN": return ParseBegin();
            case "IF": return ParseIf();
            case "WHILE": return ParseWhile();
            case "DECLARE": return ParseDeclare();
            case "SET": return ParseSet();
            case "PRINT": Next(); return new PrintStmt(CollectExpr(false));
            case "RETURN":
                Next();
                return new ReturnStmt(IsExprStart() ? CollectExpr(false) : null);
            case "RAISERROR": return ParseRaiserror();
            case "THROW":
                Next();
                if (IsExprStart())
                    return new ThrowStmt(Tok.SplitTopLevel(CollectExpr(false)).Select(Tok.Trim).ToList());
                return new ThrowStmt(null);
            case "EXEC":
            case "EXECUTE":
                Next();
                return new ExecStmt(CollectExpr(false));
            case "COMMIT":
            case "ROLLBACK":
            {
                Next();
                bool tranWord = AcceptKw("TRAN") || AcceptKw("TRANSACTION") || AcceptKw("WORK");
                string? name = null;
                if (tranWord && (Peek().Kind is TokenKind.Identifier or TokenKind.Variable) &&
                    !StatementStart.Contains(Peek().Upper))
                    name = Next().Text;
                return new TranStmt(k.Upper, name);
            }
            case "SAVE":
                Next();
                if (!(AcceptKw("TRAN") || AcceptKw("TRANSACTION"))) break;
                return new TranStmt("SAVE", Next().Text);
            case "OPEN":
                Next(); AcceptKw("GLOBAL");
                return new CursorOpStmt("OPEN", Next().Text, null, "");
            case "FETCH": return ParseFetch();
            case "CLOSE":
            case "DEALLOCATE":
                Next(); AcceptKw("GLOBAL");
                return new CursorOpStmt(k.Upper, Next().Text, null, "");
            case "BREAK": Next(); return new BreakStmt();
            case "CONTINUE": Next(); return new ContinueStmt();
            case "GOTO": Next(); return new GotoStmt(Next().Text);
            case "USE": Next(); return new UseStmt(Next().Name);
            case "GO": Next(); return null;
            case "CREATE":
            case "ALTER":
                return ParseCreate(start);
            case "WAITFOR":
                return new UnsupportedStmt(Tok.Text(CollectStatement()), "WAITFOR → DBMS_SESSION.SLEEP / DBMS_LOCK.SLEEP");
        }

        // Label?
        if (Peek(1).IsSym(":") && !StatementStart.Contains(k.Upper))
        {
            var name = Next().Text; Next();
            return new LabelStmt(name);
        }

        if (StatementStart.Contains(k.Upper))
            return new SqlStmt(CollectStatement());

        // Prozeduraufruf ohne EXEC (nur am Batch-Anfang zulässig)
        return new ExecStmt(CollectExpr(false));
    }

    Stmt ParseBegin()
    {
        Next(); // BEGIN
        if (AcceptKw("TRY"))
        {
            var tryBody = ParseStatements(x => x.Is("END"));
            AcceptKw("END"); AcceptKw("TRY");
            TakeComments();
            AcceptSym(";");
            TakeComments();
            var catchBody = new List<Stmt>();
            if (Peek().Is("BEGIN") && Peek(1).Is("CATCH"))
            {
                Next(); Next();
                catchBody = ParseStatements(x => x.Is("END"));
                AcceptKw("END"); AcceptKw("CATCH");
            }
            return new TryCatchStmt(tryBody, catchBody);
        }
        if (Peek().IsAny("TRAN", "TRANSACTION", "DISTRIBUTED"))
        {
            AcceptKw("DISTRIBUTED");
            Next();
            string? name = null;
            if ((Peek().Kind is TokenKind.Identifier or TokenKind.Variable) && !StatementStart.Contains(Peek().Upper))
                name = Next().Text;
            return new TranStmt("BEGIN", name);
        }
        var body = ParseStatements(x => x.Is("END"));
        AcceptKw("END");
        return new BlockStmt(body);
    }

    Stmt ParseStatementWithComments()
    {
        var comments = TakeComments();
        var s = ParseStatement() ?? new BlockStmt(new());
        if (comments.Count == 0) return s;
        var body = comments.Select(c => (Stmt)new CommentStmt(c)).ToList();
        body.Add(s);
        return new BlockStmt(body);
    }

    Stmt ParseIf()
    {
        Next();
        var cond = CollectExpr(false);
        var then = ParseStatementWithComments();
        int save = pos;
        TakeComments();
        while (AcceptSym(";")) TakeComments();
        if (AcceptKw("ELSE"))
            return new IfStmt(cond, then, ParseStatementWithComments());
        pos = save;
        return new IfStmt(cond, then, null);
    }

    Stmt ParseWhile()
    {
        Next();
        var cond = CollectExpr(false);
        return new WhileStmt(cond, ParseStatementWithComments());
    }

    Stmt ParseDeclare()
    {
        Next();
        // Cursor: DECLARE c [INSENSITIVE|SCROLL] CURSOR [Optionen] FOR SELECT ...
        if (Peek().Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier &&
            Peek(1).IsAny("CURSOR", "INSENSITIVE", "SCROLL"))
        {
            var name = Next().Name;
            while (!Peek().Is("FOR") && !Peek().IsEof) Next();
            AcceptKw("FOR");
            return new DeclareCursorStmt(name, CollectStatement());
        }

        var vars = new List<VarDecl>();
        while (Peek().Kind == TokenKind.Variable)
        {
            var name = Next().Text.TrimStart('@');
            AcceptKw("AS");
            if (Peek().Is("TABLE"))
            {
                Next();
                var def = TakeParenGroup();
                return new DeclareTableVarStmt(name, def.GetRange(1, def.Count - 2));
            }
            if (Peek().Is("CURSOR") && Peek(1).Is("FOR"))
            {
                // DECLARE @c CURSOR FOR ... (selten) → wie benannter Cursor behandeln
                Next(); Next();
                return new DeclareCursorStmt(name, CollectStatement());
            }
            var type = ParseType();
            List<Token>? def2 = null;
            if (AcceptSym("=")) def2 = CollectExpr(true);
            vars.Add(new VarDecl(name, type, def2));
            if (!AcceptSym(",")) break;
        }
        return new DeclareVarStmt(vars);
    }

    List<Token> ParseType()
    {
        var res = new List<Token>();
        var first = Next();
        res.Add(first);
        while (Peek().IsSym(".")) { res.Add(Next()); res.Add(Next()); }
        if (first.Is("DOUBLE") && Peek().Is("PRECISION")) res.Add(Next());
        if (first.IsAny("CHAR", "CHARACTER", "NATIONAL") && Peek().IsAny("VARYING", "CHARACTER", "CHAR"))
        {
            res.Add(Next());
            if (Peek().Is("VARYING")) res.Add(Next());
        }
        if (Peek().IsSym("(")) res.AddRange(TakeParenGroup());
        return res;
    }

    Stmt ParseSet()
    {
        Next();
        if (Peek().Kind == TokenKind.Variable)
        {
            var name = Next().Text.TrimStart('@');
            var op = Next();
            if (op.Kind != TokenKind.Symbol)
                return new UnsupportedStmt("SET @" + name + " " + op.Text, "Unbekannte SET-Syntax");
            return new SetVarStmt(name, op.Text, CollectExpr(false));
        }
        // SET-Option bis Zeilenende
        var sb = new List<Token>();
        int i = Sig(pos);
        bool first = true;
        for (; i < Last; i++)
        {
            var tk = t[i];
            if (tk.Kind == TokenKind.Whitespace && tk.Text.Contains('\n')) break;
            if (tk.IsSym(";")) { i++; break; }
            if (!first && tk.Kind == TokenKind.Identifier && StatementStart.Contains(tk.Upper)) break;
            sb.Add(tk);
            if (!tk.IsTrivia) first = false;
        }
        pos = i;
        return new SetOptionStmt("SET " + Tok.Text(sb).Trim());
    }

    Stmt ParseRaiserror()
    {
        Next();
        var args = new List<List<Token>>();
        if (Peek().IsSym("("))
        {
            var g = TakeParenGroup();
            args = Tok.SplitTopLevel(g.GetRange(1, g.Count - 2)).Select(Tok.Trim).ToList();
        }
        if (AcceptKw("WITH"))
            while (Peek().IsAny("NOWAIT", "LOG", "SETERROR") || Peek().IsSym(",")) Next();
        return new RaiserrorStmt(args);
    }

    Stmt ParseFetch()
    {
        Next();
        string dir = "NEXT";
        if (Peek().IsAny("NEXT", "PRIOR", "FIRST", "LAST")) dir = Next().Upper;
        else if (Peek().IsAny("ABSOLUTE", "RELATIVE")) { dir = Next().Upper; Next(); }
        AcceptKw("FROM");
        AcceptKw("GLOBAL");
        var cursor = Next().Text;
        List<Token>? into = null;
        if (AcceptKw("INTO"))
        {
            into = new List<Token>();
            while (Peek().Kind == TokenKind.Variable)
            {
                into.Add(Next());
                if (!AcceptSym(",")) break;
            }
        }
        return new CursorOpStmt("FETCH", cursor, into, dir);
    }

    Stmt ParseCreate(int start)
    {
        Next(); // CREATE / ALTER
        if (Peek().Is("OR") && Peek(1).Is("ALTER")) { Next(); Next(); }
        var kind = Peek().Upper;
        switch (kind)
        {
            case "PROC":
            case "PROCEDURE":
                Next();
                return ParseProc();
            case "FUNCTION":
                Next();
                return ParseFunction(start);
            case "TRIGGER":
            {
                var raw = RawText(start, Last);
                pos = Last;
                return new UnsupportedStmt(raw,
                    "CREATE TRIGGER: inserted/deleted → :NEW/:OLD (FOR EACH ROW) oder Compound Trigger – manuell umsetzen");
            }
            case "VIEW":
            {
                var tokens = Tok.Trim(t.GetRange(start, Last - start));
                pos = Last;
                return new SqlStmt(tokens);
            }
        }
        pos = start;
        return new SqlStmt(CollectStatement());
    }

    List<ParamDef> ParseParams()
    {
        var ps = new List<ParamDef>();
        while (Peek().Kind == TokenKind.Variable)
        {
            var name = Next().Text.TrimStart('@');
            AcceptKw("AS");
            var type = ParseType();
            AcceptKw("VARYING");
            List<Token>? def = null;
            if (AcceptSym("="))
                def = CollectUntil(x => x.IsSym(",") || x.IsSym(")") ||
                                        x.IsAny("OUT", "OUTPUT", "READONLY", "AS", "WITH", "FOR"));
            bool output = false, ro = false;
            while (true)
            {
                if (AcceptKw("OUT") || AcceptKw("OUTPUT")) output = true;
                else if (AcceptKw("READONLY")) ro = true;
                else break;
            }
            ps.Add(new ParamDef(name, type, def, output, ro));
            if (!AcceptSym(",")) break;
        }
        return ps;
    }

    static List<Stmt> Unwrap(List<Stmt> body)
    {
        var real = body.Where(s => s is not CommentStmt).ToList();
        if (real.Count == 1 && real[0] is BlockStmt b)
        {
            var r = new List<Stmt>();
            foreach (var s in body)
                if (ReferenceEquals(s, b)) r.AddRange(b.Body); else r.Add(s);
            return r;
        }
        return body;
    }

    Stmt ParseProc()
    {
        var name = NameChain();
        bool paren = AcceptSym("(");
        var ps = ParseParams();
        if (paren) AcceptSym(")");
        if (AcceptKw("WITH"))
            while (!Peek().Is("AS") && !Peek().IsEof) Next();
        if (AcceptKw("FOR")) AcceptKw("REPLICATION");
        AcceptKw("AS");
        var body = Unwrap(ParseStatements(_ => false));
        return new CreateProcStmt(name, ps, body);
    }

    void SkipWithOptions()
    {
        if (!AcceptKw("WITH")) return;
        while (!Peek().IsAny("AS", "BEGIN", "RETURN") && !Peek().IsEof) Next();
    }

    Stmt ParseFunction(int start)
    {
        var name = NameChain();
        AcceptSym("(");
        var ps = ParseParams();
        AcceptSym(")");
        if (!AcceptKw("RETURNS"))
        {
            var raw = RawText(start, Last); pos = Last;
            return new UnsupportedStmt(raw, "Unbekannte Funktionssyntax");
        }
        if (Peek().Kind == TokenKind.Variable)
        {
            var raw = RawText(start, Last); pos = Last;
            return new UnsupportedStmt(raw,
                "Multi-Statement-Tabellenwertfunktion → PIPELINED Function mit Objekt-/Collection-Typ manuell umsetzen");
        }
        if (Peek().Is("TABLE"))
        {
            Next();
            SkipWithOptions();
            AcceptKw("AS");
            AcceptKw("RETURN");
            List<Token> q;
            if (Peek().IsSym("("))
            {
                var g = TakeParenGroup();
                q = g.GetRange(1, g.Count - 2);
            }
            else q = CollectStatement();
            return new CreateFuncStmt(name, ps, null, new List<Stmt>(), Tok.Trim(q));
        }
        var rt = ParseType();
        SkipWithOptions();
        AcceptKw("AS");
        var body = Unwrap(ParseStatements(_ => false));
        return new CreateFuncStmt(name, ps, rt, body, null);
    }

    // ---------------------------------------------------------------- Token-Sammler

    bool IsExprStart()
    {
        var p = Peek();
        if (p.IsEof || p.IsSym(";")) return false;
        if (p.Kind == TokenKind.Identifier && StatementStart.Contains(p.Upper)) return false;
        return true;
    }

    List<Token> CollectUntil(Func<Token, bool> stop)
    {
        int start = Sig(pos);
        var res = new List<Token>();
        int depth = 0, lastSig = -1;
        for (int i = start; i < Last; i++)
        {
            var tk = t[i];
            if (tk.IsTrivia) { res.Add(tk); continue; }
            if (depth == 0 && stop(tk)) break;
            if (tk.IsSym("(") || tk.Is("CASE")) depth++;
            else if ((tk.IsSym(")") || tk.Is("END")) && depth > 0) depth--;
            res.Add(tk);
            lastSig = i;
        }
        pos = lastSig >= 0 ? lastSig + 1 : start;
        return Tok.Trim(res);
    }

    /// <summary>Sammelt einen Ausdruck bis zur nächsten Statement-Grenze.</summary>
    List<Token> CollectExpr(bool stopAtComma) =>
        CollectUntil(tk => tk.IsSym(";") || tk.IsSym(")") || (stopAtComma && tk.IsSym(",")) ||
                           (tk.Kind == TokenKind.Identifier && StatementStart.Contains(tk.Upper)));

    /// <summary>Sammelt ein DML/DDL-Statement. T-SQL kennt keine Pflicht-Semikolons, daher heuristische Grenzen.</summary>
    List<Token> CollectStatement()
    {
        int start = Sig(pos);
        var res = new List<Token>();
        string kind = t[start].Upper;
        bool cte = kind == "WITH";
        bool insertNeedsSource = kind == "INSERT";
        bool seenSet = false;
        string prev = "";
        int depth = 0, lastSig = start - 1;
        bool consumedSemicolon = false;
        int i = start;

        for (; i < Last; i++)
        {
            var tk = t[i];
            if (tk.IsTrivia) { res.Add(tk); continue; }
            bool first = i == start;
            if (!first && depth == 0)
            {
                if (tk.IsSym(";")) { consumedSemicolon = true; break; }
                if (tk.IsSym(")")) break;
                if (tk.Kind == TokenKind.Identifier &&
                    IsBoundary(tk, i, kind, prev, insertNeedsSource, cte, seenSet)) break;
            }
            if (depth == 0 && tk.Kind == TokenKind.Identifier)
            {
                var u = tk.Upper;
                if (u is "VALUES" or "EXEC" or "EXECUTE") insertNeedsSource = false;
                else if (u == "SELECT" && insertNeedsSource && !first) insertNeedsSource = false;
                if (cte && u is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE")
                {
                    cte = false;
                    kind = u;
                    if (u == "INSERT") insertNeedsSource = true;
                }
                if (u == "SET" && kind == "UPDATE") seenSet = true;
            }
            if (tk.IsSym("(") || tk.Is("CASE")) depth++;
            else if ((tk.IsSym(")") || tk.Is("END")) && depth > 0) depth--;
            res.Add(tk);
            lastSig = i;
            prev = tk.Kind == TokenKind.Identifier ? tk.Upper : tk.Text;
        }
        pos = consumedSemicolon ? i + 1 : Math.Max(lastSig + 1, start);
        return Tok.Trim(res);
    }

    bool IsBoundary(Token tk, int idx, string kind, string prev, bool insertNeedsSource, bool cte, bool seenSet)
    {
        var u = tk.Upper;
        if (!StatementStart.Contains(u)) return false;
        if (kind == "MERGE" && u is "INSERT" or "UPDATE" or "DELETE" or "SET" or "SELECT" or "WITH") return false;
        switch (u)
        {
            case "SELECT":
                if (prev is "UNION" or "ALL" or "EXCEPT" or "INTERSECT" or "MINUS") return false;
                return !(insertNeedsSource || cte);
            case "INSERT":
            case "UPDATE":
            case "DELETE":
            case "MERGE":
                if (cte) return false;
                return prev is not ("FOR" or "ON" or "THEN" or "OF");
            case "SET":
                if (kind == "UPDATE" && !seenSet) return false;
                return prev is not ("DELETE" or "UPDATE");
            case "EXEC":
            case "EXECUTE":
                if (insertNeedsSource) return false;
                return prev is not ("WITH" or "AS" or "GRANT" or "REVOKE" or "DENY" or ",");
            case "WITH":
            {
                var nx = t[Sig(idx + 1)];
                if (nx.IsSym("(")) return false;
                if (nx.IsAny("TIES", "ROLLUP", "CUBE", "CHECK", "NOCHECK", "SCHEMABINDING", "ENCRYPTION",
                        "RECOMPILE", "EXECUTE", "NOWAIT", "LOG", "SETERROR", "GRANT")) return false;
                return kind is not ("CREATE" or "ALTER" or "GRANT");
            }
            case "FETCH":
                return prev is not ("ROWS" or "ROW");
            case "IF":
                return prev is not ("TABLE" or "PROCEDURE" or "PROC" or "VIEW" or "FUNCTION" or "INDEX" or
                    "SEQUENCE" or "TRIGGER" or "SCHEMA" or "TYPE" or "COLUMN" or "CONSTRAINT");
            case "ALTER":
                return !(t[Sig(idx + 1)].Is("COLUMN"));
            case "DROP":
                return kind != "ALTER";
            case "CREATE":
            case "GRANT":
            case "REVOKE":
            case "DENY":
                return true;
            default:
                return true;
        }
    }
}
