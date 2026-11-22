#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

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
    /// Base class for the expression tree. And/Or nodes are flat n-ary: after
    /// normalization an And never contains a nested And and an Or never contains a
    /// nested Or, so child lists are compared as unordered sets.
    /// </summary>
    public abstract class BoolNode
    {
        internal abstract bool EqualsNode(BoolNode other);

        /// <summary>
        /// Appends the node's display text to <paramref name="sb"/>. <paramref name="outerPrecedence"/>
        /// is -1 at the root, 0 inside an And (a " | " is looser than the surroundings), and 1
        /// inside an Or (a " & " is tighter than the surroundings).
        /// </summary>
        internal abstract void AppendToDisplay(StringBuilder sb, int outerPrecedence);
    }

    public sealed class BoolAnd : BoolNode
    {
        public List<BoolNode> Children { get; }

        public BoolAnd(List<BoolNode> children)
        {
            Children = children ?? new List<BoolNode>();
        }

        internal override bool EqualsNode(BoolNode other)
            => other is BoolAnd a && ChildrenAllMatch(Children, a.Children);

        internal override void AppendToDisplay(StringBuilder sb, int outerPrecedence)
        {
            // " & " binds tighter than " | " and a nested And is impossible
            // (the normalizer flattens it), so an And never needs parens.
            for (var i = 0; i < Children.Count; i++)
            {
                if (i > 0)
                    sb.Append(" & ");
                Children[i].AppendToDisplay(sb, 0);
            }
        }

        internal static bool ChildrenAllMatch(List<BoolNode> mine, List<BoolNode> theirs)
        {
            if (mine.Count != theirs.Count)
                return false;
            for (var i = 0; i < mine.Count; i++)
            {
                var found = false;
                for (var j = 0; j < theirs.Count; j++)
                {
                    if (mine[i].EqualsNode(theirs[j]))
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
    }

    public sealed class BoolOr : BoolNode
    {
        public List<BoolNode> Children { get; }

        public BoolOr(List<BoolNode> children)
        {
            Children = children ?? new List<BoolNode>();
        }

        internal override bool EqualsNode(BoolNode other)
            => other is BoolOr o && BoolAnd.ChildrenAllMatch(Children, o.Children);

        internal override void AppendToDisplay(StringBuilder sb, int outerPrecedence)
        {
            // " | " binds looser, so an Or needs parens only when inside an And.
            var group = outerPrecedence == 0;
            if (group)
                sb.Append('(');
            for (var i = 0; i < Children.Count; i++)
            {
                if (i > 0)
                    sb.Append(" | ");
                Children[i].AppendToDisplay(sb, 1);
            }
            if (group)
                sb.Append(')');
        }
    }

    public sealed class BoolNot : BoolNode
    {
        public BoolNode Child { get; }

        public BoolNot(BoolNode child)
        {
            Child = child;
        }

        internal override bool EqualsNode(BoolNode other)
            => other is BoolNot n && Child.EqualsNode(n.Child);

        internal override void AppendToDisplay(StringBuilder sb, int outerPrecedence)
        {
            sb.Append('!');
            if (Child is BoolTermLeaf)
                Child.AppendToDisplay(sb, 0);
            else
            {
                sb.Append('(');
                Child.AppendToDisplay(sb, 0);
                sb.Append(')');
            }
        }
    }

    public sealed class BoolConstant : BoolNode
    {
        public bool Value { get; }

        public BoolConstant(bool value)
        {
            Value = value;
        }

        internal override bool EqualsNode(BoolNode other)
            => other is BoolConstant c && Value == c.Value;

        internal override void AppendToDisplay(StringBuilder sb, int outerPrecedence)
            => sb.Append(Value ? "true" : "false");
    }

    public sealed class BoolTermLeaf : BoolNode
    {
        public BoolTerm Term { get; }

        public BoolTermLeaf(BoolTerm term)
        {
            Term = term;
        }

        internal override bool EqualsNode(BoolNode other)
            => other is BoolTermLeaf t && Term.Equals(t.Term);

        internal override void AppendToDisplay(StringBuilder sb, int outerPrecedence)
            => sb.Append(Term.ToString());
    }

    /// <summary>
    /// A boolean expression stored as a tree of flat n-ary And/Or/Not nodes over
    /// <see cref="BoolTermLeaf"/> leaves (plus <see cref="BoolConstant"/>). After
    /// <see cref="Normalize"/> the tree is simplified: same-kind operators are flattened,
    /// double negations removed, De Morgan applied so Not only ever sits directly on a
    /// term, constants folded, duplicate children removed, and same-parameter term pairs
    /// reduced (subsumption / contradiction). A null <see cref="Root"/> means
    /// "unconditional" (always true).
    /// </summary>
    public sealed class BoolExpression
    {
        public BoolNode Root { get; set; }

        public bool IsEmpty => Root == null;

        /// <summary>The expression is impossible to satisfy (normalized to false).</summary>
        public bool IsContradiction => Root is BoolConstant c && !c.Value;

        private Dictionary<string, AnimatorControllerParameterType> _parameterTypes;

        /// <summary>
        /// Parses user text into a normalized tree expression.
        /// Empty/whitespace input succeeds with an empty (unconditional) expression.
        /// </summary>
        public static bool TryParse(string text, out BoolExpression result, out string error)
            => TryParse(text, null, null, out result, out error);

        /// <summary>
        /// Like <see cref="TryParse(string, out BoolExpression, out string)"/> but with a set of
        /// known parameter names. Names may contain spaces (matched with space-tolerance);
        /// at a term position the longest known name wins over plain identifier scanning.
        /// </summary>
        public static bool TryParse(string text, IEnumerable<string> knownParameterNames, out BoolExpression result, out string error)
            => TryParse(text, knownParameterNames, null, out result, out error);

        /// <summary>
        /// Like the name-only overload but additionally given the parameters' types. For
        /// Int parameters, negating Greater/Less IS representable (by shifting the threshold
        /// by 1), so negation succeeds where the name-only parse would refuse it.
        /// </summary>
        public static bool TryParse(string text, IEnumerable<string> knownParameterNames, Dictionary<string, AnimatorControllerParameterType> parameterTypes, out BoolExpression result, out string error)
        {
            var expr = new BoolExpression { _parameterTypes = parameterTypes };
            try
            {
                new Parser(text, knownParameterNames).ParseIntoRoot(expr);
            }
            catch (ParseException e)
            {
                result = null;
                error = e.Message;
                return false;
            }

            expr.Normalize();

            if (expr.IsContradiction)
            {
                result = null;
                error = "Expression can never be true";
                return false;
            }

            result = expr;
            error = null;
            return true;
        }

        /// <summary>
        /// Negates this expression in place (wraps the root in Not and re-normalizes).
        /// On failure (a comparison that cannot be negated) Root is restored and the error is reported.
        /// </summary>
        public void Negate(out string error)
        {
            var prev = Root;
            if (IsEmpty)
                Root = new BoolConstant(false);
            else if (IsContradiction)
                Root = new BoolConstant(true);
            else
                Root = new BoolNot(Root);
            try
            {
                Normalize();
                error = null;
            }
            catch (ParseException e)
            {
                error = e.Message;
                Root = prev; // keep the expression usable
            }
        }

        /// <summary>Negates a copy of this expression without mutating it.</summary>
        public static bool TryNegate(BoolExpression expr, out BoolExpression negated, out string error)
        {
            var copy = new BoolExpression();
            if (expr != null)
            {
                copy.Root = expr.Root;
                copy._parameterTypes = expr._parameterTypes;
            }
            copy.Negate(out error);
            negated = error == null ? copy : null;
            return error == null;
        }

        /// <summary>Adds every parameter referenced anywhere in the tree to <paramref name="set"/>.</summary>
        public void CollectParameters(HashSet<string> set)
        {
            if (Root == null || set == null)
                return;
            CollectParametersR(Root, set);
        }

        private static void CollectParametersR(BoolNode node, HashSet<string> set)
        {
            if (node is BoolTermLeaf leaf && leaf.Term.Parameter != null)
                set.Add(leaf.Term.Parameter);
            else if (node is BoolAnd a)
                foreach (var c in a.Children)
                    CollectParametersR(c, set);
            else if (node is BoolOr o)
                foreach (var c in o.Children)
                    CollectParametersR(c, set);
            else if (node is BoolNot n)
                CollectParametersR(n.Child, set);
        }

        /// <summary>True when the (normalized) expression is a single term.</summary>
        public bool TryGetSingleTerm(out BoolTerm term)
        {
            term = default;
            if (Root is BoolTermLeaf leaf)
            {
                term = leaf.Term;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Converts this expression to simplified DNF (flat AND term lists) for use with
        /// Unity transitions, which only support a flat AND of conditions per transition.
        /// Distributes any And-over-Or structure, then simplifies each clause (dedupe,
        /// contradiction/subsumption via <see cref="SimplifyPair"/>) and removes duplicate
        /// and subsumed clauses. A null Root returns an empty list (unconditional);
        /// an unsatisfiable root throws.
        /// </summary>
        public List<List<BoolTerm>> ToDisjunctiveClauses()
        {
            var dnf = DnfOf(Root == null ? new BoolConstant(true) : Root);
            if (dnf.Count == 0)
                throw new InvalidOperationException("Cannot project an unsatisfiable expression to DNF");
            SimplifyDnf(dnf);
            if (dnf.Count == 1 && dnf[0].Count == 0) // tautology -> unconditional -> no clauses
                return new List<List<BoolTerm>>();
            return dnf;
        }

        /// <summary>
        /// Simplifies a DNF clause list in place: each clause is deduped and reduced with
        /// <see cref="SimplifyPair"/> (a contradicted clause is dropped), then duplicate
        /// and subsumed clauses (one whose terms are a superset of another's) are removed.
        /// </summary>
        private void SimplifyDnf(List<List<BoolTerm>> clauses)
        {
            var remove = new bool[clauses.Count];
            for (var i = 0; i < clauses.Count; i++)
            {
                var clause = clauses[i];

                // Dedupe first, then reduce same-parameter pairs until stable.
                for (var j = clause.Count - 1; j > 0; j--)
                    for (var k = 0; k < j; k++)
                        if (clause[j].Equals(clause[k]))
                        {
                            clause.RemoveAt(j);
                            break;
                        }

                var changed = true;
                while (changed && clause.Count > 1)
                {
                    changed = false;
                    for (var a = 0; a < clause.Count - 1; a++)
                    {
                        for (var b = clause.Count - 1; b > a; b--)
                        {
                            var r = SimplifyPair(clause[a], clause[b]);
                            if (r == PairResult.Contradiction)
                            {
                                remove[i] = true; // contradiction: this clause is unsatisfiable
                                break;
                            }
                            if (r == PairResult.DropFirst)
                            {
                                clause.RemoveAt(a);
                                a--;
                                changed = true;
                            }
                            else if (r == PairResult.DropSecond)
                            {
                                clause.RemoveAt(b);
                                changed = true;
                            }
                        }
                        if (remove[i])
                            break;
                    }
                }

                if (remove[i])
                    continue;

                // Skip clauses already covered by a kept clause (subset relation).
                var redundant = false;
                for (var j = 0; j < i; j++)
                {
                    if (remove[j])
                        continue;
                    if (ClauseSubsumes(clauses[j], clause))
                    {
                        redundant = true;
                        break;
                    }
                }
                if (!redundant)
                {
                    // Also absorb any earlier kept clauses this one subsumes.
                    for (var j = 0; j < i; j++)
                    {
                        if (!remove[j] && ClauseSubsumes(clause, clauses[j]))
                            remove[j] = true;
                    }
                }
                else
                    remove[i] = true;
            }

            for (var i = clauses.Count - 1; i >= 0; i--)
                if (remove[i])
                    clauses.RemoveAt(i);
        }

        /// <summary>True when every term of <paramref name="subset"/> appears in <paramref name="superset"/>.</summary>
        private static bool ClauseSubsumes(List<BoolTerm> subset, List<BoolTerm> superset)
        {
            for (var i = 0; i < subset.Count; i++)
            {
                var found = false;
                for (var j = 0; j < superset.Count; j++)
                {
                    if (subset[i].Equals(superset[j]))
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

        // DNF (list of flat conjunctions) of <paramref name="node"/> itself.
        // An empty result means unsatisfiable; a single empty list maps to true/unconditional.
        private List<List<BoolTerm>> DnfOf(BoolNode node)
        {
            switch (node)
            {
                case BoolConstant c:
                    return c.Value
                        ? SingleClause() // true -> one empty conjunction
                        : new List<List<BoolTerm>>(); // false -> unsatisfiable
                case BoolTermLeaf leaf:
                    return SingleClause(leaf.Term);
                case BoolNot n:
                    return DnfOfNot(n.Child);
                case BoolOr or:
                    return DnfConcat(or.Children);
                case BoolAnd and:
                    return DnfDistribute(and.Children);
                default:
                    throw new InvalidOperationException($"DNF projection failed for {node?.GetType().Name}");
            }
        }

        // DNF of the negation of <paramref name="node"/>.
        private List<List<BoolTerm>> DnfOfNot(BoolNode node)
        {
            switch (node)
            {
                case BoolConstant c:
                    return DnfOf(new BoolConstant(!c.Value));
                case BoolTermLeaf leaf:
                    return SingleClause(NegateTerm(leaf).Term);
                case BoolNot n:
                    return DnfOf(n.Child); // double negation
                case BoolAnd a:
                {
                    // !(a & b & ...) = !a | !b | ...
                    var disjuncts = new List<BoolNode>(a.Children.Count);
                    foreach (var child in a.Children)
                        disjuncts.Add(new BoolNot(child));
                    return DnfConcat(disjuncts);
                }
                case BoolOr o:
                {
                    // !(a | b | ...) = !a & !b & ...
                    var conjuncts = new List<BoolNode>(o.Children.Count);
                    foreach (var child in o.Children)
                        conjuncts.Add(new BoolNot(child));
                    return DnfDistribute(conjuncts);
                }
                default:
                    throw new InvalidOperationException($"DNF projection failed for !({node?.GetType().Name})");
            }
        }

        private static List<List<BoolTerm>> SingleClause(BoolTerm term)
            => new List<List<BoolTerm>>() { new List<BoolTerm>() { term } };

        private static List<List<BoolTerm>> SingleClause()
            => new List<List<BoolTerm>>() { new List<BoolTerm>() };

        // Union of the children's DNFs (an Or).
        private List<List<BoolTerm>> DnfConcat(List<BoolNode> children)
        {
            var result = new List<List<BoolTerm>>();
            for (var i = 0; i < children.Count; i++)
            {
                var childDnf = DnfOf(children[i]);
                for (var j = 0; j < childDnf.Count; j++)
                    result.Add(childDnf[j]);
            }
            return result;
        }

        // Cartesian product of the children's DNFs (an And): each resulting conjunction
        // is one conjunction from every child, concatenated. A child whose DNF is empty
        // (unsatisfiable) collapses the whole thing to empty.
        private List<List<BoolTerm>> DnfDistribute(List<BoolNode> children)
        {
            var result = new List<List<BoolTerm>>() { new List<BoolTerm>() };
            for (var i = 0; i < children.Count; i++)
            {
                var childDnf = DnfOf(children[i]);
                var next = new List<List<BoolTerm>>();
                for (var x = 0; x < result.Count; x++)
                    for (var y = 0; y < childDnf.Count; y++)
                    {
                        var conj = new List<BoolTerm>(result[x].Count + childDnf[y].Count);
                        conj.AddRange(result[x]);
                        conj.AddRange(childDnf[y]);
                        next.Add(conj);
                    }
                result = next;
            }
            return result;
        }

        /// <summary>
        /// Heuristically rewrites <see cref="Root"/> with fewer operators, keeping it
        /// logically equivalent. Two candidates are compared: the current normalized
        /// tree, and a DNF-derived form (distribute, simplify clauses, consensus
        /// absorption, factor out common terms, normalize again). Only the strictly
        /// cheaper candidate is kept, so the operation is idempotent and can never
        /// make the expression longer.
        /// </summary>
        public void Minify()
        {
            if (Root == null)
                return;
            Normalize();
            var baseline = NodeCost(Root);
            try
            {
                var clauses = ToDisjunctiveClauses();
                ConsensusAbsorb(clauses);
                if (clauses.Count == 0) // tautology -> unconditional
                {
                    if (baseline > 0)
                        Root = null;
                    return;
                }
                var candidate = new BoolExpression();
                candidate.Root = FactorClauses(clauses);
                candidate._parameterTypes = _parameterTypes;
                candidate.Normalize();
                if (NodeCost(candidate.Root) < baseline)
                    Root = candidate.Root;
            }
            catch (InvalidOperationException)
            {
                // Unsatisfiable expression: DNF projection failed. Keep the original tree.
            }
        }

        // Operator count used for minimization: each term = 1, each And/Or beyond the
        // first child = 1 connective, each Not = 1. Constants are free.
        private static int NodeCost(BoolNode node)
        {
            if (node == null)
                return 0;
            if (node is BoolTermLeaf)
                return 1;
            if (node is BoolConstant)
                return 0;
            if (node is BoolNot n)
                return 1 + NodeCost(n.Child);
            if (node is BoolAnd a)
            {
                var sum = a.Children.Count - 1;
                foreach (var child in a.Children)
                    sum += NodeCost(child);
                return sum;
            }
            if (node is BoolOr o)
            {
                var sum = o.Children.Count - 1;
                foreach (var child in o.Children)
                    sum += NodeCost(child);
                return sum;
            }
            throw new InvalidOperationException("Unknown node type in NodeCost");
        }

        // Repeatedly add consensus clauses until fixpoint: for two clauses C1, C2 that
        // contain a complementary term pair (x, !x), the consensus (C1 u C2) \ {x, !x}
        // is implied by C1 | C2 and usually subsumes both - the re-simplification then
        // drops the terms/clauses it covers. Capped so pathological input cannot loop.
        private void ConsensusAbsorb(List<List<BoolTerm>> clauses)
        {
            for (var pass = 0; pass < 64; pass++)
            {
                var next = TryFindConsensus(clauses);
                if (next == null)
                    return;
                clauses.Add(next);
                SimplifyDnf(clauses);
            }
        }

        // Finds one pair of clauses with a complementary term pair and returns their
        // consensus (union minus the pair), or null when none adds new information
        // (a consensus already subsumed by an existing clause counts as none).
        private List<BoolTerm> TryFindConsensus(List<List<BoolTerm>> clauses)
        {
            for (var i = 0; i < clauses.Count; i++)
            {
                var ci = clauses[i];
                for (var j = i + 1; j < clauses.Count; j++)
                {
                    var cj = clauses[j];
                    for (var a = 0; a < ci.Count; a++)
                    {
                        for (var b = 0; b < cj.Count; b++)
                        {
                            if (!TermsComplement(ci[a], cj[b]))
                                continue;
                            var consensus = new List<BoolTerm>(ci.Count + cj.Count - 2);
                            for (var x = 0; x < ci.Count; x++)
                                if (x != a)
                                    consensus.Add(ci[x]);
                            for (var x = 0; x < cj.Count; x++)
                                if (x != b)
                                    consensus.Add(cj[x]);
                            var redundant = false;
                            for (var k = 0; k < clauses.Count; k++)
                            {
                                if (ClauseSubsumes(clauses[k], consensus))
                                {
                                    redundant = true;
                                    break;
                                }
                            }
                            if (redundant)
                                continue; // try another pair
                            return consensus;
                        }
                    }
                }
            }
            return null;
        }

        // True when two terms on the same parameter exclude each other's satisfaction
        // - i.e. they are exactly one another's logical negation within the
        // parameter's value domain.
        private bool TermsComplement(BoolTerm a, BoolTerm b)
        {
            if (!string.Equals(a.Parameter, b.Parameter, StringComparison.Ordinal))
                return false;
            switch (a.Op)
            {
                case BoolTermOp.If:
                    return b.Op == BoolTermOp.IfNot;
                case BoolTermOp.IfNot:
                    return b.Op == BoolTermOp.If;
                case BoolTermOp.Equals:
                    return b.Op == BoolTermOp.NotEquals && b.Threshold == a.Threshold;
                case BoolTermOp.NotEquals:
                    return b.Op == BoolTermOp.Equals && b.Threshold == a.Threshold;
                // Int parameters are discrete: !(x > t) == x < t + 1, !(x < t) == x > t - 1.
                case BoolTermOp.Greater:
                    return IsInt(a.Parameter) && b.Op == BoolTermOp.Less && b.Threshold == a.Threshold + 1f;
                case BoolTermOp.Less:
                    return IsInt(a.Parameter) && b.Op == BoolTermOp.Greater && b.Threshold == a.Threshold - 1f;
                default:
                    return false;
            }
        }

        // Builds a (possibly nested) tree from DNF clauses by recursively factoring a
        // common term:  T & A | T & B | C  ==  T & (A | B) | C.
        private BoolNode FactorClauses(List<List<BoolTerm>> clauses)
        {
            if (clauses.Count == 0)
                return new BoolConstant(true);
            if (clauses.Count == 1)
                return NodeFromClause(clauses[0]);

            // Pick the term that appears in the most clauses.
            var counts = new Dictionary<BoolTerm, int>();
            foreach (var clause in clauses)
            {
                foreach (var t in clause)
                {
                    int previous;
                    counts.TryGetValue(t, out previous);
                    counts[t] = previous + 1;
                }
            }
            var best = default(BoolTerm);
            var bestCount = 1;
            foreach (var kv in counts)
            {
                if (kv.Value > bestCount)
                {
                    bestCount = kv.Value;
                    best = kv.Key;
                    if (bestCount >= clauses.Count)
                        break; // present in every clause - cannot do better
                }
            }

            if (bestCount <= 1)
            {
                // No shared term: plain OR of AND clauses.
                var kids = new List<BoolNode>(clauses.Count);
                foreach (var clause in clauses)
                    kids.Add(NodeFromClause(clause));
                return new BoolOr(kids);
            }

            var withTerm = new List<List<BoolTerm>>();
            var withoutTerm = new List<List<BoolTerm>>();
            foreach (var clause in clauses)
            {
                if (clause.Contains(best))
                {
                    var reduced = new List<BoolTerm>(clause.Count - 1);
                    foreach (var t in clause)
                        if (!t.Equals(best))
                            reduced.Add(t);
                    withTerm.Add(reduced);
                }
                else
                    withoutTerm.Add(clause);
            }

            var branch = FactorClauses(withTerm);
            BoolNode folded;
            if (branch is BoolAnd and)
            {
                var merged = new List<BoolNode>(and.Children.Count + 1);
                merged.Add(new BoolTermLeaf(best));
                foreach (var c in and.Children)
                    merged.Add(c);
                folded = merged.Count == 1 ? merged[0] : new BoolAnd(merged);
            }
            else if (branch is BoolConstant bc && bc.Value)
            {
                folded = new BoolTermLeaf(best); // T & true == T
            }
            else
            {
                folded = new BoolAnd(new List<BoolNode> { new BoolTermLeaf(best), branch });
            }
            if (withoutTerm.Count == 0)
                return folded;
            var rest = FactorClauses(withoutTerm);
            return new BoolOr(new List<BoolNode> { folded, rest });
        }

        // Turns one flat DNF conjunction into a tree node.
        private static BoolNode NodeFromClause(List<BoolTerm> clause)
        {
            if (clause.Count == 0)
                return new BoolConstant(true);
            if (clause.Count == 1)
                return new BoolTermLeaf(clause[0]);
            var kids = new List<BoolNode>(clause.Count);
            foreach (var t in clause)
                kids.Add(new BoolTermLeaf(t));
            return new BoolAnd(kids);
        }

        public string ToDisplayString()
        {
            if (Root == null)
                return "";
            var sb = new StringBuilder();
            Root.AppendToDisplay(sb, -1);
            return sb.ToString();
        }

        /// <summary>
        /// Prints the expression in simplified DNF: flat AND clauses joined with " | "
        /// (e.g. "(a | b) & (a | !c)" -> "a | b & !c").
        /// </summary>
        public string ToDnfString()
        {
            var clauses = ToDisjunctiveClauses();
            if (clauses.Count == 0)
                return "";
            var sb = new StringBuilder();
            for (var i = 0; i < clauses.Count; i++)
            {
                if (i > 0)
                    sb.Append(" | ");
                var clause = clauses[i];
                for (var j = 0; j < clause.Count; j++)
                {
                    if (j > 0)
                        sb.Append(" & ");
                    sb.Append(clause[j].ToString());
                }
            }
            return sb.ToString();
        }

        public override string ToString() => ToDisplayString();

        public bool Differs(BoolExpression other)
            => other == null || !IsSameExpression(other);

        public bool IsSameExpression(BoolExpression other)
        {
            if (other == null)
                return false;
            if (Root == null || other.Root == null)
            {
                var bothEmpty = Root == null && other.Root == null;
                var bothTrue = Root is BoolConstant ca && ca.Value && other.Root is BoolConstant cb && cb.Value;
                return bothEmpty || bothTrue;
            }
            if (Root is BoolConstant c1 && other.Root is BoolConstant c2)
                return c1.Value == c2.Value;
            return Root.EqualsNode(other.Root);
        }

        /// <summary>
        /// Simplifies the tree (rewriting <see cref="Root"/> in place): flattens same-kind
        /// And/Or, removes double negations, pushes Not down via De Morgan so it only sits
        /// directly on a term, folds constants, removes duplicate children, and reduces
        /// same-parameter term pairs (subsumption / contradiction). Does NOT perform
        /// absorption.
        /// </summary>
        public void Normalize()
        {
            if (Root != null)
                Root = NormalizeNode(Root);
        }

        /// <summary>Normalizes <paramref name="node"/> and returns the (possibly new) simplified node.</summary>
        private BoolNode NormalizeNode(BoolNode node)
        {
            while (true)
            {
                if (node is BoolConstant || node is BoolTermLeaf)
                    return node;

                if (node is BoolNot n)
                {
                    var inner = NormalizeNode(n.Child);
                    if (inner is BoolConstant c)
                        return new BoolConstant(!c.Value);
                    if (inner is BoolAnd andNode)
                    {
                        // De Morgan: !(a & b & ...) = !a | !b | ...
                        var kids = new List<BoolNode>(andNode.Children.Count);
                        foreach (var child in andNode.Children)
                            kids.Add(new BoolNot(child));
                        node = kids.Count == 1 ? kids[0] : new BoolOr(kids);
                        continue;
                    }
                    if (inner is BoolOr orNode)
                    {
                        // De Morgan: !(a | b | ...) = !a & !b & ...
                        var kids = new List<BoolNode>(orNode.Children.Count);
                        foreach (var child in orNode.Children)
                            kids.Add(new BoolNot(child));
                        node = kids.Count == 1 ? kids[0] : new BoolAnd(kids);
                        continue;
                    }
                    if (inner is BoolTermLeaf termLeaf)
                        return NegateTerm(termLeaf);
                    return new BoolConstant(true); // unreachable - NormalizeNode never returns a Not
                }

                if (node is BoolAnd a)
                {
                    var result = new List<BoolNode>(a.Children.Count);
                    for (var i = 0; i < a.Children.Count; i++)
                    {
                        var child = NormalizeNode(a.Children[i]);
                        if (child is BoolConstant c)
                        {
                            if (!c.Value)
                                return child; // And{..., false} -> false
                            continue; // swallow true
                        }
                        if (child is BoolAnd inner)
                        {
                            foreach (var gc in inner.Children)
                                result.Add(gc);
                            continue;
                        }
                        if (!ContainsNode(result, child))
                            result.Add(child);
                    }
                    if (result.Count == 0)
                        return new BoolConstant(true);
                    if (result.Count == 1)
                        return result[0];
                    if (ReduceTermPairsInPlace(result, out var becameFalse))
                    {
                        if (becameFalse)
                            return new BoolConstant(false);
                        if (result.Count == 0)
                            return new BoolConstant(true);
                        if (result.Count == 1)
                            return result[0];
                    }
                    return new BoolAnd(result);
                }

                if (node is BoolOr o)
                {
                    var result = new List<BoolNode>(o.Children.Count);
                    for (var i = 0; i < o.Children.Count; i++)
                    {
                        var child = NormalizeNode(o.Children[i]);
                        if (child is BoolConstant c)
                        {
                            if (c.Value)
                                return child; // Or{..., true} -> true
                            continue; // swallow false
                        }
                        if (child is BoolOr inner)
                        {
                            foreach (var gc in inner.Children)
                                result.Add(gc);
                            continue;
                        }
                        if (!ContainsNode(result, child))
                            result.Add(child);
                    }
                    // No pair reduction here: a contradiction cannot appear inside an Or,
                    // and subsumption between Or children (absorption) is deliberately not
                    // performed so equality behavior matches the old DNF normalizer.
                    if (result.Count == 0)
                        return new BoolConstant(false);
                    if (result.Count == 1)
                        return result[0];
                    return new BoolOr(result);
                }

                return node;
            }
        }

        private static bool ContainsNode(List<BoolNode> list, BoolNode node)
        {
            for (var i = 0; i < list.Count; i++)
                if (node.EqualsNode(list[i]))
                    return true;
            return false;
        }

        /// <summary>
        /// Reduces pairs of same-parameter terms in <paramref name="children"/> in place
        /// (subsumption drops the weaker term, equal terms dedupe, contradiction drops the
        /// whole list via <paramref name="becameFalse"/>). Returns true when anything changed.
        /// </summary>
        private bool ReduceTermPairsInPlace(List<BoolNode> children, out bool becameFalse)
        {
            becameFalse = false;
            var pass = false;
            var loop = true;
            while (loop && children.Count > 1)
            {
                loop = false;
                for (var i = 0; i < children.Count - 1; i++)
                {
                    if (!(children[i] is BoolTermLeaf li))
                        continue;
                    for (var j = children.Count - 1; j > i; j--)
                    {
                        if (!(children[j] is BoolTermLeaf lj))
                            continue;
                        var r = SimplifyPair(li.Term, lj.Term);
                        if (r == PairResult.Contradiction)
                        {
                            becameFalse = true;
                            return true;
                        }
                        if (r == PairResult.DropFirst)
                        {
                            children.RemoveAt(i);
                            i--;
                            pass = true;
                        }
                        else if (r == PairResult.DropSecond)
                        {
                            children.RemoveAt(j);
                            pass = true;
                        }
                    }
                    if (pass)
                    {
                        loop = true; // rescan from the top
                        break;
                    }
                }
            }
            return pass || becameFalse;
        }

        private BoolTermLeaf NegateTerm(BoolTermLeaf leaf)
        {
            // Int parameters are discrete, so the inverted comparison IS representable:
            // !(x > t) == x <= t == x < t+1;   !(x < t) == x >= t == x > t-1.
            switch (leaf.Term.Op)
            {
                case BoolTermOp.If:
                    return new BoolTermLeaf(new BoolTerm(leaf.Term.Parameter, BoolTermOp.IfNot));
                case BoolTermOp.IfNot:
                    return new BoolTermLeaf(new BoolTerm(leaf.Term.Parameter, BoolTermOp.If));
                case BoolTermOp.Equals:
                    return new BoolTermLeaf(new BoolTerm(leaf.Term.Parameter, BoolTermOp.NotEquals, leaf.Term.Threshold));
                case BoolTermOp.NotEquals:
                    return new BoolTermLeaf(new BoolTerm(leaf.Term.Parameter, BoolTermOp.Equals, leaf.Term.Threshold));
                case BoolTermOp.Greater:
                    if (IsInt(leaf.Term.Parameter))
                        return new BoolTermLeaf(new BoolTerm(leaf.Term.Parameter, BoolTermOp.Less, leaf.Term.Threshold + 1f));
                    throw new ParseException("Cannot negate '>' - the inverted form is not representable for this parameter type");
                case BoolTermOp.Less:
                    if (IsInt(leaf.Term.Parameter))
                        return new BoolTermLeaf(new BoolTerm(leaf.Term.Parameter, BoolTermOp.Greater, leaf.Term.Threshold - 1f));
                    throw new ParseException("Cannot negate '<' - the inverted form is not representable for this parameter type");
                default:
                    throw new ParseException("Cannot negate this condition");
            }
        }

        private bool IsInt(string parameter)
            => _parameterTypes != null
            && parameter != null
            && _parameterTypes.TryGetValue(parameter, out var t)
            && t == AnimatorControllerParameterType.Int;

        private enum PairResult
        {
            KeepBoth,
            DropFirst,
            DropSecond,
            Contradiction
        }

        // "If" is treated as "parameter = 1". Reduces a pair of same-parameter terms
        // to a single term if one subsumes the other, or flags a contradiction.
        private PairResult SimplifyPair(BoolTerm a, BoolTerm b)
        {
            if (!string.Equals(a.Parameter, b.Parameter, StringComparison.Ordinal))
                return PairResult.KeepBoth;

            // Int parameters are discrete: x >= t is the same as x > t - 1, so
            // for Int parameters x = t is the same as x > t - 1 & x < t + 1.
            // x > ta & x > tb -> x > max(ta, tb). x < ta & x < tb -> x < min(ta, tb).
            int intOpA = -1, intOpB = -1;
            if (IsInt(a.Parameter))
            {
                intOpA = a.Op == BoolTermOp.Greater ? 1 : a.Op == BoolTermOp.Less ? -1 : 0;
                intOpB = b.Op == BoolTermOp.Greater ? 1 : b.Op == BoolTermOp.Less ? -1 : 0;
            }

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
                        case BoolTermOp.Greater:
                            // a > ta  and  b > tb:  the higher threshold is stronger.
                            if (b.Threshold <= a.Threshold)
                                return PairResult.DropSecond;
                            // Int: both are upper bounds on the same discrete line.
                            if (intOpA == 1 && intOpB == 1)
                            {
                                if (a.Threshold >= b.Threshold) return PairResult.DropSecond;
                                return PairResult.DropFirst;
                            }
                            break;
                        case BoolTermOp.Less:
                            if (a.Threshold >= b.Threshold)
                                return PairResult.Contradiction;
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
                        case BoolTermOp.Greater:
                            if (b.Threshold >= a.Threshold)
                                return PairResult.Contradiction;
                            break;
                        case BoolTermOp.Less:
                            // a < ta  and  b < tb:  the lower threshold is stronger.
                            if (b.Threshold >= a.Threshold)
                                return PairResult.DropSecond;
                            // Int: both are lower bounds on the same discrete line.
                            if (intOpA == -1 && intOpB == -1)
                            {
                                if (a.Threshold <= b.Threshold) return PairResult.DropSecond;
                                return PairResult.DropFirst;
                            }
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

            public void ParseIntoRoot(BoolExpression expr)
            {
                SkipWs();
                if (string.IsNullOrWhiteSpace(_text))
                {
                    expr.Root = null;
                    return;
                }
                expr.Root = ParseOr();
                SkipWs();
                if (_pos != _text.Length)
                    throw new ParseException($"Unexpected '{_text[_pos]}' at position {_pos}");
            }

            private BoolNode ParseOr()
            {
                var children = new List<BoolNode> { ParseAnd() };
                while (true)
                {
                    SkipWs();
                    if (!MatchOr())
                        break;
                    SkipWs();
                    children.Add(ParseAnd());
                }
                return children.Count == 1 ? children[0] : new BoolOr(children);
            }

            private BoolNode ParseAnd()
            {
                var children = new List<BoolNode> { ParseNot() };
                while (true)
                {
                    SkipWs();
                    if (!MatchAnd())
                        break;
                    SkipWs();
                    children.Add(ParseNot());
                }
                return children.Count == 1 ? children[0] : new BoolAnd(children);
            }

            private BoolNode ParseNot()
            {
                if (Match("!"))
                {
                    SkipWs();
                    return new BoolNot(ParseNot());
                }
                return ParsePrimary();
            }

            private BoolNode ParsePrimary()
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
                return new BoolTermLeaf(ParseTerm());
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