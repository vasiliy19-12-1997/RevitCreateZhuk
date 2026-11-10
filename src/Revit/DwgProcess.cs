using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using RevitCreateZhuk.Core;

namespace RevitCreateZhuk
{
    internal static class DwgProcess
    {
        public static Drawing Read(string path)
        {
            string folder = Path.GetDirectoryName(typeof(DwgProcess).Assembly.Location);
            string exe = Path.Combine(folder, "DwgReader", "RevitCreateZhuk.DwgReader.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("В установке отсутствует модуль чтения DWG.", exe);
            using (var process = Process.Start(new ProcessStartInfo(exe, "\"" + Path.GetFullPath(path) + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            }))
            using (var output = new MemoryStream())
            {
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    process.Kill();
                    throw new TimeoutException("Чтение DWG превысило 2 минуты.");
                }
                Task.WaitAll(copy, error);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Не удалось прочитать DWG. " + error.Result.Trim());
                output.Position = 0;
                return Drawing.Read(output);
            }
        }
    }
}
