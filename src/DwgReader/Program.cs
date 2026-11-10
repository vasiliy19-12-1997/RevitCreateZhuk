using System;
using RevitCreateZhuk.Core;

namespace RevitCreateZhuk.DwgReader
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Укажите один DWG.");
                var drawing = new CadReader().Read(args[0]);
                drawing.Write(Console.OpenStandardOutput());
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }
    }
}
