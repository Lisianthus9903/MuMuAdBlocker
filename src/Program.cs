namespace MuMuAdBlocker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Scheduled/CLI modes never construct the GUI or display modal dialogs.
        if (args.Length > 0)
        {
            try
            {
                if (args.Length != 1) return 64;
                switch (args[0])
                {
                    case "--smoke-test": return 0;
                    case "--guard-once": return GuardHost.RunOnceAsync().GetAwaiter().GetResult();
                    case "--install-guard":
                        GuardTask.Install(SettingsStore.Load());
                        return GuardHost.RunOnceAsync().GetAwaiter().GetResult();
                    case "--remove-guard": GuardTask.Disable(); return 0;
                    case "--restore-saved":
                        GuardTask.Disable();
                        new GuardStore().Log(GuardHost.RestoreSavedAsync().GetAwaiter().GetResult());
                        return new GuardStore().Load().Backups.Count == 0 ? 0 : 2;
                    default: return 64;
                }
            }
            catch (Exception ex)
            {
                try { new GuardStore().Log("CLI 실패: " + ex); } catch { }
                return 1;
            }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
