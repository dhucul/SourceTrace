using System;
using System.Runtime.CompilerServices;

namespace TraceDemo
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            int total = 0; // Set a breakpoint here, then open Tools > SourceTrace.
            for (int i = 1; i <= 4; i++)
            {
                total += Square(i); // Trace Into enters Square. Trace Over skips its body.
            }
            Console.WriteLine("Total = " + total);
            Console.WriteLine("Factorial(4) = " + Factorial(4));
            if (Array.IndexOf(args, "--exception") >= 0)
                throw new InvalidOperationException("SourceTrace exception-stop demonstration.");
            Console.WriteLine("Trace complete."); // The program exits without waiting for input.
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Square(int value)
        {
            int result = value * value;
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Factorial(int value)
        {
            if (value <= 1) return 1;
            return value * Factorial(value - 1);
        }
    }
}
