using MicPtt.UI;

namespace MicPtt;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--diagnose")
        {
            Diagnostics.WriteReport(args[1], false);
            return;
        }
        if (args.Length == 2 && args[0] == "--test-mute")
        {
            Diagnostics.WriteReport(args[1], true);
            return;
        }

        using var instance = new Mutex(true, "Local\\MicKey.Desktop.Instance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("MicKey가 이미 실행 중입니다. 작업 표시줄의 트레이 아이콘을 더블클릭하세요.",
                "MicKey", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        Application.ThreadException += (_, e) => form.HandleUnexpectedError(e.Exception);
        Application.Run(form);
    }
}
