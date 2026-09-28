using Forms = System.Windows.Forms;

namespace CodexVoiceAssistant.App;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayController(
        AssistantHost host,
        OrbWindow orb,
        Action shutdown)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(
            "显示语音栏",
            null,
            (_, _) =>
            {
                orb.Show();
            });
        menu.Items.Add(
            "停止当前回复",
            null,
            async (_, _) => await host.InterruptAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => shutdown());
        _icon = new Forms.NotifyIcon
        {
            Text = "Codex Voice Assistant",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) =>
        {
            orb.Show();
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
