#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace d4rkpl4y3r.AV3ToggleUtil.Util
{
    public enum BoolTermOp
    {
        If,
        IfNot,
        Equals,
        NotEquals,
        Greater,
        Less
    }

    [Serializable]
    public struct BoolTerm
    {
        public string Parameter;
        public BoolTermOp Op;
        public float Threshold;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public BoolTerm(string parameter, BoolTermOp op, float threshold = 0f)
        {
            Parameter = parameter;
            Op = op;
            Threshold = op == BoolTermOp.If || op == BoolTermOp.IfNot ? 0f : threshold;
        }

        public static string FormatThreshold(float value)
            => value.ToString("0.####", Invariant);

        public override string ToString()
        {
            switch (Op)
            {
                case BoolTermOp.If:
                    return Parameter;
                case BoolTermOp.IfNot:
                    return "!" + Parameter;
                case BoolTermOp.Equals:
                    return Parameter + " = " + FormatThreshold(Threshold);
                case BoolTermOp.NotEquals:
                    return Parameter + " != " + FormatThreshold(Threshold);
                case BoolTermOp.Greater:
                    return Parameter + " > " + FormatThreshold(Threshold);
                case BoolTermOp.Less:
                    return Parameter + " < " + FormatThreshold(Threshold);
                default:
                    return "?";
            }
        }

        public bool Equals(BoolTerm other)
            => Op == other.Op
            && string.Equals(Parameter, other.Parameter, StringComparison.Ordinal)
            && Threshold == other.Threshold;

        public override bool Equals(object obj)
            => obj is BoolTerm other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Parameter != null ? Parameter.GetHashCode() : 0;
                hash = (hash * 397) ^ (int)Op;
                hash = (hash * 397) ^ Threshold.GetHashCode();
                return hash;
            }
        }
    }

    /// <summary>
    /// A boolean expression in DNF (disjunctive normal form): a list of AND clauses joined with OR.
    /// Handles pure boolean logic only - no Unity types. Zero clauses means "unconditional" (always true).
    /// </summary>
    public sealed class BoolExpression
    {
        public List<List<BoolTerm>> Clauses { get; } = new List<List<BoolTerm>>();

        public bool IsEmpty => Clauses.Count == 0;

        /// <summary>
        /// Parses user text into a normalized DNF expression.
        /// Empty/whitespace input succeeds with an empty (unconditional) expression.
        /// </summary>
        public static bool TryParse(string text, out BoolExpression result, out string error)
            => TryParse(text, null, out result, out error);

        /// <summary>
        /// Like <see cref="TryParse(string, out BoolExpression, out string)"/> but with a set of
        /// known parameter names. Names may contain spaces (matched with space-tolerance);
        /// at a term position the longest known name wins over plain identifier scanning.
        /// </summary>
        public static bool TryParse(string text, IEnumerable<string> knownParameterNames, out BoolExpression result, out string error)
        {
            var expr = new BoolExpression();
            try
            {
                if (!string.IsNullOrWhiteSpace(text))
                    new Parser(text, knownParameterNames).ParseInto(expr);
            }
            catch (ParseException e)
            {
                result = null;
                error = e.Message;
                return false;
            }

            expr.Normalize();

            if (expr.IsEmpty && !string.IsNullOrWhiteSpace(text))
            {
                result = null;
                error = "Expression can never be true";
                return false;
            }

            result = expr;
            error = null;
            return true;
        }

        public string ToDisplayString()
        {
            if (Clauses.Count == 0)
                return "";

            var sb = new StringBuilder();
            for (var i = 0; i < Clauses.Count; i++)
            {
                if (i > 0)
                    sb.Append(" | ");
                var clause = Clauses[i];
                if (clause.Count > 1)
                {
                    sb.Append('(');
                    for (var j = 0; j < clause.Count; j++)
                    {
                        if (j > 0)
                            sb.Append(" & ");
                        sb.Append(clause[j].ToString());
                    }
                    sb.Append(')');
                }
                else
                {
                    sb.Append(clause[0].ToString());
                }
            }
            return sb.ToString();
        }

        public override string ToString() => ToDisplayString();

        public bool Differs(BoolExpression other)
            => other == null || !IsSameExpression(other);

        public bool IsSameExpression(BoolExpression other)
        {
            if (other == null || Clauses.Count != other.Clauses.Count)
                return false;
            for (var i = 0; i < Clauses.Count; i++)
            {
                var found = false;
                for (var j = 0; j < other.Clauses.Count; j++)
                {
                    if (ClausesEqual(Clauses[i], other.Clauses[j]))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
            return true;
        }

        /// <summary>Deduplicates terms and clauses and drops clauses that contain a contradiction.</summary>
        public void Normalize()
        {
            var normalized = new List<List<BoolTerm>>();
            for (var i = 0; i < Clauses.Count; i++)
            {
                SimplifyClause(Clauses[i]);
                if (Clauses[i].Count > 0 && !ContainsClause(normalized, Clauses[i]))
                    normalized.Add(Clauses[i]);
            }
            Clauses.Clear();
            Clauses.AddRange(normalized);
        }

        private static bool ContainsClause(List<List<BoolTerm>> list, List<BoolTerm> clause)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (ClausesEqual(list[i], clause))
                    return true;
            }
            return false;
        }

        private static bool ClausesEqual(List<BoolTerm> a, List<BoolTerm> b)
        {
            if (a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
            {
                var found = false;
                for (var j = 0; j < b.Count; j++)
                {
                    if (a[i].Equals(b[j]))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
            return true;
        }

        private static void SimplifyClause(List<BoolTerm> clause)
        {
            var unique = new List<BoolTerm>();
            for (var i = 0; i < clause.Count; i++)
            {
                if (!unique.Contains(clause[i]))
                    unique.Add(clause[i]);
            }
            clause.Clear();
            clause.AddRange(unique);

            var changed = true;
            while (changed && clause.Count > 0)
            {
                changed = false;
                for (var i = 0; i < clause.Count - 1 && !changed; i++)
                {
                    for (var j = clause.Count - 1; j > i; j--)
                    {
                        var result = SimplifyPair(clause[i], clause[j]);
                        if (result == PairResult.Contradiction)
                        {
                            clause.Clear();
                            changed = true;
                            break;
                        }
                        if (result == PairResult.DropFirst)
                        {
                            clause.RemoveAt(i);
                            i--;
                            changed = true;
                        }
                        else if (result == PairResult.DropSecond)
                        {
                            clause.RemoveAt(j);
                            changed = true;
                        }
                    }
                }
            }
        }

        private enum PairResult
        {
            KeepBoth,
            DropFirst,
            DropSecond,
            Contradiction
        }

        // "If" is treated as "parameter = 1". Reduces a pair of same-parameter terms
        // to a single term if one subsumes the other, or flags a contradiction.
        private static PairResult SimplifyPair(BoolTerm a, BoolTerm b)
        {
            if (!string.Equals(a.Parameter, b.Parameter, StringComparison.Ordinal))
                return PairResult.KeepBoth;

            if (a.Op == BoolTermOp.If && b.Op == BoolTermOp.IfNot)
                return PairResult.Contradiction;
            if (a.Op == BoolTermOp.IfNot && b.Op == BoolTermOp.If)
                return PairResult.Contradiction;

            if (a.Op == BoolTermOp.If)
                a = new BoolTerm(a.Parameter, BoolTermOp.Equals, 1f);
            if (b.Op == BoolTermOp.If)
                b = new BoolTerm(b.Parameter, BoolTermOp.Equals, 1f);

            switch (a.Op)
            {
                case BoolTermOp.Equals:
                    switch (b.Op)
                    {
                        case BoolTermOp.Equals:
                            return b.Threshold == a.Threshold ? PairResult.DropSecond : PairResult.Contradiction;
                        case BoolTermOp.NotEquals:
                            return b.Threshold == a.Threshold ? PairResult.Contradiction : PairResult.DropSecond;
                        case BoolTermOp.Greater:
                            return a.Threshold > b.Threshold ? PairResult.DropSecond : PairResult.Contradiction;
                        case BoolTermOp.Less:
                            return a.Threshold < b.Threshold ? PairResult.DropSecond : PairResult.Contradiction;
                        default:
                            break;
                    }
                    break;
                case BoolTermOp.NotEquals:
                    switch (b.Op)
                    {
                        case BoolTermOp.Equals:
                            return b.Threshold == a.Threshold ? PairResult.Contradiction : PairResult.DropFirst;
                        case BoolTermOp.Greater:
                            if (b.Threshold == a.Threshold)
                                return PairResult.DropFirst;
                            break;
                        case BoolTermOp.Less:
                            if (b.Threshold == a.Threshold)
                                return PairResult.DropFirst;
                            break;
                        default:
                            break;
                    }
                    break;
                case BoolTermOp.Greater:
                    switch (b.Op)
                    {
                        case BoolTermOp.Equals:
                            return b.Threshold > a.Threshold ? PairResult.DropFirst : PairResult.Contradiction;
                        case BoolTermOp.NotEquals:
                            if (b.Threshold == a.Threshold)
                                return PairResult.DropFirst;
                            break;
                        default:
                            break;
                    }
                    break;
                case BoolTermOp.Less:
                    switch (b.Op)
                    {
                        case BoolTermOp.Equals:
                            return b.Threshold < a.Threshold ? PairResult.DropFirst : PairResult.Contradiction;
                        case BoolTermOp.NotEquals:
                            if (b.Threshold == a.Threshold)
                                return PairResult.DropFirst;
                            break;
                        default:
                            break;
                    }
                    break;
                case BoolTermOp.IfNot:
                    switch (b.Op)
                    {
                        case BoolTermOp.Equals:
                            return b.Threshold == 1f ? PairResult.Contradiction : PairResult.DropSecond;
                        case BoolTermOp.Greater:
                            if (b.Threshold >= 1f)
                                return PairResult.DropFirst;
                            break;
                        case BoolTermOp.Less:
                            if (b.Threshold <= 1f)
                                return PairResult.DropFirst;
                            break;
                        default:
                            break;
                    }
                    break;
            }
            return PairResult.KeepBoth;
        }

        private sealed class ParseException : Exception
        {
            public ParseException(string message)
                : base(message)
            {
            }
        }

        private class Parser
        {
            private readonly string _text;
            private int _pos;
            private readonly HashSet<string> _knownTokens;

            public Parser(string text, IEnumerable<string> knownTokens = null)
            {
                _text = text;
                if (knownTokens != null)
                {
                    _knownTokens = new HashSet<string>();
                    foreach (var t in knownTokens)
                        if (!string.IsNullOrEmpty(t))
                            _knownTokens.Add(t);
                }
            }

            public void ParseInto(BoolExpression expr)
            {
                SkipWs();
                if (string.IsNullOrWhiteSpace(_text))
                    return;
                expr.Clauses.AddRange(ParseOr());
                SkipWs();
                if (_pos != _text.Length)
                    throw new ParseException($"Unexpected '{_text[_pos]}' at position {_pos}");
            }

            private List<List<BoolTerm>> ParseOr()
            {
                var clauses = ParseAnd();
                while (true)
                {
                    SkipWs();
                    if (!MatchOr())
                        break;
                    SkipWs();
                    clauses.AddRange(ParseAnd());
                }
                return clauses;
            }

            private List<List<BoolTerm>> ParseAnd()
            {
                var clauses = ParseNot();
                while (true)
                {
                    SkipWs();
                    if (!MatchAnd())
                        break;
                    SkipWs();
                    var right = ParseNot();
                    var distributed = new List<List<BoolTerm>>();
                    for (var i = 0; i < clauses.Count; i++)
                    {
                        for (var j = 0; j < right.Count; j++)
                        {
                            var merged = new List<BoolTerm>(clauses[i]);
                            merged.AddRange(right[j]);
                            distributed.Add(merged);
                        }
                    }
                    clauses = distributed;
                }
                return clauses;
            }

            private List<List<BoolTerm>> ParseNot()
            {
                if (Match("!"))
                {
                    SkipWs();
                    var inner = ParseNot();
                    if (inner.Count == 1)
                    {
                        // !(a & b) == !a | !b : each negated term becomes its own OR clause.
                        var negated = new List<List<BoolTerm>>();
                        for (var i = 0; i < inner[0].Count; i++)
                            negated.Add(new List<BoolTerm> { Negate(inner[0][i]) });
                        return negated;
                    }
                    // !(a | b) == !a & !b : all negated terms form one AND clause.
                    var clause = new List<BoolTerm>();
                    for (var i = 0; i < inner.Count; i++)
                    {
                        for (var j = 0; j < inner[i].Count; j++)
                            clause.Add(Negate(inner[i][j]));
                    }
                    return new List<List<BoolTerm>> { clause };
                }
                return ParsePrimary();
            }

            private static BoolTerm Negate(BoolTerm term)
            {
                switch (term.Op)
                {
                    case BoolTermOp.If:
                        return new BoolTerm(term.Parameter, BoolTermOp.IfNot);
                    case BoolTermOp.IfNot:
                        return new BoolTerm(term.Parameter, BoolTermOp.If);
                    case BoolTermOp.Equals:
                        return new BoolTerm(term.Parameter, BoolTermOp.NotEquals, term.Threshold);
                    case BoolTermOp.NotEquals:
                        return new BoolTerm(term.Parameter, BoolTermOp.Equals, term.Threshold);
                    case BoolTermOp.Greater:
                        throw new ParseException("Cannot negate '>' - the inverted form is not representable");
                    case BoolTermOp.Less:
                        throw new ParseException("Cannot negate '<' - the inverted form is not representable");
                    default:
                        throw new ParseException("Cannot negate this condition");
                }
            }

            private List<List<BoolTerm>> ParsePrimary()
            {
                SkipWs();
                if (Match("("))
                {
                    SkipWs();
                    var inner = ParseOr();
                    SkipWs();
                    if (!Match(")"))
                        throw new ParseException($"Expected ')' at position {_pos}");
                    return inner;
                }
                var term = ParseTerm();
                return new List<List<BoolTerm>> { new List<BoolTerm> { term } };
            }

            private BoolTerm ParseTerm()
            {
                var parameter = ParseParameterName();
                SkipWs();

                if (Match(">="))
                    throw new ParseException("'>=' is not supported - only single comparison operators are allowed");
                if (Match("<="))
                    throw new ParseException("'<=' is not supported - only single comparison operators are allowed");
                if (Match("!="))
                    return new BoolTerm(parameter, BoolTermOp.NotEquals, ParseNumber());
                if (Match("="))
                    return new BoolTerm(parameter, BoolTermOp.Equals, ParseNumber());
                if (Match(">"))
                    return new BoolTerm(parameter, BoolTermOp.Greater, ParseNumber());
                if (Match("<"))
                    return new BoolTerm(parameter, BoolTermOp.Less, ParseNumber());
                return new BoolTerm(parameter, BoolTermOp.If);
            }

            /// <summary>
            /// At a term position, try every known parameter name using a whitespace-tolerant
            /// matcher and keep the longest match. When no token set is provided, fall back
            /// to plain identifier scanning. When a token set IS provided and no token
            /// matches, scan a candidate identifier and throw a ParseException naming it.
            /// </summary>
            private string ParseParameterName()
            {
                if (_knownTokens == null)
                {
                    var ident = ParseIdentifier();
                    if (ident == null)
                    {
                        SkipWs();
                        throw new ParseException($"Expected a parameter name at position {_pos}");
                    }
                    return ident;
                }

                string best = null;
                var bestEnd = -1;
                foreach (var token in _knownTokens)
                {
                    var end = TryMatchKnownToken(token);
                    if (end > bestEnd)
                    {
                        bestEnd = end;
                        best = token;
                    }
                }
                if (best != null)
                {
                    _pos = bestEnd;
                    return best;
                }

                var candidate = ScanIdentifierOrSpaceIdentifier();
                if (string.IsNullOrEmpty(candidate))
                {
                    SkipWs();
                    throw new ParseException($"Expected a known parameter name at position {_pos}");
                }
                SkipWs();
                throw new ParseException($"Unknown parameter '{candidate}' at position {_pos}");
            }

            /// <summary>
            /// Matches a known token starting at _pos, tolerating extra/any whitespace where
            /// the token has a space (so "my  var" works for "my var"). Returns the end
            /// position in _text, or -1.
            /// </summary>
            private int TryMatchKnownToken(string token)
            {
                if (_pos >= _text.Length)
                    return -1;

                if (_pos + token.Length <= _text.Length && _text.Substring(_pos, token.Length) == token)
                    return _pos + token.Length;

                var t = _pos;
                for (var i = 0; i < token.Length; i++)
                {
                    var c = token[i];
                    if (c == ' ')
                    {
                        if (t >= _text.Length || !char.IsWhiteSpace(_text[t]))
                            return -1;
                        while (t < _text.Length && char.IsWhiteSpace(_text[t]))
                            t++;
                        if (t >= _text.Length)
                            return -1;
                    }
                    else
                    {
                        if (t >= _text.Length || _text[t] != c)
                            return -1;
                        t++;
                    }
                }
                return t;
            }

            /// <summary>
            /// Greedily scans a candidate parameter name: identifier characters plus internal
            /// whitespace runs (so "my  var" is one candidate). Trailing whitespace is ignored
            /// so a bare "my var" followed by "&" still scans as "my var".
            /// </summary>
            private string ScanIdentifierOrSpaceIdentifier()
            {
                var start = _pos;
                var lastNonWs = start;
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '$')
                    {
                        lastNonWs = ++_pos;
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        // Keep consuming whitespace only if it is followed by an
                        // identifier character (i.e. it is part of a multi-word name).
                        var t = _pos;
                        while (t < _text.Length && char.IsWhiteSpace(_text[t]))
                            t++;
                        if (t < _text.Length)
                        {
                            var nc = _text[t];
                            if (char.IsLetterOrDigit(nc) || nc == '_' || nc == '-' || nc == '.' || nc == '$')
                            {
                                _pos = t;
                                continue;
                            }
                        }
                        break;
                    }
                    else
                        break;
                }
                return _pos > start ? _text.Substring(start, lastNonWs - start) : null;
            }

            private string ParseIdentifier()
            {
                var start = _pos;
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '$')
                        _pos++;
                    else
                        break;
                }
                return _pos > start ? _text.Substring(start, _pos - start) : null;
            }

            private float ParseNumber()
            {
                SkipWs();
                var start = _pos;
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (char.IsDigit(c) || c == '.' || c == '+' || c == '-' || c == 'e' || c == 'E')
                        _pos++;
                    else
                        break;
                }
                var token = _pos > start ? _text.Substring(start, _pos - start) : string.Empty;
                if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw new ParseException($"Invalid number '{token}'");
                return value;
            }

            /// <summary>Accepts both "||" and single-char "|".</summary>
            private bool MatchOr()
            {
                if (Match("||"))
                    return true;
                if (Match("|"))
                    return true;
                return false;
            }

            /// <summary>Accepts both "&&" and single-char "&".</summary>
            private bool MatchAnd()
            {
                if (Match("&&"))
                    return true;
                if (Match("&"))
                    return true;
                return false;
            }

            private bool Match(string token)
            {
                if (_pos + token.Length <= _text.Length
                    && _text.Substring(_pos, token.Length) == token)
                {
                    _pos += token.Length;
                    return true;
                }
                return false;
            }

            private void SkipWs()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                    _pos++;
            }
        }
    }
}
#endif