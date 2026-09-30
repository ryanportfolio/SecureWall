using System.Diagnostics;
using pylorak.TinyWall;

namespace SecureWall.Core.Tests;

internal static class ControllerUiPortTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("copy path is offered only for real subjects", CopyPathIsOfferedOnlyForRealSubjects),
        ("open folder accepts only drive and UNC paths", OpenFolderAcceptsOnlyDriveAndUncPaths),
        ("open folder selects an existing file without executing it", OpenFolderSelectsExistingFile),
        ("open folder falls back to the folder when the file is missing", OpenFolderFallsBackToFolder),
        ("controller and connections UI wiring matches the upstream ports", ControllerUiWiring),
    };

    private const string WindowsDirectory = @"C:\Windows";

    private static void CopyPathIsOfferedOnlyForRealSubjects()
    {
        AssertEx.False(ProcessPathActions.CanCopyPath(null));
        AssertEx.False(ProcessPathActions.CanCopyPath(string.Empty));
        AssertEx.False(ProcessPathActions.CanCopyPath("  "));
        AssertEx.False(ProcessPathActions.CanCopyPath("System"));
        AssertEx.True(ProcessPathActions.CanCopyPath(@"C:\Apps\app.exe"));
        AssertEx.True(ProcessPathActions.CanCopyPath(@"\device\harddiskvolume3\apps\app.exe"));
    }

    private static void OpenFolderAcceptsOnlyDriveAndUncPaths()
    {
        AssertEx.True(ProcessPathActions.IsFileSystemPath(@"C:\Apps\app.exe"));
        AssertEx.True(ProcessPathActions.IsFileSystemPath(@"d:\app.exe"));
        AssertEx.True(ProcessPathActions.IsFileSystemPath(@"\\server\share\app.exe"));

        AssertEx.False(ProcessPathActions.IsFileSystemPath(null));
        AssertEx.False(ProcessPathActions.IsFileSystemPath("System"));
        AssertEx.False(ProcessPathActions.IsFileSystemPath("app.exe"));
        AssertEx.False(ProcessPathActions.IsFileSystemPath(@"C:app.exe"));
        AssertEx.False(ProcessPathActions.IsFileSystemPath(@"\device\harddiskvolume3\apps\app.exe"));
        AssertEx.False(ProcessPathActions.IsFileSystemPath(@"\\?\C:\Apps\app.exe"));
        AssertEx.False(ProcessPathActions.IsFileSystemPath(@"\\.\pipe\name"));
        AssertEx.False(ProcessPathActions.IsFileSystemPath("C:\\Apps\\a\" & calc.exe"));
        AssertEx.False(ProcessPathActions.IsFileSystemPath("C:\\Apps\\a\r\nb.exe"));

        bool probed = false;
        AssertEx.Equal<ProcessStartInfo?>(null, ProcessPathActions.CreateOpenFolderStartInfo(
            "System", WindowsDirectory, _ => probed = true, _ => probed = true));
        AssertEx.False(probed, "Rejected paths must not reach the file system.");
    }

    private static void OpenFolderSelectsExistingFile()
    {
        const string file = @"C:\Program Files\My App\app, v2.exe";
        var psi = ProcessPathActions.CreateOpenFolderStartInfo(file, WindowsDirectory, p => p == file, _ => true);

        AssertEx.True(psi != null);
        AssertEx.Equal(@"C:\Windows\explorer.exe", psi!.FileName);
        AssertEx.Equal("/select,\"" + file + "\"", psi.Arguments);
        AssertEx.False(psi.UseShellExecute);
        AssertEx.Equal(string.Empty, psi.Verb);
    }

    private static void OpenFolderFallsBackToFolder()
    {
        const string file = @"C:\Apps\gone.exe";
        var psi = ProcessPathActions.CreateOpenFolderStartInfo(file, WindowsDirectory, _ => false, d => d == @"C:\Apps");

        AssertEx.True(psi != null);
        AssertEx.Equal(@"C:\Apps", psi!.FileName);
        AssertEx.Equal("explore", psi.Verb, "Only the folder-only verb may be used on a path that was not verified as a file.");
        AssertEx.True(psi.UseShellExecute);

        AssertEx.Equal<ProcessStartInfo?>(null, ProcessPathActions.CreateOpenFolderStartInfo(
            file, WindowsDirectory, _ => false, _ => false));
    }

    private static void ControllerUiWiring()
    {
        string init = Section(Read("TinyWallController.cs"), "private void InitController()", "LoadDatabase();");
        AssertEx.True(init.Contains("Utils.DisableMessageUIPI(\"TaskbarCreated\");"),
            "The elevated controller must accept TaskbarCreated so the tray icon returns after Explorer restarts.");

        string utils = Read("Utils.cs");
        string uipi = Section(utils, "public static bool DisableMessageUIPI(", "public static T OnlyFirst");
        AssertEx.True(uipi.Contains("RegisterWindowMessage(msg)")
            && uipi.Contains("ChangeWindowMessageFilter(msgId, UnsafeNativeMethods.ChangeWindowMessageFilterFlags.Add)"));

        string connections = Read("ConnectionsForm.cs");
        AssertEx.False(connections.Contains("processlibrary", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(Read("ConnectionsForm.Designer.cs").Contains("mnuProcessLibrary"));
        AssertEx.False(connections.Contains("Utils.StartProcess("), "Process handles must be disposed.");
        string opening = Section(connections, "private void contextMenuStrip1_Opening(", "private void mnuCloseProcess_Click(");
        AssertEx.True(opening.Contains("RefreshTimer.Stop();") && opening.Contains("RefreshTimer.Start();"),
            "The one-second refresh must not clear the selection while the context menu is open.");
        string openFolder = Section(connections, "private void mnuOpenFolder_Click(", "private void ConnectionsForm_KeyDown(");
        AssertEx.True(openFolder.Contains("ProcessPathActions.CreateOpenFolderStartInfo("));

        AssertEx.True(Read("AppFinderForm.Designer.cs").Contains("this.CancelButton = this.btnCancel;"));
    }

    private static string Read(string file)
    {
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TinyWall", "Utils.cs"))) root = root.Parent;
        AssertEx.True(root != null);
        return File.ReadAllText(Path.Combine(root!.FullName, "TinyWall", file));
    }

    private static string Section(string text, string begin, string end)
    {
        int start = text.IndexOf(begin, StringComparison.Ordinal);
        AssertEx.True(start >= 0, "Missing " + begin);
        int stop = text.IndexOf(end, start, StringComparison.Ordinal);
        AssertEx.True(stop > start, "Missing " + end);
        return text.Substring(start, stop - start);
    }
}
