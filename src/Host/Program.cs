using System;
using System.Windows.Forms;

namespace Fast;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 3 && args[0].Equals("--inject-direct", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                int pid = int.Parse(args[1]);
                Environment.Exit(Injector.InjectDirect(pid, args[2]) ? 0 : 1);
            }
            catch (Exception ex)
            {
                Log.Write($"Helper injection failed: {ex.Message}");
                Environment.Exit(1);
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
