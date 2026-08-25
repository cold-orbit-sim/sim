using Godot;

namespace ColdOrbit.SimCore;

// Startup screen geometry for the three OS windows (see CLAUDE.md — main game
// view, ControlPanelsWindow, AdminPanelWindow; embed_subwindows=false makes
// them real separate windows). One shared source of the split so the two
// left-column windows and the game window can't drift out of alignment by a
// pixel or two from independently-rounded math.
//
// Layout: left column (Controls over Admin, each half the column height) is
// 25% of usable screen width; the game window fills the remaining 75% and the
// full height. Usable rect (not raw screen size) so this doesn't tuck a window
// under the macOS menu bar/dock or a Windows taskbar.
public static class WindowLayout
{
    private const float LeftColumnFraction = 0.25f;

    private static Rect2I UsableRect => DisplayServer.ScreenGetUsableRect(DisplayServer.GetPrimaryScreen());

    public static Rect2I MainGameRect()
    {
        var s = UsableRect;
        int leftW = (int)(s.Size.X * LeftColumnFraction);
        return new Rect2I(new Vector2I(s.Position.X + leftW, s.Position.Y),
                           new Vector2I(s.Size.X - leftW, s.Size.Y));
    }

    public static Rect2I ControlPanelsRect()
    {
        var s = UsableRect;
        int leftW = (int)(s.Size.X * LeftColumnFraction);
        int halfH = s.Size.Y / 2;
        return new Rect2I(s.Position, new Vector2I(leftW, halfH));
    }

    public static Rect2I AdminPanelRect()
    {
        var s = UsableRect;
        int leftW = (int)(s.Size.X * LeftColumnFraction);
        int halfH = s.Size.Y / 2;
        return new Rect2I(new Vector2I(s.Position.X, s.Position.Y + halfH),
                           new Vector2I(leftW, s.Size.Y - halfH));
    }

    // Positions the main OS window (id 0) — there's no Window node for it to
    // apply Position/Size to directly, so this goes through DisplayServer.
    public static void ApplyMainWindowLayout()
    {
        var r = MainGameRect();
        DisplayServer.WindowSetPosition(r.Position, (int)DisplayServer.MainWindowId);
        DisplayServer.WindowSetSize(r.Size, (int)DisplayServer.MainWindowId);
    }
}
