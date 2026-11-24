namespace TSqlToPlSql;

public static class Lexer
{
    static readonly HashSet<string> TwoCharOps = new()
    {
        "<=", ">=", "<>", "!=", "!<", "!>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::"
    };

    public static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int i = 0, n = s.Length;
        char At(int k) => k < n ? s[k] : '\0';

        while (i < n)
        {
            char c = s[i];
            int st = i;

            if (char.IsWhiteSpace(c))
            {
                while (i < n && char.IsWhiteSpace(s[i])) i++;
                list.Add(new Token(TokenKind.Whitespace, Normalize(s[st..i]).Replace("\t", "    ")));
                continue;
            }
            if (c == '-' && At(i + 1) == '-')
            {
                while (i < n && s[i] != '\n' && s[i] != '\r') i++;
                list.Add(new Token(TokenKind.LineComment, s[st..i]));
                continue;
            }
            if (c == '/' && At(i + 1) == '*')
            {
                i += 2;
                int depth = 1;
                while (i < n && depth > 0)
                {
                    if (s[i] == '/' && At(i + 1) == '*') { depth++; i += 2; }
                    else if (s[i] == '*' && At(i + 1) == '/') { depth--; i += 2; }
                    else i++;
                }
                list.Add(new Token(TokenKind.BlockComment, Normalize(s[st..Math.Min(i, n)])));
                continue;
            }
            if ((c == 'N' || c == 'n') && At(i + 1) == '\'')
            {
                i++;
                i = ReadQuoted(s, i, '\'');
                list.Add(new Token(TokenKind.NString, "N" + s[(st + 1)..i]));
                continue;
            }
            if (c == '\'')
            {
                i = ReadQuoted(s, i, '\'');
                list.Add(new Token(TokenKind.String, s[st..i]));
                continue;
            }
            if (c == '[')
            {
                i++;
                while (i < n)
                {
                    if (s[i] == ']') { if (At(i + 1) == ']') { i += 2; continue; } i++; break; }
                    i++;
                }
                list.Add(new Token(TokenKind.QuotedIdentifier, s[st..i]));
                continue;
            }
            if (c == '"')
            {
                i = ReadQuoted(s, i, '"');
                list.Add(new Token(TokenKind.QuotedIdentifier, s[st..i]));
                continue;
            }
            if (c == '@')
            {
                i++;
                bool sys = At(i) == '@';
                if (sys) i++;
                while (i < n && IsIdentChar(s[i])) i++;
                list.Add(new Token(sys ? TokenKind.SystemVariable : TokenKind.Variable, s[st..i]));
                continue;
            }
            if (c == '#')
            {
                while (i < n && s[i] == '#') i++;
                while (i < n && IsIdentChar(s[i])) i++;
                list.Add(new Token(TokenKind.TempTable, s[st..i]));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(At(i + 1))))
            {
                if (c == '0' && (At(i + 1) == 'x' || At(i + 1) == 'X'))
                {
                    i += 2;
                    while (i < n && Uri.IsHexDigit(s[i])) i++;
                }
                else
                {
                    while (i < n && char.IsDigit(s[i])) i++;
                    if (At(i) == '.') { i++; while (i < n && char.IsDigit(s[i])) i++; }
                    if ((At(i) == 'e' || At(i) == 'E') &&
                        (char.IsDigit(At(i + 1)) || ((At(i + 1) == '+' || At(i + 1) == '-') && char.IsDigit(At(i + 2)))))
                    {
                        i += 2;
                        while (i < n && char.IsDigit(s[i])) i++;
                    }
                }
                list.Add(new Token(TokenKind.Number, s[st..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && IsIdentChar(s[i])) i++;
                list.Add(new Token(TokenKind.Identifier, s[st..i]));
                continue;
            }
            if (i + 1 < n && TwoCharOps.Contains(s.Substring(i, 2)))
            {
                list.Add(new Token(TokenKind.Symbol, s.Substring(i, 2)));
                i += 2;
                continue;
            }
            list.Add(new Token(TokenKind.Symbol, c.ToString()));
            i++;
        }
        list.Add(Token.Eof());
        return list;
    }

    static int ReadQuoted(string s, int i, char q)
    {
        int n = s.Length;
        i++; // öffnendes Zeichen
        while (i < n)
        {
            if (s[i] == q)
            {
                if (i + 1 < n && s[i + 1] == q) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return n;
    }

    static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#' || c == '@';

    /// <summary>Teilt einen Token-Strom an GO-Zeilen in Batches.</summary>
    public static List<List<Token>> SplitBatches(List<Token> tokens)
    {
        var batches = new List<List<Token>>();
        var cur = new List<Token>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsEof) break;
            if (t.Is("GO") && AtLineStart(tokens, i) && AtLineEnd(tokens, i, out int skipTo))
            {
                cur.Add(Token.Eof());
                batches.Add(cur);
                cur = new List<Token>();
                i = skipTo;
                continue;
            }
            cur.Add(t);
        }
        cur.Add(Token.Eof());
        batches.Add(cur);
        return batches.Where(b => b.Any(x => !x.IsTrivia && !x.IsEof) || b.Any(x => x.IsComment)).ToList();
    }

    static bool AtLineStart(List<Token> t, int i)
    {
        if (i == 0) return true;
        var p = t[i - 1];
        return p.Kind == TokenKind.Whitespace && (p.Text.Contains('\n') || i - 1 == 0);
    }

    static bool AtLineEnd(List<Token> t, int i, out int lastIdx)
    {
        lastIdx = i;
        int k = i + 1;
        if (k < t.Count && t[k].Kind == TokenKind.Whitespace && !t[k].Text.Contains('\n')) k++;
        if (k < t.Count && t[k].Kind == TokenKind.Number) { lastIdx = k; k++; }
        if (k < t.Count && t[k].Kind == TokenKind.Whitespace && !t[k].Text.Contains('\n')) k++;
        if (k < t.Count && t[k].Kind == TokenKind.LineComment) k++;
        return k >= t.Count || t[k].IsEof || (t[k].Kind == TokenKind.Whitespace && t[k].Text.Contains('\n'));
    }
}
