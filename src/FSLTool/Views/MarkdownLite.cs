using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace FSLTool.Views;

/// <summary>
/// Renders the small Markdown subset used by Docs/Help.md into a themed FlowDocument:
/// # / ## / ### headings (## go to the table of contents), paragraphs, "- " lists (nested by 2 spaces),
/// "| a | b |" tables, ``` code blocks, `code`, **bold**; &lt;!-- comments --&gt; are skipped.
/// </summary>
public static partial class MarkdownLite
{
    public sealed record Heading(string Text, Block Block);

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex InlineRx();

    public static FlowDocument Render(string markdown, out List<Heading> toc)
    {
        var res = Application.Current.Resources;
        Brush B(string key) => (Brush)res[key];
        var mono = (FontFamily)res["MonoFont"];

        var doc = new FlowDocument
        {
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 13.5,
            Foreground = B("Text"),
            Background = B("Bg"),
            PagePadding = new Thickness(32, 24, 32, 40),
            MaxPageWidth = 980,
            LineHeight = 21,
            TextAlignment = TextAlignment.Left,   // FlowDocument justifies by default — looks odd in narrow table cells
        };
        toc = new List<Heading>();

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var para = new List<string>();
        List? rootList = null;
        var listStack = new List<List>();   // current list per nesting level
        bool inComment = false;

        void FlushPara()
        {
            if (para.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            AddInlines(p.Inlines, string.Join(" ", para), B, mono);
            doc.Blocks.Add(p);
            para.Clear();
        }
        void FlushList()
        {
            rootList = null;
            listStack.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();

            // <!-- comments -->
            // a comment ends only on a line that ENDS with -->, so the text may mention the markers
            if (inComment) { if (line.EndsWith("-->")) inComment = false; continue; }
            if (line.TrimStart().StartsWith("<!--")) { inComment = !line.EndsWith("-->"); continue; }

            // ``` code block
            if (line.TrimStart().StartsWith("```"))
            {
                FlushPara(); FlushList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i].TrimEnd());
                var p = new Paragraph
                {
                    FontFamily = mono, FontSize = 12.5, LineHeight = 18,
                    Background = B("Surface2"), Padding = new Thickness(12, 10, 12, 10),
                    BorderBrush = B("Line"), BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 2, 0, 12),
                };
                for (int c = 0; c < code.Count; c++)
                {
                    if (c > 0) p.Inlines.Add(new LineBreak());
                    p.Inlines.Add(new Run(code[c]));
                }
                doc.Blocks.Add(p);
                continue;
            }

            // tables
            if (line.TrimStart().StartsWith('|'))
            {
                FlushPara(); FlushList();
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '))) continue;   // |---|---|
                    rows.Add(cells);
                }
                i--;
                doc.Blocks.Add(BuildTable(rows, B, mono));
                continue;
            }

            if (line.Length == 0) { FlushPara(); FlushList(); continue; }

            // headings
            var m = Regex.Match(line, @"^(#{1,3})\s+(.*)$");
            if (m.Success)
            {
                FlushPara(); FlushList();
                int level = m.Groups[1].Length;
                var text = m.Groups[2].Value;
                var h = new Paragraph(new Run(text))
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level switch { 1 => 26, 2 => 19, _ => 15 },
                    Margin = level switch { 1 => new Thickness(0, 0, 0, 12), 2 => new Thickness(0, 26, 0, 10), _ => new Thickness(0, 14, 0, 6) },
                    Foreground = level == 3 ? B("Accent") : B("Text"),
                };
                if (level == 2)
                {
                    h.BorderBrush = B("Line");
                    h.BorderThickness = new Thickness(0, 0, 0, 1);
                    h.Padding = new Thickness(0, 0, 0, 6);
                    toc.Add(new Heading(text, h));
                }
                doc.Blocks.Add(h);
                continue;
            }

            // list items
            var lm = Regex.Match(raw, @"^(\s*)- (.*)$");
            if (lm.Success)
            {
                FlushPara();
                int level = lm.Groups[1].Value.Length / 2;
                var item = new ListItem(new Paragraph { Margin = new Thickness(0, 0, 0, 3) });
                AddInlines(((Paragraph)item.Blocks.FirstBlock).Inlines, lm.Groups[2].Value.Trim(), B, mono);

                if (rootList == null)
                {
                    rootList = NewList(B);
                    doc.Blocks.Add(rootList);
                    listStack.Clear();
                    listStack.Add(rootList);
                }
                level = Math.Min(level, listStack.Count);   // never skip a level
                if (level == listStack.Count)
                {
                    // nested list under the previous item of the parent level
                    var parent = listStack[level - 1];
                    var sub = NewList(B);
                    sub.Margin = new Thickness(0, 2, 0, 2);
                    (parent.ListItems.LastListItem ?? AddEmpty(parent)).Blocks.Add(sub);
                    listStack.Add(sub);
                }
                else if (level < listStack.Count - 1) listStack.RemoveRange(level + 1, listStack.Count - level - 1);
                listStack[level].ListItems.Add(item);
                continue;
            }

            // continuation of a list item ("  text") or a plain paragraph line
            if (rootList != null && raw.StartsWith("  ") && listStack.Count > 0 &&
                listStack[^1].ListItems.LastListItem?.Blocks.LastBlock is Paragraph lp)
            {
                lp.Inlines.Add(new Run(" "));
                AddInlines(lp.Inlines, line.Trim(), B, mono);
                continue;
            }
            FlushList();
            para.Add(line.Trim());
        }
        FlushPara();
        return doc;
    }

    private static ListItem AddEmpty(List l)
    {
        var li = new ListItem();
        l.ListItems.Add(li);
        return li;
    }

    private static List NewList(Func<string, Brush> B) => new()
    {
        MarkerStyle = TextMarkerStyle.Disc,
        Margin = new Thickness(0, 0, 0, 10),
        Padding = new Thickness(22, 0, 0, 0),
    };

    private static Table BuildTable(List<string[]> rows, Func<string, Brush> B, FontFamily mono)
    {
        int cols = rows.Count == 0 ? 1 : rows.Max(r => r.Length);
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 14),
            BorderBrush = B("Line"),
            BorderThickness = new Thickness(1),
        };
        // width weights: longer content → wider column, but never extreme
        for (int c = 0; c < cols; c++)
        {
            int len = rows.Select(r => c < r.Length ? r[c].Length : 0).DefaultIfEmpty(1).Max();
            table.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(Math.Sqrt(len), 2.5, 9), GridUnitType.Star) });
        }
        var group = new TableRowGroup();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) { row.Background = B("Surface2"); row.FontWeight = FontWeights.SemiBold; }
            for (int c = 0; c < cols; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInlines(p.Inlines, c < rows[r].Length ? rows[r][c] : "", B, mono);
                row.Cells.Add(new TableCell(p)
                {
                    Padding = new Thickness(10, 6, 10, 6),
                    BorderBrush = B("Line"),
                    BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0),
                });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>`code` and **bold** inside a line.</summary>
    private static void AddInlines(InlineCollection target, string text, Func<string, Brush> B, FontFamily mono)
    {
        foreach (var part in InlineRx().Split(text))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                target.Add(new Bold(new Run(part[2..^2])));
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                target.Add(new Run(part[1..^1]) { FontFamily = mono, FontSize = 12.5, Background = B("Surface2") });
            else
                target.Add(new Run(part));
        }
    }
}
