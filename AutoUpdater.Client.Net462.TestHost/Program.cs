using System;
using System.Threading;
using AutoUpdater.Client.Net462;

namespace AutoUpdater.Client.Net462.TestHost
{
    internal static class Program
    {
        private static readonly ManualResetEvent ExitEvent =
            new ManualResetEvent(false);

        private static int Main(string[] args)
        {
            var port = args.Length > 0
                ? int.Parse(args[0])
                : EmbeddedUpdateClient.DefaultPort;
            var connectionString =
                Environment.GetEnvironmentVariable(
                    "AUTOUPDATER_TEST_DATABASE");
            var client = new EmbeddedUpdateClient(
                new EmbeddedClientOptions
                {
                    DeviceId = "NET462-PROBE",
                    DeviceName = Environment.MachineName + " (.NET 4.6.2)",
                    CurrentVersion = "1.0.0.0",
                    Port = port,
                    InstallationDirectory =
                        AppDomain.CurrentDomain.BaseDirectory,
                    RestartExecutablePath =
                        "AutoUpdater.Client.Net462.TestHost.exe",
                    DatabaseConnectionString = connectionString
                });
            client.UpdateDecisionRequired += context =>
                UpdateDecision.Postpone;
            client.RollbackDecisionRequired += context =>
                UpdateDecision.Postpone;
            client.Error += exception =>
                Console.WriteLine("ERROR " + exception.Message);
            client.ShutdownRequested += () =>
                ExitEvent.Set();
            client.Start();

            Console.WriteLine(
                "NET462_HOST_READY device=NET462-PROBE port=" + port);
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                ExitEvent.Set();
            };
            ExitEvent.WaitOne();
            client.Dispose();
            return 0;
        }
    }
}
