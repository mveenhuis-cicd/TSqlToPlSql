namespace TSqlToPlSql;

public enum TokenKind
{
    Whitespace, LineComment, BlockComment, String, NString, Identifier, QuotedIdentifier,
    Variable, SystemVariable, TempTable, Number, Symbol, Raw, EOF
}

public sealed class Token
{
    public Token(TokenKind kind, string text) { Kind = kind; Text = text; }

    public TokenKind Kind { get; }
    public string Text { get; }

    public bool IsTrivia => Kind is TokenKind.Whitespace or TokenKind.LineComment or TokenKind.BlockComment;
    public bool IsComment => Kind is TokenKind.LineComment or TokenKind.BlockComment;
    public bool IsName => Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier;
    public bool IsEof => Kind == TokenKind.EOF;
    public string Upper => Text.ToUpperInvariant();
    public string Name => Kind == TokenKind.QuotedIdentifier ? Unquote(Text) : Text;

    public bool Is(string keyword) =>
        Kind == TokenKind.Identifier && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsAny(params string[] keywords)
    {
        foreach (var k in keywords) if (Is(k)) return true;
        return false;
    }

    public bool IsSym(string s) => Kind == TokenKind.Symbol && Text == s;

    public static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']') return s[1..^1].Replace("]]", "]");
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1].Replace("\"\"", "\"");
        return s.Trim('[', ']', '"');
    }

    /// <summary>Inhalt eines String-Literals ohne Anführungszeichen (Escapes bleiben erhalten).</summary>
    public string StringContent
    {
        get
        {
            var s = Kind == TokenKind.NString ? Text[1..] : Text;
            return s.Length >= 2 ? s[1..^1] : s;
        }
    }

    public override string ToString() => Text;

    public static Token Raw(string s) => new(TokenKind.Raw, s);
    public static Token Space() => new(TokenKind.Whitespace, " ");
    public static Token Kw(string s) => new(TokenKind.Identifier, s);
    public static Token Sym(string s) => new(TokenKind.Symbol, s);
    public static Token Eof() => new(TokenKind.EOF, "");
}
