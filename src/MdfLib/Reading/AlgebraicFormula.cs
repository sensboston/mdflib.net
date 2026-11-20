// Copyright (c) 2026 SeNS
// SPDX-License-Identifier: MIT
using System;
using System.Globalization;

namespace MdfLib.Reading
{
    /// <summary>
    /// algebraic conversion formula of one variable, compiled once into a node array and evaluated in IEEE binary64
    /// without allocation. Grammar (whitespace ignored):
    /// expr:= term (('+'|'-') term)*; term:= unary (('*'|'/') unary)*; unary:= ('+'|'-') unary | power;
    /// power:= primary (('^'|'**') unary)? (right associative, so -X^2 = -(X^2));
    /// primary:= number | X | x | X1 | x1 | function '(' expr ')' | pow '(' expr ',' expr ')' | '(' expr ')'.
    /// Functions: abs sin cos tan asin acos atan arcsin arccos arctan sinh cosh tanh exp ln log (natural) log10 sqrt.
    /// Anything else is not compiled (the conversion stays unsupported).
    /// </summary>
    internal sealed class AlgebraicFormula
    {
        internal const int MaximumLength = 4096;
        internal const int MaximumNodes = 1024;
        internal const int MaximumDepth = 32;

        private enum Op : byte
        {
            Constant, Variable, Negate, Add, Subtract, Multiply, Divide, Power,
            Abs, Sin, Cos, Tan, Asin, Acos, Atan, Sinh, Cosh, Tanh, Exp, Log, Log10, Sqrt
        }

        private readonly Op[] ops;
        private readonly int[] left;
        private readonly int[] right;
        private readonly double[] constants;
        private readonly int root;

        private AlgebraicFormula(Op[] ops, int[] left, int[] right, double[] constants, int root)
        {
            this.ops = ops;
            this.left = left;
            this.right = right;
            this.constants = constants;
            this.root = root;
        }

        /// <summary>Evaluates the formula for the raw value widened to binary64.</summary>
        internal double Evaluate(double x) { return Evaluate(root, x); }

        private double Evaluate(int node, double x)
        {
            switch (ops[node])
            {
                case Op.Constant: return constants[node];
                case Op.Variable: return x;
                case Op.Negate: return -Evaluate(left[node], x);
                case Op.Add: return Evaluate(left[node], x) + Evaluate(right[node], x);
                case Op.Subtract: return Evaluate(left[node], x) - Evaluate(right[node], x);
                case Op.Multiply: return Evaluate(left[node], x) * Evaluate(right[node], x);
                case Op.Divide: return Evaluate(left[node], x) / Evaluate(right[node], x);
                case Op.Power: return Math.Pow(Evaluate(left[node], x), Evaluate(right[node], x));
                case Op.Abs: return Math.Abs(Evaluate(left[node], x));
                case Op.Sin: return Math.Sin(Evaluate(left[node], x));
                case Op.Cos: return Math.Cos(Evaluate(left[node], x));
                case Op.Tan: return Math.Tan(Evaluate(left[node], x));
                case Op.Asin: return Math.Asin(Evaluate(left[node], x));
                case Op.Acos: return Math.Acos(Evaluate(left[node], x));
                case Op.Atan: return Math.Atan(Evaluate(left[node], x));
                case Op.Sinh: return Math.Sinh(Evaluate(left[node], x));
                case Op.Cosh: return Math.Cosh(Evaluate(left[node], x));
                case Op.Tanh: return Math.Tanh(Evaluate(left[node], x));
                case Op.Exp: return Math.Exp(Evaluate(left[node], x));
                case Op.Log: return Math.Log(Evaluate(left[node], x));
                case Op.Log10: return Math.Log10(Evaluate(left[node], x));
                default: return Math.Sqrt(Evaluate(left[node], x));
            }
        }

        /// <summary>Compiles <paramref name="text"/>; null when it is empty, too large or outside the grammar.</summary>
        internal static AlgebraicFormula Compile(string text)
        {
            if (text == null || text.Length > MaximumLength) { return null; }
            Parser parser = new Parser(text);
            try
            {
                parser.Skip();
                if (parser.AtEnd) { return null; }
                int root = parser.Expression(0);
                parser.Skip();
                if (!parser.AtEnd) { return null; }
                return new AlgebraicFormula(parser.Ops(), parser.Left(), parser.Right(), parser.Constants(), root);
            }
            catch (FormatException)
            {
                return null; // Outside the grammar or the node/depth bounds: the codec keeps the conversion unsupported.
            }
        }

        private sealed class Parser
        {
            private readonly string text;
            private int position;
            private int count;
            private readonly Op[] ops = new Op[MaximumNodes];
            private readonly int[] left = new int[MaximumNodes];
            private readonly int[] right = new int[MaximumNodes];
            private readonly double[] constants = new double[MaximumNodes];

            internal Parser(string text) { this.text = text; }

            internal bool AtEnd => position >= text.Length;

            internal Op[] Ops() { return Trim(ops); }
            internal int[] Left() { return Trim(left); }
            internal int[] Right() { return Trim(right); }
            internal double[] Constants() { return Trim(constants); }

            private T[] Trim<T>(T[] source)
            {
                T[] result = new T[count];
                Array.Copy(source, result, count);
                return result;
            }

            internal void Skip()
            {
                while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n'))
                {
                    position++;
                }
            }

            private bool Accept(string token)
            {
                Skip();
                if (string.CompareOrdinal(text, position, token, 0, token.Length) != 0) { return false; }
                position += token.Length;
                return true;
            }

            private void Expect(string token)
            {
                if (!Accept(token)) { throw new FormatException(); }
            }

            private int Node(Op op, int a = -1, int b = -1, double value = 0)
            {
                if (count == MaximumNodes) { throw new FormatException(); }
                ops[count] = op;
                left[count] = a;
                right[count] = b;
                constants[count] = value;
                return count++;
            }

            private static void Check(int depth)
            {
                if (depth > MaximumDepth) { throw new FormatException(); }
            }

            internal int Expression(int depth)
            {
                Check(depth);
                int result = Term(depth);
                while (true)
                {
                    if (Accept("+")) { result = Node(Op.Add, result, Term(depth)); }
                    else if (Accept("-")) { result = Node(Op.Subtract, result, Term(depth)); }
                    else { return result; }
                }
            }

            private int Term(int depth)
            {
                int result = Unary(depth);
                while (true)
                {
                    Skip();
                    if (position + 1 < text.Length && text[position] == '*' && text[position + 1] == '*') { return result; }
                    if (Accept("*")) { result = Node(Op.Multiply, result, Unary(depth)); }
                    else if (Accept("/")) { result = Node(Op.Divide, result, Unary(depth)); }
                    else { return result; }
                }
            }

            private int Unary(int depth)
            {
                Check(depth);
                if (Accept("+")) { return Unary(depth + 1); }
                if (Accept("-")) { return Node(Op.Negate, Unary(depth + 1)); }
                int primary = Primary(depth);
                if (Accept("**") || Accept("^")) { return Node(Op.Power, primary, Unary(depth + 1)); }
                return primary;
            }

            private int Primary(int depth)
            {
                Skip();
                if (AtEnd) { throw new FormatException(); }
                char c = text[position];
                if (IsDigit(c) || (c == '.' && position + 1 < text.Length && IsDigit(text[position + 1]))) { return Number(); }
                if (Accept("("))
                {
                    int inner = Expression(depth + 1);
                    Expect(")");
                    return inner;
                }
                if (!IsLetter(c)) { throw new FormatException(); }
                int start = position;
                while (position < text.Length && (IsLetter(text[position]) || IsDigit(text[position]))) { position++; }
                string name = text.Substring(start, position - start);
                if (name == "X" || name == "x" || name == "X1" || name == "x1") { return Node(Op.Variable); }
                if (name == "pow")
                {
                    Expect("(");
                    int a = Expression(depth + 1);
                    Expect(",");
                    int b = Expression(depth + 1);
                    Expect(")");
                    return Node(Op.Power, a, b);
                }
                Op function = Function(name);
                Expect("(");
                int argument = Expression(depth + 1);
                Expect(")");
                return Node(function, argument);
            }

            private int Number()
            {
                int start = position;
                while (position < text.Length && IsDigit(text[position])) { position++; }
                if (position < text.Length && text[position] == '.')
                {
                    position++;
                    while (position < text.Length && IsDigit(text[position])) { position++; }
                }
                if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
                {
                    int exponent = position + 1;
                    if (exponent < text.Length && (text[exponent] == '+' || text[exponent] == '-')) { exponent++; }
                    if (exponent < text.Length && IsDigit(text[exponent]))
                    {
                        while (exponent < text.Length && IsDigit(text[exponent])) { exponent++; }
                        position = exponent;
                    }
                }
                double value = double.Parse(text.Substring(start, position - start), NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture);
                return Node(Op.Constant, value: value);
            }

            private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
            private static bool IsLetter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'; }

            private static Op Function(string name)
            {
                switch (name)
                {
                    case "abs": return Op.Abs;
                    case "sin": return Op.Sin;
                    case "cos": return Op.Cos;
                    case "tan": return Op.Tan;
                    case "asin": case "arcsin": return Op.Asin;
                    case "acos": case "arccos": return Op.Acos;
                    case "atan": case "arctan": return Op.Atan;
                    case "sinh": return Op.Sinh;
                    case "cosh": return Op.Cosh;
                    case "tanh": return Op.Tanh;
                    case "exp": return Op.Exp;
                    case "ln": case "log": return Op.Log;
                    case "log10": return Op.Log10;
                    case "sqrt": return Op.Sqrt;
                    default: throw new FormatException();
                }
            }
        }
    }
}
