using System.Text;

namespace JsonMaster.Api.Services;

public class TextDiffService
{
    public class DiffResult
    {
        public List<FileLine> SourceLines { get; set; } = new();
        public List<FileLine> TargetLines { get; set; } = new();
        public int TotalDifferences { get; set; }
        public long SourceSize { get; set; }
        public long TargetSize { get; set; }
        public int TotalLines { get; set; }
    }

    public class FileLine
    {
        public int LineNumber { get; set; }
        public string Content { get; set; } = "";
        public bool IsDifferent { get; set; }
        public string ChangeType { get; set; } = ""; // "same", "modified", "added", "removed"
    }

    public class DiffSession
    {
        public string[] SourceLines { get; set; } = Array.Empty<string>();
        public string[] TargetLines { get; set; } = Array.Empty<string>();
        public int TotalDifferences { get; set; }
        public long SourceSize { get; set; }
        public long TargetSize { get; set; }
        public int TotalLines { get; set; }
    }

    // Heavy operation - run once per file pair
    public DiffSession InitializeSession(string source, string target)
    {
        var session = new DiffSession
        {
            SourceSize = source.Length,
            TargetSize = target.Length
        };

        // Split lines once
        session.SourceLines = source.Split('\n');
        session.TargetLines = target.Split('\n');

        int maxLines = Math.Max(session.SourceLines.Length, session.TargetLines.Length);
        session.TotalLines = maxLines;

        // Count differences once
        int totalDiffs = 0;
        for (int i = 0; i < maxLines; i++)
        {
            var srcLine = i < session.SourceLines.Length ? session.SourceLines[i].TrimEnd('\r') : null;
            var tgtLine = i < session.TargetLines.Length ? session.TargetLines[i].TrimEnd('\r') : null;

            if (srcLine != tgtLine)
            {
                totalDiffs++;
            }
        }
        session.TotalDifferences = totalDiffs;

        return session;
    }

    // Light operation - run on every scroll
    public DiffResult GetPage(DiffSession session, int startLine = 1, int lineCount = 100)
    {
        var result = new DiffResult
        {
            SourceSize = session.SourceSize,
            TargetSize = session.TargetSize,
            TotalLines = session.TotalLines,
            TotalDifferences = session.TotalDifferences
        };

        int endLine = Math.Min(startLine + lineCount - 1, session.TotalLines);

        for (int i = startLine - 1; i < endLine; i++)
        {
            var sourceLine = i < session.SourceLines.Length ? session.SourceLines[i].TrimEnd('\r') : null;
            var targetLine = i < session.TargetLines.Length ? session.TargetLines[i].TrimEnd('\r') : null;

            bool isDifferent = sourceLine != targetLine;
            string changeType = "same";

            if (sourceLine == null && targetLine != null)
            {
                changeType = "added";
            }
            else if (sourceLine != null && targetLine == null)
            {
                changeType = "removed";
            }
            else if (isDifferent)
            {
                changeType = "modified";
            }

            result.SourceLines.Add(new FileLine
            {
                LineNumber = i + 1,
                Content = TruncateLine(sourceLine ?? "", 1000),
                IsDifferent = isDifferent,
                ChangeType = changeType
            });

            result.TargetLines.Add(new FileLine
            {
                LineNumber = i + 1,
                Content = TruncateLine(targetLine ?? "", 1000),
                IsDifferent = isDifferent,
                ChangeType = changeType
            });
        }

        return result;
    }

    public DiffResult CompareText(string source, string target, int startLine = 1, int lineCount = 100)
    {
        // Legacy fallback - not efficient
        var session = InitializeSession(source, target);
        return GetPage(session, startLine, lineCount);
    }

    private string TruncateLine(string line, int maxLength)
    {
        if (line.Length <= maxLength)
            return line;
        
        return line.Substring(0, maxLength) + "... (truncated)";
    }
}
