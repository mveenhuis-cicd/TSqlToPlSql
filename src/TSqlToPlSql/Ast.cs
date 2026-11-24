namespace TSqlToPlSql;

internal abstract record Stmt
{
    /// <summary>Einrückung der ersten Zeile im Original (für Re-Indent mehrzeiliger Statements).</summary>
    public int Indent { get; set; }
}

internal sealed record CommentStmt(string Text) : Stmt;
internal sealed record BlockStmt(List<Stmt> Body) : Stmt;
internal sealed record IfStmt(List<Token> Cond, Stmt Then, Stmt? Else) : Stmt;
internal sealed record WhileStmt(List<Token> Cond, Stmt Body) : Stmt;
internal sealed record TryCatchStmt(List<Stmt> Try, List<Stmt> Catch) : Stmt;

internal sealed record VarDecl(string Name, List<Token> Type, List<Token>? Default);
internal sealed record DeclareVarStmt(List<VarDecl> Vars) : Stmt;
internal sealed record DeclareTableVarStmt(string Name, List<Token> Definition) : Stmt;
internal sealed record DeclareCursorStmt(string Name, List<Token> Query) : Stmt;

internal sealed record SetVarStmt(string Name, string Op, List<Token> Expr) : Stmt;
internal sealed record SetOptionStmt(string Text) : Stmt;
internal sealed record PrintStmt(List<Token> Expr) : Stmt;
internal sealed record ReturnStmt(List<Token>? Expr) : Stmt;
internal sealed record RaiserrorStmt(List<List<Token>> Args) : Stmt;
internal sealed record ThrowStmt(List<List<Token>>? Args) : Stmt;
internal sealed record ExecStmt(List<Token> Tokens) : Stmt;
internal sealed record TranStmt(string Kind, string? Name) : Stmt;
internal sealed record CursorOpStmt(string Op, string Cursor, List<Token>? Into, string Direction) : Stmt;
internal sealed record BreakStmt : Stmt;
internal sealed record ContinueStmt : Stmt;
internal sealed record GotoStmt(string Label) : Stmt;
internal sealed record LabelStmt(string Label) : Stmt;
internal sealed record UseStmt(string Database) : Stmt;
internal sealed record SqlStmt(List<Token> Tokens) : Stmt;
internal sealed record UnsupportedStmt(string Original, string Reason) : Stmt;

internal sealed record ParamDef(string Name, List<Token> Type, List<Token>? Default, bool Output, bool ReadOnly);

internal sealed record CreateProcStmt(List<Token> Name, List<ParamDef> Params, List<Stmt> Body) : Stmt;

internal sealed record CreateFuncStmt(
    List<Token> Name, List<ParamDef> Params, List<Token>? ReturnType, List<Stmt> Body, List<Token>? InlineQuery) : Stmt;
