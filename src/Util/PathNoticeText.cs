using System;
using System.Text;

namespace Vomplayer.Util;

// Copy for the in-window warning row shown when a file arrives with an unusable directory (see PathPortal.Diagnose). Pure so the wording can be tested relationally; the row itself is GTK.
public static class PathNoticeText
{
    public static string Compose(PathProblem problem, string logPath)
    {
        if (problem == null)
        {
            throw new ArgumentNullException(nameof(problem));
        }
        var sb = new StringBuilder();
        switch (problem.Kind)
        {
            case PathProblemKind.DocumentPortal:
                if (problem.Directory != null)
                {
                    sb.Append($"This file came through the document portal because the sandbox can't see its folder, {problem.Directory}. Next/previous and per-folder memory won't work for it. To grant access: flatpak override --user --filesystem={ShellQuote(problem.Directory)}:ro {AppIdentity.FlatpakId}");
                }
                else
                {
                    sb.Append("This file came through the document portal (the sandbox can't see its real folder) and the portal didn't report where it lives. Next/previous and per-folder memory won't work for it.");
                }
                break;
            case PathProblemKind.DocumentPortalOriginRejected:
                sb.Append($"This file came through the document portal, and although its folder {problem.Directory} is visible here, the file there isn't the one handed over (or isn't there), so the portal's own path is what's playing. Next/previous and per-folder memory won't work for it.");
                break;
            case PathProblemKind.DirectoryUnlistable:
                sb.Append($"The folder {problem.Directory} can't be listed, so next/previous and per-folder memory won't work for it.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(problem), problem.Kind, "unknown PathProblemKind");
        }
        sb.Append($" Details: {logPath}");
        return sb.ToString();
    }

    // Single-quote a path for pasting into a shell only when it contains something the shell would otherwise interpret; plain paths stay readable. The quoted path abuts the unquoted `--filesystem=` / `:ro` around it, which the shell concatenates into one word.
    private static string ShellQuote(string path)
    {
        foreach (var c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '/' || c == '.' || c == '_' || c == '-' || c == '+' || c == ':' || c == '@' || c == '%'))
            {
                return "'" + path.Replace("'", "'\\''") + "'";
            }
        }
        return path;
    }
}
