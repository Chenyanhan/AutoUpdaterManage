namespace AutoUpdater.Updater;

internal static class UpdaterProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        UpdaterOptions? options = null;
        try
        {
            options = UpdaterOptions.Parse(args);
            await using var logger = new FileUpdateLogger(options.LogPath);
            var reporter = new UdpResultReporter(options);
            var engine = new UpdateEngine(logger, reporter);
            await engine.ExecuteAsync(options);
            return 0;
        }
        catch (OptionsException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(UpdaterOptions.Usage);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (options is not null)
            {
                try
                {
                    await new UdpResultReporter(options).ReportAsync(false, ex.Message);
                }
                catch
                {
                }
            }
            return 1;
        }
    }
}
