namespace ProdToy.Setup;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool uninstallFlag = args.Any(a =>
            a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-u",          StringComparison.OrdinalIgnoreCase));

        if (uninstallFlag)
        {
            Application.Run(new UninstallForm());
            return;
        }

        // No args: show the install / repair / update form. The form decides its
        // mode internally by comparing the bundled version to AppRegistry.GetInstalledVersion().
        Application.Run(new SetupForm());
    }
}
