namespace Aip.Viewer;

internal static class SolutionPaths
{
    // Configuration file lookup — identical logic to Aip.Host, so the Creator and Viewer always agree
    // on where appsettings.json lives regardless of which directory either one is launched from.
    internal static string? FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Aip.slnx").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
