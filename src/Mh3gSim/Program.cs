namespace Mh3gSim;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Mh3gSim.Core.DataLoadException exception)
        {
            MessageBox.Show($"データを読み込めませんでした。\n{exception.Message}", "MH3G スキルシミュレーター",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
