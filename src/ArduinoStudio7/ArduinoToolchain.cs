using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ArduinoStudio7
{
    // Стартираме външния Arduino CLI, който управлява компилаторите и board core пакетите.
    internal sealed class ArduinoToolchain
    {
        public string CliPath { get; set; }

        public ArduinoToolchain()
        {
            // Първо търсим преносимо копие в папка tools до приложението.
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "arduino-cli.exe");
            CliPath = File.Exists(local) ? local : "arduino-cli.exe";
        }

        public string Run(string arguments, string workingDirectory, Action<string> output)
        {
            // Проверяваме пътя, ако потребителят е избрал конкретен изпълним файл.
            if (!String.Equals(CliPath, "arduino-cli.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(CliPath))
                throw new FileNotFoundException("Не е намерен Arduino CLI: " + CliPath);

            var info = new ProcessStartInfo
            {
                FileName = CliPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Прочитаме stdout и stderr асинхронно, за да избегнем блокиране на процеса.
            using (var process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += (s, e) => { if (e.Data != null) output(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) output(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit(); // Изчакваме процеса да приключи.
                process.WaitForExit(); // Изчакваме и последните асинхронни редове от stdout/stderr.
                output("Arduino CLI завърши с код: " + process.ExitCode);
                if (process.ExitCode != 0) output("Компилацията/командата е неуспешна. Проверете грешките по-горе.");
                return process.ExitCode.ToString();
            }
        }
    }
}