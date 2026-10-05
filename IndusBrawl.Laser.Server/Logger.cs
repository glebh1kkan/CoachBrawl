namespace IndusBrawl.Laser.Server
{
    using IndusBrawl.Laser.Titan.Debug;
    using System;
    using System.IO;

    public static class Logger
    {
        private static readonly object _fileLock = new object();
        private const string LOG_PATH = "logs/server.log";
        private const long MAX_LOG_SIZE = 20_000_000;

        private static void ToFile(string line)
        {
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory("logs");
                    var info = new FileInfo(LOG_PATH);
                    if (info.Exists && info.Length > MAX_LOG_SIZE)
                    {
                        if (File.Exists(LOG_PATH + ".1")) File.Delete(LOG_PATH + ".1");
                        File.Move(LOG_PATH, LOG_PATH + ".1");
                    }
                    File.AppendAllText(LOG_PATH, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
                }
            }
            catch { }
        }

        public static void Print(string log)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("[DEBUG] " + log);
            ToFile("[DEBUG] " + log);
        }

        public static void Init()
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Debugger.SetListener(new DebuggerListener());
        }

        public static void Warning(string log)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("[WARNING] " + log);
            ToFile("[WARNING] " + log);
        }

        public static void Error(string log)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("[ERROR] " + log);
            ToFile("[ERROR] " + log);
        }
    }

    public class DebuggerListener : IDebuggerListener
    {
        public void Error(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("[LOGIC] Error: " + message);
        }

        public void Print(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("[LOGIC] Info: " + message);
        }

        public void Warning(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("[LOGIC] Warning: " + message);
        }
    }
}
