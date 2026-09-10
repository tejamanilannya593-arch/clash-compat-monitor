using System.Drawing;
using System.Windows.Forms;

public static class AppIcon
{
    private static readonly Icon current = LoadAssociatedIcon();

    public static Icon Current { get { return current; } }

    private static Icon LoadAssociatedIcon()
    {
        Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        return icon ?? SystemIcons.Application;
    }
}
