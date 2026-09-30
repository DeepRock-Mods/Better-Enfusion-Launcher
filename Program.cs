namespace ReforgerHub;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // ReforgerHub.exe --open <GUID or name>: straight into Workbench, no window (project shortcuts)
        if (args.Length >= 2 && args[0] == "--open")
        {
            OpenDirect(string.Join(" ", args.Skip(1)));
            return;
        }

        // ReforgerHub.exe --demo <file.png>: example data, a screenshot of the window, then exit (README picture)
        if (args.Length >= 2 && args[0] == "--demo")
        {
            Application.Run(new MainForm(Demo.Create(), Path.GetFullPath(args[1])));
            return;
        }

        Application.Run(new MainForm());
    }

    private static void OpenDirect(string project)
    {
        try
        {
            var store = new Store();
            store.Load();
            var found = Launcher.Find(Scanner.Scan(store.Settings), project)
                        ?? throw new Exception($"No project \"{project}\" in your addon folders.");
            Launcher.OpenWorkbench(store, found);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Reforger Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
